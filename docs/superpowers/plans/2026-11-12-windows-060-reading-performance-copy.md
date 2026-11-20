# 轻阅 Windows 0.6.0 阅读性能与跨应用复制 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将 Windows 客户端升级为 0.6.0，使带图片 Markdown 打开、切换、缩放和返回预览时保持响应，并让复制内容在浏览器、腾讯文档、Word/WPS、邮件编辑器和纯文本编辑器中尽量保留结构与图片。

**Architecture:** 保留现有 WPF `FlowDocument`、`ReadingModel`、`MarkdownPreview` 和原生剪贴板架构。真实窗口统一使用可取消的异步 Markdown 构造；图片文件读取和解码继续在后台执行，由受限调度器按首屏/视口优先发布冻结位图。复制先在 UI 线程冻结选区和图片快照，再从同一份结构化快照生成 UnicodeText、HTML、RTF 和 XamlPackage，最后一次性发布 `DataObject`。

**Tech Stack:** .NET 10、WPF、C#、`FlowDocument`、Windows Clipboard/CF_HTML、现有 NSIS 安装器、PowerShell 回归脚本。

**Spec:** `docs/superpowers/specs/2026-11-12-windows-060-reading-performance-copy-design.md`

## Global Constraints

- 只修改 Windows 工程和 Windows 验收资料；不修改 Mac 工程。
- 不引入 WebView、网络图片请求、脚本执行或第三方运行时。
- 继续支持 50 万正文字符上限；超过资源预算时显示正文和明确图片占位。
- 图片缓存只存在当前进程内，不写用户目录，不改变原 Markdown、会话或用户资料格式。
- 任何读取、解码、Base64 转换和图片头检查不得在 Dispatcher 上执行。
- 所有异步请求必须同时校验取消令牌、当前标签、文本/路径/类型和生成号。
- 不提交用户真实 Markdown、图片、路径、临时发布目录、安装包和运行日志。
- 安装器页面、默认保留资料和彻底卸载策略本版只做版本同步，不重新设计。

---

### Task 1: 固化失败回归并统一真实窗口的 Markdown 异步入口

**Files:**
- Modify: `开发空间/Windows/src/Qingyue.Windows/MainWindow.Operations.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/MainWindow.Reading.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/MainWindow.Markdown.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/ReadingRenderer.Markdown.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/EditorTab.cs`
- Test: `开发空间/Windows/tests/Qingyue.ReadingTests/Program.cs`
- Test: `开发空间/Windows/tests/Qingyue.ReaderExperienceTests/Program.cs`
- Test: `开发空间/Windows/tests/Qingyue.ImageTests/Program.cs`

**Interfaces:**
- Consumes: 现有 `EnterReadingAsync`、`EnterReading`、`PublishReading`、`MarkdownPreview.Update` 和 `RenderedReading.MarkdownAsync`。
- Produces: `MarkdownRenderRequest`、按标签保存的 Markdown 取消源和生成号校验；真实窗口调用异步入口，纯离屏测试继续保留同步工厂。

- [ ] **Step 1: 写出旧同步路径和过期发布的失败测试**

在阅读体验测试中增加以下可观察断言：

```csharp
var old = await RenderedReading.MarkdownAsync(model, oldText, null, 16, 680, first.Token);
first.Cancel();
var current = await RenderedReading.MarkdownAsync(model, newText, null, 16, 680, second.Token);
Assert(Text(current.Document).Contains("新正文"), "当前请求必须保留最新正文");
Assert(!Text(current.Document).Contains("旧正文"), "旧请求不能覆盖新请求");
```

在 `MainWindow` 流程测试中通过现有测试宿主验证：切换标签、关闭标签、返回编辑和外部刷新后，旧 `RenderedReading` 不会再次赋给 `tab.Preview.Document`。

运行：

```powershell
dotnet run --project tests/Qingyue.ReadingTests/Qingyue.ReadingTests.csproj -c Release --no-restore
dotnet run --project tests/Qingyue.ReaderExperienceTests/Qingyue.ReaderExperienceTests.csproj -c Release --no-restore
dotnet run --project tests/Qingyue.ImageTests/Qingyue.ImageTests.csproj -c Release --no-restore
```

预期：新增测试在当前实现中至少能捕捉真实窗口同步入口或过期发布问题；已有离屏同步测试继续通过。

- [ ] **Step 2: 增加统一请求身份**

在 `MainWindow.Operations.cs` 定义窗口内部请求模型：

```csharp
private sealed record MarkdownRenderRequest(
    EditorTab Tab,
    string Text,
    string? SourcePath,
    TextKind Kind,
    double FontSize,
    double ReadingWidth,
    long Generation);
```

增加窗口字段：

```csharp
private CancellationTokenSource? markdownRenderCancellation;
private long markdownRenderGeneration;
```

创建请求时取消并释放上一个 `markdownRenderCancellation`，递增 `markdownRenderGeneration`，并将文本、路径、类型和排版参数冻结在请求中。请求发布前使用一个方法完成全部检查：

```csharp
private bool IsCurrentMarkdownRequest(MarkdownRenderRequest request, ExternalFileRefresh expected)
{
    return !request.Tab.IsClosed
        && tabs.Contains(request.Tab)
        && ReferenceEquals(CurrentTab, request.Tab)
        && request.Generation == markdownRenderGeneration
        && request.Tab.Kind == request.Kind
        && request.Tab.File.Text == request.Text
        && request.Tab.File.Path == request.SourcePath
        && expected.IsCurrent
        && !approvedShutdown
        && !exitInProgress;
}
```

实际字段名若与当前 `EditorTab` 关闭状态不同，使用现有标签集合和标签引用校验实现同等条件，不新增会话字段。

- [ ] **Step 3: 删除真实窗口同步 Markdown 构造**

将 `MainWindow.Reading.cs` 中 `ToggleReading` 的新建路径改为调用现有异步 `EnterReadingAsync`，保留 `EnterReading` 只用于测试或已确认可复用的已构造对象。`ToggleReadingAsync` 负责记录 `EditingViewportAnchor`，等待异步结果后再恢复一次位置。

将 `MainWindow.Operations.cs` 中的构造改为：

```csharp
var request = BeginMarkdownRenderRequest(tab, text, path, kind);
var model = await Task.Run(() => ReadingModel.Build(request.Text, request.Kind), operation.Token)
    .WaitAsync(operation.Token);
var rendered = request.Kind == TextKind.Markdown
    ? await RenderedReading.MarkdownAsync(model, request.Text, request.SourcePath,
        request.FontSize, request.ReadingWidth, operation.Token)
    : new RenderedReading(model, sourcePath: request.SourcePath);
operation.Token.ThrowIfCancellationRequested();
if (!IsCurrentMarkdownRequest(request, expected)) return false;
PublishReading(request.Tab, model, rendered, request.Text, request.SourcePath,
    request.Kind, anchor, focus);
```

`RenderMarkdownAsync` 必须在所有返回分支调用 `Dispose`/取消旧的 `markdownRenderCancellation`，避免标签关闭后继续持有大型 FlowDocument。

- [ ] **Step 4: 将分屏预览更新改为可取消异步更新**

在 `MainWindow.Markdown.cs` 增加专用的 `markdownPreviewCancellation` 和 `markdownPreviewGeneration`。防抖计时器只提交文本快照：

```csharp
private async Task UpdateMarkdownPreviewAsync(EditorTab tab, string text, long generation,
    CancellationToken token)
{
    var model = await Task.Run(() => ReadingModel.Build(text, TextKind.Markdown), token)
        .WaitAsync(token);
    var document = await MarkdownPreview.BuildDocumentAsync(model, text, tab.File.Path,
        settings.FontSize + 1, settings.ReadingWidth, token);
    await Dispatcher.InvokeAsync(() =>
    {
        if (token.IsCancellationRequested || generation != markdownPreviewGeneration
            || CurrentTab != tab || tab.IsReading || tab.File.Text != text) return;
        markdownPreview!.Document = document;
        markdownText = text;
    }, DispatcherPriority.Background, token);
}
```

对于同一标签的快速输入，只发布最后一个生成号；取消时保留旧预览，直到新预览成功，失败时显示当前原文的可读降级文档。

- [ ] **Step 5: 运行定向测试并提交**

运行：

```powershell
dotnet run --project tests/Qingyue.ReadingTests/Qingyue.ReadingTests.csproj -c Release --no-restore
dotnet run --project tests/Qingyue.ReaderExperienceTests/Qingyue.ReaderExperienceTests.csproj -c Release --no-restore
dotnet run --project tests/Qingyue.ImageTests/Qingyue.ImageTests.csproj -c Release --no-restore
```

预期：异步构造、标签切换、取消和离屏同步兼容检查通过；失败时先修复入口和生成号校验，再继续下一任务。

```powershell
git add '开发空间/Windows/src/Qingyue.Windows/MainWindow.Operations.cs' `
  '开发空间/Windows/src/Qingyue.Windows/MainWindow.Reading.cs' `
  '开发空间/Windows/src/Qingyue.Windows/MainWindow.Markdown.cs' `
  '开发空间/Windows/src/Qingyue.Windows/ReadingRenderer.Markdown.cs' `
  '开发空间/Windows/src/Qingyue.Windows/EditorTab.cs' `
  '开发空间/Windows/tests/Qingyue.ReadingTests/Program.cs' `
  '开发空间/Windows/tests/Qingyue.ReaderExperienceTests/Program.cs' `
  '开发空间/Windows/tests/Qingyue.ImageTests/Program.cs'
git commit -m "feat(windows): unify markdown async rendering"
```

---

### Task 2: 增加图片缓存、可见区域调度和可取消资源预算

**Files:**
- Create: `开发空间/Windows/src/Qingyue.Windows/MarkdownImageCache.cs`
- Create: `开发空间/Windows/src/Qingyue.Windows/MarkdownImageScheduler.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/ReadingRenderer.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/MarkdownPreview.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/ReaderImageWindow.cs`
- Modify: `开发空间/Windows/tests/Qingyue.ImageTests/Qingyue.ImageTests.csproj`
- Modify: `开发空间/Windows/tests/Qingyue.ImageTests/Program.cs`
- Modify: `开发空间/Windows/tests/Qingyue.ReaderExperienceTests/Program.cs`

**Interfaces:**
- Consumes: 现有 `MarkdownImageLoader.LoadAsync`、`MarkdownImageView`、`MarkdownImageBudget`、WPF 宿主滚动事件。
- Produces:

```csharp
internal readonly record struct MarkdownImageCacheKey(
    string Identity, long Length, long LastWriteTicks, int DecodeWidth, int DecodeHeight);

internal sealed class MarkdownImageCache
{
    internal bool TryGet(MarkdownImageCacheKey key, out BitmapSource bitmap);
    internal void Put(MarkdownImageCacheKey key, BitmapSource bitmap, long bytes, long pixels);
    internal void Clear();
}

internal sealed class MarkdownImageScheduler : IDisposable
{
    internal void Attach(RichTextBox host);
    internal void RefreshViewport();
    internal void CancelPending();
    internal void Dispose();
}
```

- [ ] **Step 1: 增加缓存和预算测试**

在 `Qingyue.ImageTests` 增加合成图片测试：

```csharp
var key = MarkdownImageCacheKey.ForFile(path, width: 4096, height: 0);
cache.Put(key, bitmap, 1024, 100);
Assert(cache.TryGet(key, out var cached) && ReferenceEquals(cached, bitmap),
    "同一文件签名和解码尺寸必须命中缓存");
File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));
Assert(!cache.TryGet(MarkdownImageCacheKey.ForFile(path, 4096, 0), out _),
    "文件变化不能复用旧位图");
```

补充：容量淘汰、重复引用只解码一次、取消后不发布、64 MiB/4800 万像素超限仍生成明确占位。

- [ ] **Step 2: 实现受限 LRU 缓存**

`MarkdownImageCache` 只保存冻结 `BitmapSource` 和字节/像素计数；使用 `Dictionary<MarkdownImageCacheKey, LinkedListNode<Entry>>` 和 `LinkedList<Entry>`，总容量超过 64 MiB 或 48,000,000 解码像素时从尾部淘汰。

文件键必须读取规范化路径、长度、最后写入时间和目标尺寸。读取文件签名失败时直接跳过缓存，不能把错误状态缓存为成功位图。

- [ ] **Step 3: 把加载器改为缓存感知的纯异步服务**

保留 `MarkdownImageLoader` 的本地路径白名单、PNG data URI、单图 16 MiB、单图 3200 万像素和共享预算检查。将加载接口改为接受 `MarkdownImageCache`：

```csharp
internal static Task<MarkdownImageResult> LoadAsync(
    MarkdownImageReference reference,
    string? sourcePath,
    MarkdownImageBudget budget,
    MarkdownImageCache cache,
    CancellationToken cancellation);
```

顺序固定为：解析本地身份 → 尝试缓存 → `DecodeSlots` 限流 → 后台读取/解码 → `Freeze` → 缓存 → 返回。缓存命中不占用解码槽，不重复读取完整图片。

- [ ] **Step 4: 用视口调度替换 `Loaded` 全量启动**

`MarkdownImageScheduler` 维护当前 `MarkdownPreview`/阅读宿主中的图片视图，监听 `ScrollChanged`、`SizeChanged`、`Loaded` 和 `Unloaded`。使用 `TransformToAncestor(host)` 计算图片相对视口位置，优先加载视口上下各一屏范围内图片；坐标不可用时只顺序预加载固定数量。

`MarkdownImageView` 改成由调度器调用 `LoadIfNeededAsync`，`Loaded` 只注册宿主和请求一次 `RefreshViewport`，不再将每张图片的 `Loaded` 作为立即解码触发器。图片离开当前文档或当前请求生成号变化时调用取消，并在完成后检查视图仍属于当前宿主。

- [ ] **Step 5: 保持图片完成后的版式和位置稳定**

图片显示前保留固定的最小占位高度或按图片元数据计算占位比例；显示成功只更新当前 `MarkdownImageView` 的 `Image.Source`、尺寸和状态，不替换整个 `FlowDocument`。刷新后调用现有 `UpdateReadingWidth`/`UpdatePresentation`，并通过 `ReadingViewportState` 恢复源文锚点一次。

图片查看窗口从缓存或已冻结位图读取，不重新打开原文件；缓存淘汰后仍允许查看窗口持有的冻结引用，窗口关闭后由 GC 回收。

- [ ] **Step 6: 运行图片和阅读体验回归并提交**

运行：

```powershell
dotnet run --project tests/Qingyue.ImageTests/Qingyue.ImageTests.csproj -c Release --no-restore
dotnet run --project tests/Qingyue.ReaderExperienceTests/Qingyue.ReaderExperienceTests.csproj -c Release --no-restore
```

预期：大量图片首屏不超过调度预算；滚动后继续加载；取消、缩放、标签切换、外部图片更新时间正确；正文、搜索选区和视口不被图片完成回调覆盖。

```powershell
git add '开发空间/Windows/src/Qingyue.Windows/MarkdownImageCache.cs' `
  '开发空间/Windows/src/Qingyue.Windows/MarkdownImageScheduler.cs' `
  '开发空间/Windows/src/Qingyue.Windows/ReadingRenderer.cs' `
  '开发空间/Windows/src/Qingyue.Windows/MarkdownPreview.cs' `
  '开发空间/Windows/src/Qingyue.Windows/ReaderImageWindow.cs' `
  '开发空间/Windows/tests/Qingyue.ImageTests' `
  '开发空间/Windows/tests/Qingyue.ReaderExperienceTests/Program.cs'
git commit -m "feat(windows): schedule markdown images by viewport"
```

---

### Task 3: 统一复制快照并完善第三方接收格式

**Files:**
- Create: `开发空间/Windows/src/Qingyue.Windows/ReadingClipboardSnapshot.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/ReadingClipboard.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/ReadingHtmlExport.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/MainWindow.Clipboard.cs`
- Modify: `开发空间/Windows/tests/Qingyue.ClipboardTests/SharingChecks.cs`
- Modify: `开发空间/Windows/tests/Qingyue.ClipboardTests/Program.cs`
- Modify: `开发空间/Windows/tests/Qingyue.ReaderExperienceTests/Program.cs`

**Interfaces:**
- Consumes: 当前 `ReadingClipboard.Export`、`ReadingHtmlExport`、`ReadingClipboardResult` 和 `DataObject` 发布流程。
- Produces:

```csharp
internal sealed record ReadingClipboardSnapshot(
    string PlainText,
    string HtmlFragment,
    string? Rtf,
    MemoryStream? XamlPackage,
    ReadingImageReport Images);

internal static ReadingClipboardSnapshot? CreateSnapshot(
    FlowDocument document,
    TextRange range,
    CancellationToken cancellationToken = default);
```

- [ ] **Step 1: 先补 CF_HTML 和格式一致性失败测试**

在 `Qingyue.ClipboardTests` 增加一个包含中文、Emoji、空表格单元格、标题、列表、引用、代码块和内嵌 PNG 的结构化样本，检查：

```csharp
var snapshot = ReadingClipboardSnapshotFactory.CreateSnapshot(document, range)!;
var html = snapshot.HtmlFragment;
Assert(html.Contains("data:image/png;base64,", StringComparison.Ordinal),
    "HTML 图片必须自包含");
Assert(!html.Contains("file:", StringComparison.OrdinalIgnoreCase)
    && !html.Contains(sourcePath, StringComparison.Ordinal),
    "HTML 不得泄露原文路径");
Assert(Encoding.UTF8.GetByteCount(snapshot.PlainText) > 0, "纯文本不能为空");
```

新增 `BuildCfHtml` 测试验证 `StartHTML`、`EndHTML`、`StartFragment`、`EndFragment` 均按 UTF-8 字节偏移指向真实片段，前导偏移位数固定为十位。

- [ ] **Step 2: 抽取同一份导出快照**

从 `ReadingClipboard.Export` 中抽出结构化遍历结果：元素白名单、文本预算、图片状态、PNG 编码结果和图片报告只执行一次。HTML、RTF 和 XamlPackage 都从同一份结果生成，不能分别重新遍历活动 WPF 控件。

图片只使用已冻结位图并重新编码 PNG；未完成、失败、不支持和超限图片输出文字占位，并在 `ReadingImageReport` 中记录具体状态。

- [ ] **Step 3: 统一全文、选区、段落、表格和代码块**

`CreateFullDocumentData`、复制选区的 WPF `Copying` 事件、`CreateCurrentTableData` 和 `CreateCurrentCodeBlockData` 都调用相同的 `CreateSnapshot`。发布 `DataObject` 时写入：

```csharp
data.SetData(DataFormats.UnicodeText, snapshot.PlainText, false);
data.SetData(DataFormats.Text, snapshot.PlainText, false);
data.SetData(DataFormats.Html, ReadingClipboard.BuildCfHtml(snapshot.HtmlFragment), false);
if (snapshot.Rtf is not null) data.SetData(DataFormats.Rtf, snapshot.Rtf, false);
if (snapshot.XamlPackage is not null)
    data.SetData(DataFormats.XamlPackage, snapshot.XamlPackage, false);
```

复制前冻结文档引用、选区起止位置、文本变化版本和图片视图集合；生成期间发现文档替换、正文变化或请求取消时抛出可读错误，不发布半成品。

- [ ] **Step 4: 复用导出 HTML 片段**

`ReadingHtmlExport.CreateDocument` 只负责文档头、标题、白名单 CSS 和原子写入，正文片段和图片报告直接使用统一快照。继续保留 Content-Security-Policy、`data:` 图片、原子写入和原文路径保护。

- [ ] **Step 5: 验证多接收方格式边界**

生成层测试至少覆盖：

- Chromium `contenteditable` 预期读取 HTML；
- Word/WPS 预期优先读取 RTF 或 HTML；
- 腾讯文档预期读取 HTML；
- 记事本/纯文本输入框预期读取 UnicodeText；
- 只允许图片的接收方预期读取 Bitmap 兜底（仅单图片选区时）。

第三方接收方实际测试在可用的本机 Windows 应用中执行；未安装的应用记录“未验收”，不把未安装误判为代码失败。

- [ ] **Step 6: 运行复制测试并提交**

运行：

```powershell
dotnet run --project tests/Qingyue.ClipboardTests/Qingyue.ClipboardTests.csproj -c Release --no-restore
dotnet run --project tests/Qingyue.ReaderExperienceTests/Qingyue.ReaderExperienceTests.csproj -c Release --no-restore
```

预期：纯文本逐字一致；CF_HTML UTF-8 偏移正确；HTML 不含原路径；图片关闭原文件后仍可粘贴；RTF/XamlPackage 与 HTML 使用同一图片报告。

```powershell
git add '开发空间/Windows/src/Qingyue.Windows/ReadingClipboardSnapshot.cs' `
  '开发空间/Windows/src/Qingyue.Windows/ReadingClipboard.cs' `
  '开发空间/Windows/src/Qingyue.Windows/ReadingHtmlExport.cs' `
  '开发空间/Windows/src/Qingyue.Windows/MainWindow.Clipboard.cs' `
  '开发空间/Windows/tests/Qingyue.ClipboardTests' `
  '开发空间/Windows/tests/Qingyue.ReaderExperienceTests/Program.cs'
git commit -m "feat(windows): unify reading clipboard snapshots"
```

---

### Task 4: 处理视口锚点、缩放、搜索、编辑往返和外部更新

**Files:**
- Create: `开发空间/Windows/src/Qingyue.Windows/ReadingViewportState.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/MainWindow.Reading.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/MainWindow.Markdown.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/MainWindow.ExternalChanges.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/MainWindow.Search.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/EditorTab.cs`
- Modify: `开发空间/Windows/tests/Qingyue.ExternalChangesTests/RegressionTests.cs`
- Modify: `开发空间/Windows/tests/Qingyue.NavigationTests/Program.cs`
- Modify: `开发空间/Windows/tests/Qingyue.ReaderExperienceTests/Program.cs`

**Interfaces:**
- Consumes: 现有 `ViewportAnchor`、`ViewportDelta`、`CurrentSourceAnchor`、搜索选区和外部文件刷新。
- Produces:

```csharp
internal sealed record ReadingViewportState(
    int SourceAnchor,
    int SourceSelectionStart,
    int SourceSelectionEnd,
    double VerticalOffset,
    double HorizontalOffset,
    long Generation);
```

- [ ] **Step 1: 写位置恢复回归**

覆盖以下顺序：

```csharp
打开 Markdown -> 滚动到中段 -> 搜索并高亮 -> 缩放窗口
-> 图片异步完成 -> 切换编辑 -> 返回预览
-> 外部文件更新 -> 继续阅读
```

每一步断言源文锚点仍在同一块，搜索高亮范围未被图片完成回调清空，用户主动滚动后旧恢复回调不再抢占视口。

- [ ] **Step 2: 保存源文锚点和选区**

在进入重排、编辑往返、外部刷新和窗口布局变化前生成 `ReadingViewportState`。恢复顺序固定为：源文块 → 源文选区 → 布局完成回调 → 受限的垂直/水平偏移兜底。

恢复回调带生成号，回调执行前检查当前标签、当前文档、用户是否已经滚动/选择/编辑；任意条件不满足就丢弃回调。

- [ ] **Step 3: 将缩放和版心变化改为原地重排**

继续使用 `RenderedReading.UpdatePresentation` 和 `MarkdownPreview.UpdatePresentation`，禁止仅因窗口大小、侧栏、字号变化而重新读取图片或重建全文。图片调度器只更新视口优先级，缓存命中不重新解码。

- [ ] **Step 4: 外部文件更新接入新的请求生成号**

`MainWindow.ExternalChanges.cs` 接纳磁盘新版本前，取消当前阅读/分屏请求并记录源文锚点；新内容发布后使用新的生成号、图片引用和搜索映射。存在未保存编辑时保留本地内容并提示冲突，不能用外部内容覆盖本地编辑。

- [ ] **Step 5: 搜索高亮保持**

搜索结果存源文起止位置，预览重建后用 `RenderedReading.PointerAt` 映射当前显示位置；匹配不到时清空高亮。搜索高亮更新不得触发完整图片重载或整篇文档复制。

- [ ] **Step 6: 运行导航和外部变化回归并提交**

运行：

```powershell
dotnet run --project tests/Qingyue.ExternalChangesTests/Qingyue.ExternalChangesTests.csproj -c Release --no-restore
dotnet run --project tests/Qingyue.NavigationTests/Qingyue.NavigationTests.csproj -c Release --no-restore
dotnet run --project tests/Qingyue.ReaderExperienceTests/Qingyue.ReaderExperienceTests.csproj -c Release --no-restore
```

预期：缩放/侧栏/图片完成不跳顶；编辑/预览往返可恢复；搜索自动高亮；外部更新只接纳当前标签和当前请求的新结果。

```powershell
git add '开发空间/Windows/src/Qingyue.Windows/ReadingViewportState.cs' `
  '开发空间/Windows/src/Qingyue.Windows/MainWindow.Reading.cs' `
  '开发空间/Windows/src/Qingyue.Windows/MainWindow.Markdown.cs' `
  '开发空间/Windows/src/Qingyue.Windows/MainWindow.ExternalChanges.cs' `
  '开发空间/Windows/src/Qingyue.Windows/MainWindow.Search.cs' `
  '开发空间/Windows/src/Qingyue.Windows/EditorTab.cs' `
  '开发空间/Windows/tests/Qingyue.ExternalChangesTests' `
  '开发空间/Windows/tests/Qingyue.NavigationTests' `
  '开发空间/Windows/tests/Qingyue.ReaderExperienceTests/Program.cs'
git commit -m "fix(windows): preserve reading viewport during refresh"
```

---

### Task 5: 补齐 0.6.0 版本、诊断、说明和测试入口

**Files:**
- Modify: `开发空间/Windows/src/Qingyue.Windows/Qingyue.Windows.csproj`
- Modify: `开发空间/Windows/Directory.Build.props`
- Modify: `开发空间/Windows/installer/version.nsh`
- Modify: `开发空间/Windows/scripts/test.ps1`
- Modify: `开发空间/Windows/README.md`
- Modify: `操作指导/Windows使用说明.md`
- Modify: `CHANGELOG.md`
- Modify: `docs/test.md`
- Create: `开发空间/Windows/tests/Qingyue.ReaderExperienceTests/Markdown060Checks.cs`

**Interfaces:**
- Consumes: Task 1-4 的诊断字段、测试项目和现有版本检查脚本。
- Produces: 0.6.0 / Windows 构建 1 / 文件版本 0.6.0.1，完整 Windows 回归入口和 0.6.0 验收记录。

- [ ] **Step 1: 增加阶段诊断字段**

在现有 `operationDiagnostics` 中只保留当前进程最近记录，新增阶段名称：

```text
Markdown 解析
阅读块构造
首屏可读
首屏图片完成
请求取消
复制快照
HTML/RTF/XamlPackage 输出
```

诊断不得包含文件名、路径、Markdown 正文、图片 alt、Base64 或用户目录。

- [ ] **Step 2: 增加 0.6.0 版本检查**

统一更新：

```xml
<Version>0.6.0</Version>
<AssemblyVersion>0.6.0.0</AssemblyVersion>
<FileVersion>0.6.0.1</FileVersion>
<InformationalVersion>0.6.0+windows-build.1</InformationalVersion>
```

```nsh
!define VERSION "0.6.0"
!define BUILD_VERSION "0.6.0.1"
!define WINDOWS_BUILD "1"
```

`scripts/test.ps1` 继续从 `version.nsh` 读取期望值，并增加 `Qingyue.ReaderExperienceTests` 的 0.6.0 Markdown 图片调度/复制检查。

- [ ] **Step 3: 更新 Windows 文档**

`操作指导/Windows使用说明.md` 明确：

- Markdown 打开默认阅读预览；
- 图片先显示占位，按视口加载；
- 复制全文/选区包含结构和自包含图片；
- 浏览器、腾讯文档、Word/WPS 和纯文本接收方存在能力差异；
- 超出正文或图片资源预算时的降级行为；
- 本版本不下载远程图片。

`README.md` 和 `CHANGELOG.md` 只追加 Windows 0.6.0 说明，不改 Mac 版本描述。

- [ ] **Step 4: 运行版本和文档检查**

```powershell
pwsh -NoProfile -File scripts/test.ps1 -Configuration Release
```

预期：构建文件版本等于 `0.6.0.1`，完整测试入口包含新检查，文档中的 Windows 版本和实现边界一致。

- [ ] **Step 5: 提交版本资料**

```powershell
git add '开发空间/Windows/src/Qingyue.Windows/Qingyue.Windows.csproj' `
  '开发空间/Windows/Directory.Build.props' `
  '开发空间/Windows/installer/version.nsh' `
  '开发空间/Windows/scripts/test.ps1' `
  '开发空间/Windows/README.md' `
  '操作指导/Windows使用说明.md' `
  'CHANGELOG.md' `
  'docs/test.md' `
  '开发空间/Windows/tests/Qingyue.ReaderExperienceTests/Markdown060Checks.cs'
git commit -m "chore(windows): prepare 0.6.0 release"
```

---

### Task 6: 完成 Windows 全量验证、打包和本机安装

**Files:**
- Modify: `docs/test.md`
- Generate locally: `开发空间/Windows/交付产品/轻阅-Windows-x64/`
- Generate locally: `开发空间/Windows/交付产品/轻阅-0.6.0-Windows构建1-x64.zip`
- Generate locally: `开发空间/Windows/交付产品/轻阅-0.6.0-Windows构建1-x64-setup.exe`
- Generate ignored evidence: `开发空间/Windows/artifacts/`

**Interfaces:**
- Consumes: 已通过 Task 1-5 的 Windows 代码、测试和版本配置。
- Produces: Windows 自包含目录、ZIP、安装 EXE、本机安装验证和可审阅的验收记录。

- [ ] **Step 1: 执行 Release 全量回归**

```powershell
pwsh -NoProfile -File scripts/test.ps1 -Configuration Release
```

必须记录：退出码、警告/错误数量、各项目通过项数、文件版本、阅读体验专项、复制格式专项和阅读指针审计结果。

- [ ] **Step 2: 执行真实样本专项**

只读复制用户提供的带图 Markdown 到被忽略的 `artifacts/samples/`，不要提交源文或图片。使用：

```powershell
dotnet run --project tests/Qingyue.ImageTests/Qingyue.ImageTests.csproj -c Release --no-restore -- --samples 'artifacts/samples'
```

检查：首屏正文可读、窗口保持响应、图片按视口加载、缺失/损坏图片不阻塞全文、滚动后图片继续加载、关闭原文后复制图片仍可粘贴。

- [ ] **Step 3: 构建发布目录和安装器**

```powershell
pwsh -NoProfile -File scripts/publish.ps1
pwsh -NoProfile -File scripts/build-installer.ps1
```

逐文件比较发布目录与 ZIP 内容，检查 `Qingyue.exe`、核心 DLL、卸载器和安装器文件版本均为 `0.6.0.1`，安装器登记显示 `0.6.0 (Windows 构建 1)`。

- [ ] **Step 4: 在本机 Windows 原位安装验证**

使用当前用户可见的 Windows 桌面由 Explorer 启动安装包，选择可用安装目录，确认：

- 页面只有选择地址、开始安装和单一进度条；
- 安装期间进度持续变化；
- 安装完成后自动退出；
- 桌面/开始菜单快捷方式、系统卸载登记和产品图标正常；
- 已有用户资料和外部 Markdown 不被修改。

启动已安装程序，验证 Markdown 默认阅读、编辑/预览往返、窗口缩放、搜索高亮、复制到浏览器/Word/WPS/腾讯文档（已安装者）和纯文本输入框。

- [ ] **Step 5: 执行安装器和卸载回归**

```powershell
pwsh -NoProfile -File scripts/test-installer.ps1 -SkipBuild -NoGui
pwsh -NoProfile -File scripts/test-installer-purge.ps1
pwsh -NoProfile -File scripts/test-installer-directory.ps1
pwsh -NoProfile -File scripts/test-installer-theme.ps1
```

默认卸载保留用户信息；选择彻底清理才清理默认 `%LOCALAPPDATA%\Qingyue`；外部文档、非轻阅文件和未知安装文件保持保护。记录实际 Windows 版本和桌面限制。

- [ ] **Step 6: 更新验收记录并做最终检查**

在 `docs/test.md` 追加 0.6.0 Windows 日期、命令、退出码、产物大小/哈希、本机安装目录、第三方粘贴结果、未验收接收方和风险。

执行：

```powershell
git status --short
git diff --check
git log -6 --oneline --decorate
```

预期：工作区只包含已明确提交的 Windows 源码和文档，真实用户资料、图片、路径、发布目录和运行日志未被 Git 跟踪。

- [ ] **Step 7: 提交最终验收记录**

```powershell
git add docs/test.md
git commit -m "docs(windows): record 0.6.0 verification"
```

推送到当前 `feat/windows-client` 前，先确认远端身份和当前分支；不强推、不修改 Mac 分支、不发布未要求的 GitHub Release。

---

## 计划自检

- 设计文档中“正文先显示、图片异步可取消、首屏优先、同文档缓存、外部图片变化失效、资源预算”由 Task 1、Task 2 覆盖。
- 设计文档中“统一复制快照、CF_HTML UTF-8 偏移、HTML/RTF/XamlPackage/纯文本、自包含图片、第三方接收矩阵”由 Task 3 覆盖。
- 设计文档中“缩放、搜索、编辑/预览往返、外部更新、阅读位置”由 Task 4 覆盖。
- 设计文档中“50 万字符、诊断、版本、Windows 全量测试、安装器回归和本机安装”由 Task 5、Task 6 覆盖。
- 所有任务都限定在 Windows 工程；没有 Mac、数据库、Redis、MQ、网络服务或用户资料格式变更。
- 所有任务都有定向测试命令、预期结果和独立提交点；生成的安装包、ZIP、样本和日志只保存在本机忽略目录。
