# Windows 0.8.0 Markdown 深度阅读与更新实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans or superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 Markdown 阅读具备稳定层次、目录跟随、全部搜索高亮和可判断的外部更新流程，同时保持 50 万字符预览与窗口缩放稳定。

**Architecture:** 目录从已有 `ReadingModel.Blocks` 构建；搜索高亮使用独立 Adorner，不修改正文和 `TextPointer` 映射；外部更新继续由 `ExternalFileRefresh` 负责读取，在 UI 侧增加提示、重新加载和只读差异窗口；Markdown 预览只做批量构建和现有文档重排。

**Tech Stack:** .NET 10、WPF、C#、`FlowDocument`、WPF `Adorner`、现有测试可执行项目、PowerShell。

**Spec:** `docs/superpowers/specs/2026-11-12-windows-080-markdown-reading-design.md`

## Global Constraints

- 只修改 Windows 工程、Windows 测试和 Windows 验收资料。
- Markdown 打开默认预览，左右分屏只能主动启用。
- 正文预览上限保持 `500_000` 个 UTF-16 单元；搜索和差异使用各自现有/明确上限。
- 高亮、目录和差异不得改变原文、编码、换行方式和持久化会话格式。
- 生产代码每一步都必须先有会失败的测试。

---

### Task 1: 锁定目录、搜索高亮和外部更新的失败回归

**Files:**
- Modify: `开发空间/Windows/tests/Qingyue.MarkdownTests/Program.cs`
- Modify: `开发空间/Windows/tests/Qingyue.ReaderExperienceTests/Program.cs`
- Modify: `开发空间/Windows/tests/Qingyue.ExternalChangesTests/RegressionTests.cs`
- Modify: `开发空间/Windows/tests/Qingyue.UITests/Program.cs`

**Interfaces:**
- Consumes: `ReadingModel`, `RenderedReading`, `SearchEngine`, `ExternalFileRefresh`, `MainWindow`.
- Produces: failing tests named `OutlineTracksCurrentHeading`, `SearchHighlightsAllMatches`, `ExternalUpdateOffersActions`, `LargeMarkdownCancelsWithoutReplacingDocument`.

- [ ] **Step 1: Write the failing tests**

增加最小样本：

```text
# 一级

## 二级

正文 alpha

## 另一个二级

正文 alpha
```

断言目录包含 3 个标题、级别正确；搜索 `alpha` 后有 2 个匹配，当前匹配和其他匹配都可识别；外部文件在未保存和已保存两种状态下返回不同提示；取消大文档预览后当前 `FlowDocument` 引用和选区不变。

- [ ] **Step 2: Run the tests to verify they fail**

```powershell
dotnet build tests/Qingyue.MarkdownTests/Qingyue.MarkdownTests.csproj -c Release --disable-build-servers -m:1 -nr:false
dotnet tests/Qingyue.MarkdownTests/bin/Release/net10.0-windows/Qingyue.MarkdownTests.dll
dotnet build tests/Qingyue.ReaderExperienceTests/Qingyue.ReaderExperienceTests.csproj -c Release --disable-build-servers -m:1 -nr:false
dotnet tests/Qingyue.ReaderExperienceTests/bin/Release/net10.0-windows/Qingyue.ReaderExperienceTests.dll
```

预期：新增行为断言失败，且失败原因对应缺少目录数据、高亮层或外部更新动作。

- [ ] **Step 3: Commit the red tests**

```powershell
git add "开发空间/Windows/tests/Qingyue.MarkdownTests" "开发空间/Windows/tests/Qingyue.ReaderExperienceTests" "开发空间/Windows/tests/Qingyue.ExternalChangesTests" "开发空间/Windows/tests/Qingyue.UITests"
git commit -m "test(windows): cover markdown outline search and refresh"
```

### Task 2: 增加 Core Markdown 目录和差异模型

**Files:**
- Create: `开发空间/Windows/src/Qingyue.Core/MarkdownOutline.cs`
- Create: `开发空间/Windows/src/Qingyue.Core/TextDiff.cs`
- Modify: `开发空间/Windows/src/Qingyue.Core/Qingyue.Core.csproj`
- Modify: `开发空间/Windows/tests/Qingyue.MarkdownTests/Program.cs`
- Modify: `开发空间/Windows/tests/Qingyue.ExternalChangesTests/RegressionTests.cs`

**Interfaces:**
- Consumes: `ReadingModel.Blocks`, `ReadingBlockKind`, source text.
- Produces:

```csharp
public sealed record MarkdownOutlineEntry(int Level, string Title, int SourceStart, int SourceLength, int DisplayStart);
public static class MarkdownOutline
{
    public static IReadOnlyList<MarkdownOutlineEntry> Build(string text, ReadingModel model);
}
public enum TextDiffLineKind { Equal, Added, Removed, Changed }
public sealed record TextDiffLine(TextDiffLineKind Kind, string Text, int LeftLine, int RightLine);
public static class TextDiff
{
    public static IReadOnlyList<TextDiffLine> Build(string left, string right);
}
```

- [ ] **Step 1: Implement `MarkdownOutline.Build` test-first**

只使用 `model.Blocks` 中 `ReadingBlockKind.Heading`，标题文本取块的可见正文，级别限制在 1 到 6，保留源/显示偏移。对没有标题的文本返回空只读列表。

- [ ] **Step 2: Run the outline test**

```powershell
dotnet build tests/Qingyue.MarkdownTests/Qingyue.MarkdownTests.csproj -c Release --disable-build-servers -m:1 -nr:false
dotnet tests/Qingyue.MarkdownTests/bin/Release/net10.0-windows/Qingyue.MarkdownTests.dll
```

- [ ] **Step 3: Implement bounded `TextDiff.Build`**

按行切分，输入任一侧超过 2,000,000 个 UTF-16 单元或 100,000 行时抛出 `ArgumentException`；正常输入使用滚动一行窗口的 LCS 计算，输出 `Equal`、`Added`、`Removed` 和相邻删除/新增合并出的 `Changed`。

- [ ] **Step 4: Run external changes tests**

```powershell
dotnet build tests/Qingyue.ExternalChangesTests/Qingyue.ExternalChangesTests.csproj -c Release --disable-build-servers -m:1 -nr:false
dotnet tests/Qingyue.ExternalChangesTests/bin/Release/net10.0-windows/Qingyue.ExternalChangesTests.dll
```

- [ ] **Step 5: Commit**

```powershell
git add "开发空间/Windows/src/Qingyue.Core/MarkdownOutline.cs" "开发空间/Windows/src/Qingyue.Core/TextDiff.cs" "开发空间/Windows/src/Qingyue.Core/Qingyue.Core.csproj" "开发空间/Windows/tests/Qingyue.MarkdownTests" "开发空间/Windows/tests/Qingyue.ExternalChangesTests"
git commit -m "feat(core): add markdown outline and bounded text diff"
```

### Task 3: 接入目录跟随和 Markdown 阅读层次

**Files:**
- Modify: `开发空间/Windows/src/Qingyue.Windows/MainWindow.Navigation.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/MainWindow.Formatting.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/MainWindow.xaml`
- Modify: `开发空间/Windows/src/Qingyue.Windows/MarkdownPreview.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/RenderedReading.cs`
- Modify: `开发空间/Windows/tests/Qingyue.ReaderExperienceTests/Program.cs`
- Modify: `开发空间/Windows/tests/Qingyue.UITests/Program.cs`

**Interfaces:**
- Consumes: `MarkdownOutline.Build`, `RenderedReading.PointerAt`, `ReadingModel.DisplayToSource`.
- Produces: `OutlinePinButton`, current heading status, and `UpdateOutlineFollow()` called after reading scroll/selection changes.

- [ ] **Step 1: Add failing UI assertions**

断言：

```csharp
Assert(Descendants<ListBox>(window).Any(list => AutomationProperties.GetName(list) == "Markdown 文档目录"), "outline missing");
Assert(Descendants<Button>(window).Any(button => button.Content?.ToString() == "固定目录"), "outline pin action missing");
```

并断言标题级别使用不同缩进、阅读滚动后当前目录项与最近标题匹配。

- [ ] **Step 2: Add outline state to the main window**

增加字段：

```csharp
private IReadOnlyList<MarkdownOutlineEntry> currentOutline = [];
private bool outlinePinned;
private int currentOutlineIndex = -1;
```

切换标签、进入阅读、外部刷新后从当前 `ReadingModel` 构建目录；目录跳转调用现有 `RememberNavigationPosition`，再用 `RenderedReading.PointerAt` 进入视口。

- [ ] **Step 3: Add current heading tracking**

在预览控件 `ScrollChanged`、选择变化和 `DispatcherPriority.ContextIdle` 回调中调用 `UpdateOutlineFollow()`；使用 `Preview.VerticalOffset` 和标题 `GetCharacterRect`，选择顶部最近的标题。更新只改变 `ListBox.SelectedIndex` 和状态文本，不触发反向跳转。

- [ ] **Step 4: Improve Markdown block hierarchy**

在 `RenderedReading.StyleBlock` 和 `MarkdownPreview.BuildBlocks` 中补充：

- 代码块语言小标签，语言为空时显示“代码”；
- 任务列表使用可读的 `☐`/`☑` 文本和更明显的状态色；
- 引用按嵌套级别增加左边框和背景层次；
- 表格表头和代码块复制入口保持现有结构；
- 不改变 `Run` 文本和 `RichDocumentMap` 映射。

- [ ] **Step 5: Run focused tests**

```powershell
dotnet tests/Qingyue.ReaderExperienceTests/bin/Release/net10.0-windows/Qingyue.ReaderExperienceTests.dll
dotnet tests/Qingyue.UITests/bin/Release/net10.0-windows/Qingyue.UITests.dll
```

- [ ] **Step 6: Commit**

```powershell
git add "开发空间/Windows/src/Qingyue.Windows/MainWindow.Navigation.cs" "开发空间/Windows/src/Qingyue.Windows/MainWindow.Formatting.cs" "开发空间/Windows/src/Qingyue.Windows/MainWindow.xaml" "开发空间/Windows/src/Qingyue.Windows/MarkdownPreview.cs" "开发空间/Windows/src/Qingyue.Windows/RenderedReading.cs" "开发空间/Windows/tests/Qingyue.ReaderExperienceTests" "开发空间/Windows/tests/Qingyue.UITests"
git commit -m "feat(windows): improve markdown outline and reading hierarchy"
```

### Task 4: 接入全部搜索高亮

**Files:**
- Create: `开发空间/Windows/src/Qingyue.Windows/SearchHighlightService.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/MainWindow.Search.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/MainWindow.Navigation.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/Theme.xaml`
- Modify: `开发空间/Windows/tests/Qingyue.ReaderExperienceTests/Program.cs`
- Modify: `开发空间/Windows/tests/Qingyue.UITests/Program.cs`

**Interfaces:**
- Consumes: `IReadOnlyList<SearchMatch>`, `RichTextBox`, `RenderedReading`.
- Produces:

```csharp
internal sealed class SearchHighlightService : IDisposable
{
    internal void Apply(RichTextBox host, IReadOnlyList<(TextPointer Start, TextPointer End)> ranges, int currentIndex);
    internal void Clear();
}
```

- [ ] **Step 1: Add failing highlighter tests**

建立两个匹配的编辑器和预览样本，断言全部匹配都生成高亮矩形，当前索引使用 `CurrentSearchHighlightBrush`，关闭搜索后 Adorner 被移除。

- [ ] **Step 2: Implement adorner-only rendering**

`SearchHighlightService` 使用 `AdornerLayer.GetAdornerLayer(host)`，在 `OnRender` 中为每个范围调用 `TextPointer.GetCharacterRect(LogicalDirection.Forward)`，对跨行匹配绘制多个矩形。服务只保存 `TextPointer` 和索引，不修改 `FlowDocument`。

- [ ] **Step 3: Map SearchMatch results**

在 `MainWindow.Search.cs` 中，搜索结果完成后为当前阅读模式调用 `tab.Rendered.PointerAt`，为编辑模式调用 `Editor` 的文本偏移转换；应用全部高亮，再调用 `SelectMatch` 选中当前项。搜索 generation 变化时先 `Clear()`。

- [ ] **Step 4: Add theme brushes**

在 `Theme.xaml` 增加：

```xml
<SolidColorBrush x:Key="SearchHighlightBrush" Color="#66F4B942"/>
<SolidColorBrush x:Key="CurrentSearchHighlightBrush" Color="#B8FFB000"/>
```

- [ ] **Step 5: Run search and UI tests**

```powershell
dotnet build tests/Qingyue.ReaderExperienceTests/Qingyue.ReaderExperienceTests.csproj -c Release --disable-build-servers -m:1 -nr:false
dotnet tests/Qingyue.ReaderExperienceTests/bin/Release/net10.0-windows/Qingyue.ReaderExperienceTests.dll
dotnet build tests/Qingyue.UITests/Qingyue.UITests.csproj -c Release --disable-build-servers -m:1 -nr:false
dotnet tests/Qingyue.UITests/bin/Release/net10.0-windows/Qingyue.UITests.dll
```

- [ ] **Step 6: Commit**

```powershell
git add "开发空间/Windows/src/Qingyue.Windows/SearchHighlightService.cs" "开发空间/Windows/src/Qingyue.Windows/MainWindow.Search.cs" "开发空间/Windows/src/Qingyue.Windows/MainWindow.Navigation.cs" "开发空间/Windows/src/Qingyue.Windows/Theme.xaml" "开发空间/Windows/tests/Qingyue.ReaderExperienceTests" "开发空间/Windows/tests/Qingyue.UITests"
git commit -m "feat(windows): highlight all markdown search matches"
```

### Task 5: 外部更新横幅、重新加载和差异窗口

**Files:**
- Create: `开发空间/Windows/src/Qingyue.Windows/TextDiffWindow.xaml`
- Create: `开发空间/Windows/src/Qingyue.Windows/TextDiffWindow.xaml.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/MainWindow.ExternalChanges.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/MainWindow.xaml`
- Modify: `开发空间/Windows/src/Qingyue.Windows/MainWindow.Commands.cs`
- Modify: `开发空间/Windows/tests/Qingyue.ExternalChangesTests/RegressionTests.cs`
- Modify: `开发空间/Windows/tests/Qingyue.UITests/Program.cs`

**Interfaces:**
- Consumes: `ExternalRefreshResult`, `TextDiff.Build`, `ApplyExternalRefresh`.
- Produces:

```csharp
private void ShowExternalChange(string message, bool conflict);
private void ReloadExternalFile();
private void KeepExternalVersion();
private void OpenExternalDiff();
```

- [ ] **Step 1: Add failing UI/behavior tests**

断言外部变更时 `ExternalChangePanel` 可见；无未保存编辑时出现“重新加载”，有未保存编辑时出现“保留当前内容”和“查看差异”；差异窗口为只读，包含新增/删除行颜色。

- [ ] **Step 2: Add the XAML banner and window**

在文档工具区域加入包含消息文本、`重新加载`、`保留当前内容`、`查看差异` 三个按钮的 `ExternalChangePanel`。新增 `TextDiffWindow`，左右两个只读 `RichTextBox` 以行颜色显示差异，窗口启动大小 1100x720，最小宽度 760。

- [ ] **Step 3: Wire the existing refresh state**

在 `ExternalRefreshTick` 根据 `Changed`、`Conflict`、`Unavailable` 设置状态；`ReloadExternalFile` 调用现有 `ApplyExternalRefresh`；`KeepExternalVersion` 清除当前状态并保留 `LastRead`；`OpenExternalDiff` 读取当前文件和磁盘版本的文本快照后调用 `TextDiff.Build`，超限只显示提示。

- [ ] **Step 4: Preserve source and reading anchors**

重新加载成功后复用当前的 `TextAnchorMapper`、阅读位置、Markdown 预览滚动位置和选区恢复；差异窗口关闭不触发主窗口刷新。

- [ ] **Step 5: Run external update tests**

```powershell
dotnet build tests/Qingyue.ExternalChangesTests/Qingyue.ExternalChangesTests.csproj -c Release --disable-build-servers -m:1 -nr:false
dotnet tests/Qingyue.ExternalChangesTests/bin/Release/net10.0-windows/Qingyue.ExternalChangesTests.dll
dotnet tests/Qingyue.UITests/bin/Release/net10.0-windows/Qingyue.UITests.dll
```

- [ ] **Step 6: Commit**

```powershell
git add "开发空间/Windows/src/Qingyue.Windows/TextDiffWindow.xaml" "开发空间/Windows/src/Qingyue.Windows/TextDiffWindow.xaml.cs" "开发空间/Windows/src/Qingyue.Windows/MainWindow.ExternalChanges.cs" "开发空间/Windows/src/Qingyue.Windows/MainWindow.xaml" "开发空间/Windows/src/Qingyue.Windows/MainWindow.Commands.cs" "开发空间/Windows/tests/Qingyue.ExternalChangesTests" "开发空间/Windows/tests/Qingyue.UITests"
git commit -m "feat(windows): surface external markdown updates and diff"
```

### Task 6: 大文档和最终交付验证

**Files:**
- Modify: `开发空间/Windows/src/Qingyue.Windows/MarkdownPreview.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/MainWindow.Markdown.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/MainWindow.ExternalChanges.cs`
- Modify: `开发空间/Windows/tests/Qingyue.ReaderExperienceTests/Program.cs`
- Modify: `开发空间/Windows/tests/Qingyue.MarkdownTests/Program.cs`
- Modify: `CHANGELOG.md`
- Modify: `README.md`
- Modify: `docs/test.md`
- Modify: `开发空间/Windows/README.md`

**Interfaces:**
- Consumes: all prior features and existing publish/installer scripts.
- Produces: 0.8.0 Windows build 1 documentation and verification evidence.

- [ ] **Step 1: Add performance regression cases**

生成 50 万字符以内的标题/列表/代码/图片混合 Markdown，验证异步构建可取消、取消后旧 `FlowDocument` 和选区仍在；窗口宽度在 820、1120、1600、900 之间反复变化，验证 `PagePadding` 更新且正文没有横向溢出。

- [ ] **Step 2: Implement only required performance guards**

确认 `MarkdownPreview.BuildDocumentAsync` 每批让出 UI 线程；确认旧 generation 不能发布；确认 `UpdatePresentation` 不重新读取图片和不重建正文。若测试发现缺口，只补对应 guard，不重写渲染器。

- [ ] **Step 3: Run the complete Windows suite**

```powershell
pwsh -NoProfile -File 开发空间/Windows/scripts/test.ps1 -Configuration Release
pwsh -NoProfile -File 开发空间/Windows/scripts/test-installer-contract.ps1
pwsh -NoProfile -File 开发空间/Windows/scripts/test-installer-theme.ps1
```

预期：所有脚本退出码为 0，构建无警告和错误。

- [ ] **Step 4: Update version and documents**

版本统一为 `0.8.0`、文件版本 `0.8.0.1`、Windows 构建 `1`。README、CHANGELOG 和 `docs/test.md` 说明 Markdown 默认预览、复制兼容、图片重试、搜索高亮、目录跟随和外部更新动作。

- [ ] **Step 5: Build, install and smoke test**

```powershell
pwsh -NoProfile -File 开发空间/Windows/scripts/publish.ps1
pwsh -NoProfile -File 开发空间/Windows/scripts/build-installer.ps1 -SkipPublish
```

覆盖安装到 `D:\Program File\Qingyue`，打开 `开发空间/Windows/Samples/示例.md` 和本地图片样本，验证打开预览、编辑返回预览、目录、搜索、外部更新提示、浏览器/富文本复制和窗口缩放。

- [ ] **Step 6: Commit and push**

```powershell
git add "开发空间/Windows/src/Qingyue.Windows/MarkdownPreview.cs" "开发空间/Windows/src/Qingyue.Windows/MainWindow.Markdown.cs" "开发空间/Windows/src/Qingyue.Windows/MainWindow.ExternalChanges.cs" "开发空间/Windows/tests/Qingyue.ReaderExperienceTests" "开发空间/Windows/tests/Qingyue.MarkdownTests" "CHANGELOG.md" "README.md" "docs/test.md" "开发空间/Windows/README.md"
git commit -m "release(windows): ship 0.8.0 markdown reading experience"
git push origin feat/windows-client
git ls-remote origin refs/heads/feat/windows-client
```

## Risks and Controls

- 搜索高亮使用 Adorner，不写入 `FlowDocument`，避免破坏复制和源偏移映射。
- 差异计算有字符和行上限，避免大文件 LCS 阵列占满内存。
- 外部更新冲突保留当前未保存内容，重新加载必须由用户主动触发。
- 大文档构建使用取消、generation 和 UI 分批让出，防止旧任务覆盖新内容。
