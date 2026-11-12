# Windows 0.8.0 Markdown 图片与跨应用复制实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans or superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 Markdown 图片在复杂 Windows 路径和失败场景下稳定显示，并让浏览器、腾讯文档、Word/WPS 和图片目标从同一份安全复制快照得到一致结果。

**Architecture:** 复用现有 `MarkdownImageLoader`、`MarkdownImageView` 和 `ReadingClipboard.Export`。图片重试只更新单个控件；复制先在 UI 线程建立冻结资源快照，再输出 HTML、RTF、XamlPackage、Bitmap 和 UnicodeText，后台不访问 WPF 视觉树。

**Tech Stack:** .NET 10、WPF、C#、`FlowDocument`、`BitmapSource`、现有自定义单元测试可执行项目、PowerShell 发布脚本。

**Spec:** `docs/superpowers/specs/2026-11-12-windows-080-markdown-media-copy-design.md`

## Global Constraints

- 只修改 Windows 工程、Windows 测试和 Windows 发布文档。
- 不引入 WebView、网络图片请求或新的运行时依赖。
- Markdown 正文预览上限保持 `500_000` 个 UTF-16 单元，图片使用现有字节/像素预算。
- 富文本生成失败时必须保留 `UnicodeText`。
- 生产代码每一步都必须先有一个会失败的测试。

---

### Task 1: 锁定图片路径、失败重试和快照降级的失败回归

**Files:**
- Modify: `开发空间/Windows/tests/Qingyue.ImageTests/Program.cs`
- Modify: `开发空间/Windows/tests/Qingyue.ImageTests/PresentationTests.cs`
- Modify: `开发空间/Windows/tests/Qingyue.ClipboardTests/Program.cs`
- Modify: `开发空间/Windows/tests/Qingyue.ClipboardTests/SharingChecks.cs`
- Modify: `开发空间/Windows/tests/Qingyue.ClipboardTests/MainWindowSharingChecks.cs`

**Interfaces:**
- Consumes: `MarkdownPreview`, `MarkdownImageLoader`, `MarkdownImageView`, `ReadingClipboard`.
- Produces: failing tests named `ImagePathVariants`, `RetryFailedImage`, `ClipboardSnapshotIsSelfContained`, `RichExportFailureKeepsUnicodeText`.

- [ ] **Step 1: Write the failing tests**

在 `Qingyue.ImageTests/Program.cs` 增加：

```csharp
Test("中文空格括号和 file URI 图片路径可解析", ImagePathVariants);
Test("失败图片可以只重试当前控件", RetryFailedImage);
```

在 `Qingyue.ClipboardTests/Program.cs` 增加：

```csharp
Test("复制快照只依赖已冻结资源", ClipboardSnapshotIsSelfContained);
Test("富文本导出失败仍保留 UnicodeText", RichExportFailureKeepsUnicodeText);
```

测试使用测试目录中的临时 PNG，路径依次包含 `中文 目录`、`(截图)` 和空格；`file:` URI 使用 `Uri.EscapeDataString` 生成。重试测试先让图片显示失败，再把 PNG 写入同一路径，调用控件公开的重试入口，断言只有该控件状态变化。

- [ ] **Step 2: Run the tests to verify they fail**

运行：

```powershell
dotnet build tests/Qingyue.ImageTests/Qingyue.ImageTests.csproj -c Release --disable-build-servers -m:1 -nr:false
dotnet tests/Qingyue.ImageTests/bin/Release/net10.0-windows/Qingyue.ImageTests.dll
dotnet build tests/Qingyue.ClipboardTests/Qingyue.ClipboardTests.csproj -c Release --disable-build-servers -m:1 -nr:false
dotnet tests/Qingyue.ClipboardTests/bin/Release/net10.0-windows/Qingyue.ClipboardTests.dll
```

预期：新增测试至少因为没有重试入口或路径样本未稳定通过而失败；不得因为测试编译错误失败。

- [ ] **Step 3: Commit the red tests**

```powershell
git add "开发空间/Windows/tests/Qingyue.ImageTests" "开发空间/Windows/tests/Qingyue.ClipboardTests"
git commit -m "test(windows): cover markdown image retry and copy snapshot"
```

### Task 2: 稳定本地图片路径和失败重试

**Files:**
- Modify: `开发空间/Windows/src/Qingyue.Windows/ReadingRenderer.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/MarkdownImageCache.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/MarkdownImageScheduler.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/ReaderImageWindow.cs`
- Modify: `开发空间/Windows/tests/Qingyue.ImageTests/Program.cs`

**Interfaces:**
- Consumes: `MarkdownImageReference.Target`, `sourcePath`, `MarkdownImageBudget`.
- Produces: `MarkdownImageView.Retry()`, `MarkdownImageView.IsReady`, and a normalized `MarkdownImageLoader.LocalPath` path used by cache keys.

- [ ] **Step 1: Implement the smallest path normalization change**

在 `MarkdownImageLoader.LocalPath` 保持现有拒绝网络路径逻辑，补充：

```csharp
path = Uri.UnescapeDataString(path)
    .Replace('/', Path.DirectorySeparatorChar);
```

仅在目标不是 `file:`、不是绝对路径且不是网络路径时按 `sourcePath` 所在目录组合；不要把合法的 Windows 盘符 `C:\` 误判成 URI scheme。

- [ ] **Step 2: Implement one-image retry**

在 `MarkdownImageView` 增加：

```csharp
internal bool IsReady => finished && image.Source is BitmapSource;
internal void Retry()
```

`Retry()` 必须先取消旧请求、重置状态和可见性，再调用 `OnLoaded` 注册调度器；失败状态显示 `"图片加载失败：" + result.Error + "  · 点击重试"`。成功后只设置当前 `Image.Source`、状态可见性和测量，不替换父文档。

- [ ] **Step 3: Add the retry context menu action**

在 `ReaderImageWindow`/图片控件上下文菜单中，仅对失败状态启用“重试加载”。点击时调用 `MarkdownImageView.Retry()`，不得改变选区和滚动偏移。

- [ ] **Step 4: Run the focused tests**

```powershell
dotnet build tests/Qingyue.ImageTests/Qingyue.ImageTests.csproj -c Release --disable-build-servers -m:1 -nr:false
dotnet tests/Qingyue.ImageTests/bin/Release/net10.0-windows/Qingyue.ImageTests.dll
```

预期：`ImagePathVariants`、`RetryFailedImage` 和已有图片测试全部通过。

- [ ] **Step 5: Commit**

```powershell
git add "开发空间/Windows/src/Qingyue.Windows/ReadingRenderer.cs" "开发空间/Windows/src/Qingyue.Windows/MarkdownImageCache.cs" "开发空间/Windows/src/Qingyue.Windows/MarkdownImageScheduler.cs" "开发空间/Windows/src/Qingyue.Windows/ReaderImageWindow.cs" "开发空间/Windows/tests/Qingyue.ImageTests"
git commit -m "feat(windows): stabilize markdown image paths and retry"
```

### Task 3: 收敛复制快照和目标应用兼容性

**Files:**
- Modify: `开发空间/Windows/src/Qingyue.Windows/ReadingClipboard.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/ReadingHtmlExport.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/MainWindow.Clipboard.cs`
- Modify: `开发空间/Windows/tests/Qingyue.ClipboardTests/Program.cs`
- Modify: `开发空间/Windows/tests/Qingyue.ClipboardTests/SharingChecks.cs`
- Modify: `开发空间/Windows/tests/Qingyue.ClipboardTests/MainWindowSharingChecks.cs`

**Interfaces:**
- Consumes: `FlowDocument`, `TextRange`, `ReadingClipboard.Export`.
- Produces: internal `ReadingClipboardSnapshot CreateSnapshot(FlowDocument document, TextRange range)`, plus existing `ReadingClipboardResult` compatibility wrapper.

- [ ] **Step 1: Add the failing snapshot assertions**

测试必须覆盖：

```csharp
var result = ReadingClipboard.CreateFullDocumentData(preview);
Check(result!.Data.GetDataPresent(DataFormats.Html, false), "HTML missing");
Check(result.Data.GetDataPresent(DataFormats.Rtf, false), "RTF missing");
Check(result.Data.GetDataPresent(DataFormats.XamlPackage, false), "XamlPackage missing");
Check(result.Data.GetDataPresent(DataFormats.UnicodeText, false), "UnicodeText missing");
```

复制后删除临时源图片，再从 HTML 的 data URI、RTF 的 `\pict` 和 XamlPackage 重新载入，断言图像仍可解码。对只包含一张图的选区断言 `DataFormats.Bitmap` 存在，对混合文字和多图全文断言不存在误导性的 Bitmap。

- [ ] **Step 2: Run the failing tests**

```powershell
dotnet build tests/Qingyue.ClipboardTests/Qingyue.ClipboardTests.csproj -c Release --disable-build-servers -m:1 -nr:false
dotnet tests/Qingyue.ClipboardTests/bin/Release/net10.0-windows/Qingyue.ClipboardTests.dll
```

预期：新增快照边界和降级断言失败，现有测试结果保持可辨识。

- [ ] **Step 3: Implement the immutable snapshot**

在 `ReadingClipboard.cs` 增加：

```csharp
internal sealed record ReadingClipboardSnapshot(
    string PlainText,
    string Html,
    string? Rtf,
    MemoryStream? XamlPackage,
    BitmapSource? Bitmap,
    ReadingImageReport Images);
```

`CreateSnapshot` 先构造 `Export`，再用 `RichRange()` 生成脱离原文档的白名单 `FlowDocument`；所有 `BitmapSource` 在写入快照前确认 `IsFrozen`。`CreateData` 从快照创建 `DataObject`，全文、选区、代码块和表格都走同一个方法。

- [ ] **Step 4: Implement atomic degradation**

富文本输出中的任意 `ExportLimitException`、`IOException`、`InvalidOperationException`、`COMException` 或 `SecurityException` 都不能发布不完整的 HTML。先创建 UnicodeText，再尝试设置 HTML/RTF/XamlPackage；失败时返回只包含 UnicodeText 的 `DataObject`，并让 UI 状态显示降级原因。

- [ ] **Step 5: Verify CF_HTML and browser structure**

使用已有 `SharingChecks` 增加本地 `contenteditable` 等价 HTML 解析检查，验证标题、表格、代码、图片和 Mermaid PNG 都在 `StartFragment`/`EndFragment` 范围内；检查 `StartHTML`、`EndHTML`、`StartFragment`、`EndFragment` 使用 UTF-8 字节偏移。

- [ ] **Step 6: Run focused clipboard tests**

```powershell
dotnet build tests/Qingyue.ClipboardTests/Qingyue.ClipboardTests.csproj -c Release --disable-build-servers -m:1 -nr:false
dotnet tests/Qingyue.ClipboardTests/bin/Release/net10.0-windows/Qingyue.ClipboardTests.dll
```

预期：剪贴板测试全部通过，输出 `Clipboard checks: ... passed` 且退出码为 0。

- [ ] **Step 7: Commit**

```powershell
git add "开发空间/Windows/src/Qingyue.Windows/ReadingClipboard.cs" "开发空间/Windows/src/Qingyue.Windows/ReadingHtmlExport.cs" "开发空间/Windows/src/Qingyue.Windows/MainWindow.Clipboard.cs" "开发空间/Windows/tests/Qingyue.ClipboardTests"
git commit -m "feat(windows): unify markdown clipboard snapshots"
```

### Task 4: 图片、Mermaid、代码块的交互回归

**Files:**
- Modify: `开发空间/Windows/src/Qingyue.Windows/MermaidSequenceView.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/MermaidBlock.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/MarkdownPreview.cs`
- Modify: `开发空间/Windows/tests/Qingyue.MermaidViewTests/Program.cs`
- Modify: `开发空间/Windows/tests/Qingyue.ReaderExperienceTests/Program.cs`
- Modify: `开发空间/Windows/tests/Qingyue.UITests/Program.cs`

**Interfaces:**
- Consumes: `MermaidSequenceView.CreateClipboardSnapshot`, `MarkdownPreview`, `ReadingClipboard.CreateCurrentCodeBlockData`.
- Produces: stable Mermaid PNG snapshot bounds and visible “复制代码块” action.

- [ ] **Step 1: Add failing UI assertions**

断言 Mermaid 图在缩放后快照仍有有效尺寸；图形超出宽高/像素上限时复制结果包含源码和提示，不包含空 `<img>`；代码块上下文菜单存在“复制当前代码块”，复制后原选区和滚动位置不变。

- [ ] **Step 2: Run focused UI tests and confirm red**

```powershell
dotnet build tests/Qingyue.MermaidViewTests/Qingyue.MermaidViewTests.csproj -c Release --disable-build-servers -m:1 -nr:false
dotnet tests/Qingyue.MermaidViewTests/bin/Release/net10.0-windows/Qingyue.MermaidViewTests.dll
dotnet build tests/Qingyue.UITests/Qingyue.UITests.csproj -c Release --disable-build-servers -m:1 -nr:false
dotnet tests/Qingyue.UITests/bin/Release/net10.0-windows/Qingyue.UITests.dll
```

- [ ] **Step 3: Implement only the missing interaction**

复用现有 Mermaid 快照和图片上下文菜单，不改变 Mermaid 解析范围；代码块按钮调用 `CreateCurrentCodeBlockData`，目标数据仍由统一快照产生。

- [ ] **Step 4: Run image, Mermaid, clipboard and UI tests**

```powershell
dotnet tests/Qingyue.ImageTests/bin/Release/net10.0-windows/Qingyue.ImageTests.dll
dotnet tests/Qingyue.MermaidViewTests/bin/Release/net10.0-windows/Qingyue.MermaidViewTests.dll
dotnet tests/Qingyue.ClipboardTests/bin/Release/net10.0-windows/Qingyue.ClipboardTests.dll
dotnet tests/Qingyue.UITests/bin/Release/net10.0-windows/Qingyue.UITests.dll
```

- [ ] **Step 5: Commit**

```powershell
git add "开发空间/Windows/src/Qingyue.Windows/MermaidSequenceView.cs" "开发空间/Windows/src/Qingyue.Windows/MermaidBlock.cs" "开发空间/Windows/src/Qingyue.Windows/MarkdownPreview.cs" "开发空间/Windows/tests/Qingyue.MermaidViewTests" "开发空间/Windows/tests/Qingyue.ReaderExperienceTests" "开发空间/Windows/tests/Qingyue.UITests"
git commit -m "test(windows): verify markdown media interactions"
```

### Task 5: Windows 0.8.0 版本和交付验证

**Files:**
- Modify: `开发空间/Windows/Directory.Build.props`
- Modify: `开发空间/Windows/src/Qingyue.Windows/Qingyue.Windows.csproj`
- Modify: `开发空间/Windows/installer/version.nsh`
- Modify: `开发空间/Windows/README.md`
- Modify: `CHANGELOG.md`
- Modify: `docs/test.md`

**Interfaces:**
- Consumes: all passing Windows test binaries and existing publish/installer scripts.
- Produces: `0.8.0.1` Windows build 1 installer, ZIP, local installation evidence and release notes.

- [ ] **Step 1: Update version sources**

把三个版本源统一为：

```text
VERSION=0.8.0
BUILD_VERSION=0.8.0.1
WINDOWS_BUILD=1
```

WPF 程序集 `Version` 为 `0.8.0`，`FileVersion` 为 `0.8.0.1`，`InformationalVersion` 为 `0.8.0+windows-build.1`。

- [ ] **Step 2: Run the complete Windows test script**

```powershell
pwsh -NoProfile -File scripts/test.ps1 -Configuration Release
pwsh -NoProfile -File scripts/test-installer-contract.ps1
pwsh -NoProfile -File scripts/test-installer-theme.ps1
```

预期：所有脚本退出码为 0；构建无 warning/error；测试产物写入 `开发空间/Windows/artifacts`。

- [ ] **Step 3: Build publish directory, ZIP and installer**

```powershell
pwsh -NoProfile -File scripts/publish.ps1
pwsh -NoProfile -File scripts/build-installer.ps1 -SkipPublish
```

检查交付目录只保留 0.8.0 产物，安装器文件版本为 `0.8.0.1`，ZIP 内 `Qingyue.exe` 版本一致。

- [ ] **Step 4: Install and smoke test**

覆盖安装到 `D:\Program File\Qingyue`，启动后打开 `开发空间/Windows/Samples/示例.md` 和包含本地图片的样本，验证预览、重试、复制和退出；不要删除用户资料。

- [ ] **Step 5: Record evidence and commit**

在 `docs/test.md` 增加 2026-11-12 Windows 0.8.0 构建记录，在 `CHANGELOG.md` 和 `开发空间/Windows/README.md` 更新行为边界和产物名称。

```powershell
git add "开发空间/Windows/Directory.Build.props" "开发空间/Windows/src/Qingyue.Windows/Qingyue.Windows.csproj" "开发空间/Windows/installer/version.nsh" "开发空间/Windows/README.md" "CHANGELOG.md" "docs/test.md"
git commit -m "release(windows): ship 0.8.0 markdown reading copy"
git push origin feat/windows-client
git ls-remote origin refs/heads/feat/windows-client
```

## 风险与控制

- WPF 视觉树只能在 UI 线程快照，所有后台图片任务只返回冻结 `BitmapSource`。
- 复制预算继续限制单图 16 MiB、单图像素和全文图片总量，防止粘贴动作分配失控。
- 图片重试不重新创建 `FlowDocument`，降低滚动跳动和交互回归风险。
- Windows 版本文件存在多个来源，发布前用脚本和文件版本双重校验。
