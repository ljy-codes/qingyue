# 轻阅 Windows 0.5.0 连续阅读与文件组织 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将轻阅 Windows 客户端升级为 0.5.0，使阅读进度、书签、标签操作、最近文件状态和外部更新后的阅读位置形成一套连续可恢复的体验。

**Architecture:** 复用现有 `EditorTab`、`WorkspaceStore`、`MainWindow` 状态和 Markdown 源文映射，不重做会话恢复、最近文件打开和外部刷新流程。新增独立的 `BookmarkStore` 负责 `Bookmarks.json`，新增无 UI 的 `TextAnchorMapper` 负责外部更新后的源文位置映射；WPF 层只负责把这些纯数据能力接到现有工具条、侧栏、标签和状态栏。

**Tech Stack:** .NET 10 WPF, C#, FlowDocument, JSON 原子写入, PowerShell 回归脚本.

**Spec:** `docs/superpowers/specs/2026-11-12-windows-050-reading-continuity-design.md`

## Global Constraints

- 产品版本为 `0.5.0`，Windows 构建号为 `1`，文件版本为 `0.5.0.1`。
- 旧版 `Session.json`、草稿和 `Settings.json` 必须继续可读；`Bookmarks.json` 不存在时按空书签处理。
- 书签文件只保存路径、位置和短标签，不复制正文或图片。
- 删除最近文件只删除最近记录，不删除磁盘文件。
- 外部文件更新不覆盖未保存内容；冲突仍需用户明确处理。
- 不修改与本版本无关的安装器页面、Markdown 渲染器、剪贴板格式和主题系统。
- 不新增数据库、网络服务或第三方运行时依赖。
- 每个任务先增加能证明目标行为缺失的失败测试，再写最小实现，再运行该任务的定向测试。
- 生产代码修改必须保持当前会话恢复、恢复草稿、标签切换、Markdown 阅读/编辑和外部刷新行为。
- `开发空间/Windows/artifacts/` 只保存本地验证产物，不加入 Git。

---

## 文件地图

- `开发空间/Windows/src/Qingyue.Core/DocumentTypes.cs`：新增持久化书签记录类型。
- `开发空间/Windows/src/Qingyue.Core/BookmarkStore.cs`：新增书签查询、变更、迁移和原子持久化。
- `开发空间/Windows/src/Qingyue.Core/TextAnchorMapper.cs`：新增旧正文到新正文的源位置映射。
- `开发空间/Windows/src/Qingyue.Windows/MainWindow.xaml`：书签侧栏、书签按钮、标签右键菜单和最近文件状态。
- `开发空间/Windows/src/Qingyue.Windows/MainWindow.Commands.cs`：书签、标签菜单、路径复制和资源管理器操作。
- `开发空间/Windows/src/Qingyue.Windows/MainWindow.Navigation.cs`：阅读进度、书签跳转和位置恢复。
- `开发空间/Windows/src/Qingyue.Windows/MainWindow.Persistence.cs`：书签加载和保存调度。
- `开发空间/Windows/src/Qingyue.Windows/MainWindow.Reading.cs`：阅读状态文本和书签按钮状态。
- `开发空间/Windows/src/Qingyue.Windows/MainWindow.ExternalChanges.cs`：外部更新源锚点映射接入。
- `开发空间/Windows/src/Qingyue.Windows/EditorTab.cs`：滚动和文档变更时通知宿主刷新内存位置。
- `开发空间/Windows/src/Qingyue.Windows/RecentItem.cs`：最近文件的存在状态和状态文本；如果类型内嵌在其他文件，则修改对应定义。
- `开发空间/Windows/tests/Qingyue.TextTests/Program.cs`：书签数据和锚点映射纯函数回归。
- `开发空间/Windows/tests/Qingyue.RecoveryTests/CoreScenarios.cs`、`WpfScenarios.cs`：书签持久化、会话恢复、另存为迁移。
- `开发空间/Windows/tests/Qingyue.ExternalChangesTests/RegressionTests.cs`：外部更新后位置、选区和 Markdown 恢复。
- `开发空间/Windows/tests/Qingyue.UITests/Program.cs`：进度、书签侧栏、右键菜单和最近文件状态。
- `开发空间/Windows/Directory.Build.props`、`installer/version.nsh`、`src/Qingyue.Windows/Dialogs.cs`：版本号。
- `docs/test.md`：0.5.0 完整验证证据。

---

### Task 1: 书签模型、存储和源位置映射

**Files:**

- Modify: `开发空间/Windows/src/Qingyue.Core/DocumentTypes.cs`
- Create: `开发空间/Windows/src/Qingyue.Core/BookmarkStore.cs`
- Create: `开发空间/Windows/src/Qingyue.Core/TextAnchorMapper.cs`
- Modify: `开发空间/Windows/tests/Qingyue.TextTests/Program.cs`

**Interfaces:**

- `DocumentBookmark`：

  ```csharp
  public sealed record DocumentBookmark(
      Guid Id,
      Guid DocumentId,
      string? Path,
      int SourceAnchor,
      string Label,
      DateTimeOffset UpdatedAt);
  ```

- `BookmarkStore`：

  ```csharp
  public sealed class BookmarkStore
  {
      public BookmarkStore(string root);
      public IReadOnlyList<DocumentBookmark> Items { get; }
      public Task LoadAsync();
      public IReadOnlyList<DocumentBookmark> For(DocumentFile file);
      public DocumentBookmark Add(DocumentFile file, int sourceAnchor, string label);
      public bool Remove(Guid bookmarkId);
      public int MigratePath(Guid documentId, string? oldPath, string newPath);
      public Task<BookmarkSaveResult> SaveAsync();
  }
  ```

- `BookmarkStore` 必须把路径规范化为绝对路径，按大小写不敏感比较；`For` 对有路径文档优先按路径匹配，对未保存文档按 `DocumentId` 匹配。
- `TextAnchorMapper.Map(string oldText, string newText, int oldAnchor)` 返回限制在新文本范围内的 `int`。

- [ ] **Step 1: 写书签和锚点映射失败测试**

在 `Qingyue.TextTests/Program.cs` 的测试列表中加入以下实际断言：

```csharp
("bookmark validation and path matching", () =>
{
    string root = Path.Combine(Path.GetTempPath(), "qingyue-bookmark-" + Guid.NewGuid());
    Directory.CreateDirectory(root);
    try
    {
        var file = new DocumentFile("标题\n正文") { };
        file.Save(Path.Combine(root, "a.md"));
        var store = new BookmarkStore(root);
        var item = store.Add(file, 3, "正文");
        Check(item.SourceAnchor == 3 && item.Path == file.Path, "书签保存规范化路径和源位置");
        Check(store.For(file).Count == 1, "书签按当前文件路径查询");
        Check(store.Remove(item.Id), "书签可以删除");
        Check(store.For(file).Count == 0, "删除后查询为空");
    }
    finally { try { Directory.Delete(root, true); } catch { } }
}),
("anchor mapping follows unchanged paragraph", () =>
{
    const string oldText = "前言\n\n目标段落\n第二行\n\n结尾";
    const string newText = "新增说明\n\n前言\n\n目标段落\n第二行\n\n结尾";
    int oldAnchor = oldText.IndexOf("目标段落", StringComparison.Ordinal);
    int mapped = TextAnchorMapper.Map(oldText, newText, oldAnchor);
    Check(newText.IndexOf("目标段落", StringComparison.Ordinal) == mapped,
        "外部前置插入后锚点跟随原段落");
    Check(TextAnchorMapper.Map(oldText, "短文", 999) == 3, "映射结果限制在新文本长度");
}),
("bookmark store survives reload", () =>
{
    string root = Path.Combine(Path.GetTempPath(), "qingyue-bookmark-reload-" + Guid.NewGuid());
    Directory.CreateDirectory(root);
    try
    {
        string path = Path.Combine(root, "a.txt");
        File.WriteAllText(path, "第一行\n第二行");
        var file = DocumentFile.Open(path);
        var first = new BookmarkStore(root);
        var item = first.Add(file, 4, "第二行");
        Check(first.SaveAsync().GetAwaiter().GetResult().Errors.Count == 0, "书签首次写入成功");
        var second = new BookmarkStore(root);
        second.LoadAsync().GetAwaiter().GetResult();
        Check(second.Items.Any(x => x.Id == item.Id && x.Label == "第二行"),
            "书签重启后可恢复");
    }
    finally { try { Directory.Delete(root, true); } catch { } }
}),
```

书签测试还要覆盖空标签自动生成、重复删除、损坏 JSON 隔离、超过 500 条限制和超过 1 MiB 的文件保护。

- [ ] **Step 2: 运行纯核心测试确认失败**

运行：

```powershell
dotnet run --project tests/Qingyue.TextTests/Qingyue.TextTests.csproj -c Release --no-restore
```

预期：编译失败或测试失败，原因是 `DocumentBookmark`、`BookmarkStore` 和 `TextAnchorMapper` 尚不存在。

- [ ] **Step 3: 实现最小书签数据层**

在 `DocumentTypes.cs` 增加 `DocumentBookmark` 和：

```csharp
public sealed record BookmarkSaveResult(DateTimeOffset SavedAt, List<string> Errors);
```

在 `BookmarkStore.cs` 实现：

- `Bookmarks.json` 路径为 `Path.Combine(root, "Bookmarks.json")`。
- 使用 `System.Text.Json`，写入 UTF-8、无缩进、允许 Unicode。
- 读取前检查文件大小不超过 1 MiB；JSON 错误时将原文件改名为带时间戳的 `.corrupt` 文件，并返回警告。
- `Add` 将源位置限制在 `0..file.Text.Length`，空标签按源位置所在行生成最多 160 字符的标题。
- `Remove` 按 ID 删除并返回是否找到。
- `MigratePath` 只迁移指定 `DocumentId` 和旧路径的记录，避免影响同名标签。
- 保存先写随机 `.tmp`，再用 `File.Move(..., overwrite: true)` 替换目标；失败时保留原目标。

- [ ] **Step 4: 实现保守的源位置映射**

在 `TextAnchorMapper.Map` 中按以下顺序实现，保证每一步都可测试：

1. 处理空文本和边界偏移。
2. 以旧锚点所在行的起始位置为局部窗口。
3. 在新文本中优先查找该行的稳定前后文。
4. 未找到完整行时查找锚点前后最多 64 个字符的稳定片段。
5. 再退回共同前缀/后缀推导的偏移。
6. 最后返回按旧比例换算的安全偏移，并限制到新文本长度。

该类型不引用 WPF，不读取文件，不判断冲突，也不负责修改 `DocumentFile`。

- [ ] **Step 5: 运行测试确认纯核心能力通过**

运行：

```powershell
dotnet run --project tests/Qingyue.TextTests/Qingyue.TextTests.csproj -c Release --no-restore
dotnet run --project tests/Qingyue.RecoveryTests/Qingyue.RecoveryTests.csproj -c Release --no-restore -p:RecoveryCoreOnly=true -- --root "$env:TEMP\qingyue-050-core"
```

预期：书签存储、数据限制和锚点映射测试通过；现有文本、格式化和搜索测试继续通过。

- [ ] **Step 6: 提交核心数据层**

```powershell
git add '开发空间/Windows/src/Qingyue.Core/DocumentTypes.cs' '开发空间/Windows/src/Qingyue.Core/BookmarkStore.cs' '开发空间/Windows/src/Qingyue.Core/TextAnchorMapper.cs' '开发空间/Windows/tests/Qingyue.TextTests/Program.cs'
git commit -m "feat(windows): add bookmark storage and anchor mapping"
```

### Task 2: 阅读进度、书签跳转和窗口持久化

**Files:**

- Modify: `开发空间/Windows/src/Qingyue.Windows/MainWindow.xaml.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/MainWindow.Navigation.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/MainWindow.Reading.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/MainWindow.Persistence.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/MainWindow.Commands.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/EditorTab.cs`
- Modify: `开发空间/Windows/tests/Qingyue.RecoveryTests/WpfScenarios.cs`
- Modify: `开发空间/Windows/tests/Qingyue.UITests/Program.cs`

**Interfaces:**

- `MainWindow` 新增：

  ```csharp
  public string ReadingProgressText(EditorTab tab);
  public IReadOnlyList<DocumentBookmark> CurrentBookmarks(EditorTab tab);
  public void ToggleBookmark();
  public void NavigateToBookmark(DocumentBookmark bookmark);
  ```

- `EditorTab` 新增内存事件或宿主回调：

  ```csharp
  public event EventHandler? ViewportChanged;
  ```

  该事件只触发内存状态和界面文字更新，不直接写磁盘。

- `MainWindow.Persistence.cs` 创建 `BookmarkStore`，数据目录沿用现有 `dataDirectory`，初始化时 `await LoadAsync()`，书签变更后通过 Dispatcher 防抖调用 `SaveAsync()`。

- [ ] **Step 1: 添加阅读进度和书签失败测试**

在 WPF 流程中创建长文档和 Markdown 文档，加入：

```csharp
var progressTab = window.NewDocument(string.Join("\n", Enumerable.Range(1, 100).Select(i => "第 " + i + " 行")));
window.ToggleReading();
await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
progressTab.Preview.ScrollToVerticalOffset(progressTab.Preview.ScrollableHeight / 2);
progressTab.Preview.RaiseEvent(new ScrollChangedEventArgs(
    ScrollChangedEvent, progressTab.Preview.VerticalOffset, 0, 0, 0, 0, 0, 0, 0));
Check(((TextBlock)window.FindName("ReadingStatus")).Text.Contains("%"), "阅读态显示百分比进度");
window.ToggleBookmark();
Check(window.CurrentBookmarks(progressTab).Count == 1, "当前阅读位置可以添加书签");
window.ToggleBookmark();
Check(window.CurrentBookmarks(progressTab).Count == 0, "同一位置再次操作可以删除书签");
```

在恢复测试中保存带书签的会话，关闭窗口并重新创建 `MainWindow`，断言书签数量、源位置和活动文档保持一致。

- [ ] **Step 2: 运行 WPF 测试确认失败**

运行：

```powershell
dotnet run --project tests/Qingyue.UITests/Qingyue.UITests.csproj -c Release --no-restore
dotnet run --project tests/Qingyue.RecoveryTests/Qingyue.RecoveryTests.csproj -c Release --no-restore -- --root "$env:TEMP\qingyue-050-recovery"
```

预期：找不到阅读进度控件、书签 API 或书签恢复断言失败。

- [ ] **Step 3: 接入书签存储和加载**

在 `MainWindow.xaml.cs` 的初始化流程中，在现有 `WorkspaceStore.LoadAsync()` 完成后加载 `BookmarkStore`。加载失败只写入现有诊断/状态提示，不阻止文档恢复。

在 `MainWindow.Persistence.cs` 中：

- `CaptureWorkspaceEntry` 继续保存 `ViewportAnchor` 和 `ViewportDelta`，不新增重复进度字段。
- 书签保存使用独立 `bookmarkSaveTimer`，延迟 200ms 合并连续变更。
- 窗口关闭时先停止定时器并等待 `BookmarkStore.SaveAsync()`，失败时沿用现有退出错误处理。
- 标签保存成功、另存为、重命名后调用 `MigratePath`。

- [ ] **Step 4: 接入阅读进度刷新**

在 `Navigation.cs` 中增加：

```csharp
private static int ProgressPercent(EditorTab tab)
{
    if (tab.File.Text.Length == 0) return 0;
    int anchor = Math.Clamp(tab.ViewportAnchor ?? tab.SourceAnchor, 0, tab.File.Text.Length);
    return anchor >= tab.File.Text.Length - 1
        ? 100
        : Math.Clamp((int)Math.Round(anchor * 100d / tab.File.Text.Length), 0, 99);
}
```

在现有 `CaptureReadingViewport`、`RestoreReadingViewport`、标签切换和阅读滚动事件中统一调用 `RefreshReadingProgress(tab)`。方法只更新 `ReadingStatus.Text`、`StatusText.Text` 和书签按钮状态，不重建 `ReadingModel`。

- [ ] **Step 5: 实现书签添加、删除和跳转**

`ToggleBookmark` 使用当前阅读视口的源锚点；如果编辑态触发，则使用编辑器首个可见位置。相同文档、相同源位置存在书签时删除，否则添加。

`NavigateToBookmark`：

- 先切换到书签所属标签；若当前会话没有该标签且路径存在，调用现有 `OpenPathsAsync` 打开路径。
- 将源位置限制到当前正文长度。
- Markdown 使用现有 `EnterReading(tab, anchor, focus: true)` 和 `SnapToReadingBlockStart`。
- 纯文本使用现有 `EditorOffset`、`ScrollToCaret` 或 `RestoreReadingViewport`。
- 最后调用 `SchedulePersist()`。

- [ ] **Step 6: 运行定向恢复和 UI 测试**

运行：

```powershell
dotnet run --project tests/Qingyue.RecoveryTests/Qingyue.RecoveryTests.csproj -c Release --no-restore -- --root "$env:TEMP\qingyue-050-recovery"
dotnet run --project tests/Qingyue.UITests/Qingyue.UITests.csproj -c Release --no-restore
```

预期：进度文本、书签添加/删除、重启恢复、另存为路径迁移和原有 Markdown 位置恢复全部通过。

- [ ] **Step 7: 提交窗口状态集成**

```powershell
git add '开发空间/Windows/src/Qingyue.Windows' '开发空间/Windows/tests/Qingyue.RecoveryTests' '开发空间/Windows/tests/Qingyue.UITests'
git commit -m "feat(windows): persist reading progress and bookmarks"
```

### Task 3: 书签侧栏、标签右键菜单和最近文件状态

**Files:**

- Modify: `开发空间/Windows/src/Qingyue.Windows/MainWindow.xaml`
- Modify: `开发空间/Windows/src/Qingyue.Windows/MainWindow.Commands.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/MainWindow.xaml.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/ReaderSettings.cs` only if the selected sidebar page needs a compatible optional field
- Modify: `开发空间/Windows/tests/Qingyue.UITests/Program.cs`

**Interfaces:**

- `RecentItem` exposes:

  ```csharp
  public string Path { get; }
  public string Name { get; }
  public string Folder { get; }
  public bool Exists { get; }
  public string StateText { get; }
  ```

- `MainWindow` adds:

  ```csharp
  public void RefreshBookmarks();
  public void RemoveRecentPath(string path);
  ```

- Tab context menu receives its `EditorTab` target through `ContextMenu.Tag`; all actions call existing close/move/select methods.

- [ ] **Step 1: 添加 UI 失败测试**

在 `Qingyue.UITests/Program.cs` 中加入：

```csharp
var sidebarTabs = (TabControl)window.FindName("SidebarTabs");
Check(sidebarTabs.Items.Cast<TabItem>().Any(item => Equals(item.Header, "书签")),
    "侧栏包含书签页");
var tabHeader = FindTabHeader(window, progressTab);
tabHeader.ContextMenu.IsOpen = true;
Check(tabHeader.ContextMenu.Items.Cast<MenuItem>().Any(item => Equals(item.Header, "关闭其他标签")),
    "标签右键菜单包含关闭其他标签");
tabHeader.ContextMenu.IsOpen = false;

string missing = Path.Combine(root, "已删除.md");
File.WriteAllText(missing, "# 已删除");
await window.OpenPathsAsync([missing]);
File.Delete(missing);
window.RefreshRecent();
var recent = (ListBox)window.FindName("RecentList");
Check(recent.Items.Cast<RecentItem>().Any(item => item.Path == Path.GetFullPath(missing)
    && !item.Exists && item.StateText.Contains("不存在")), "最近文件显示失效状态");
```

- [ ] **Step 2: 运行 UI 测试确认失败**

运行：

```powershell
dotnet run --project tests/Qingyue.UITests/Qingyue.UITests.csproj -c Release --no-restore
```

预期：找不到“书签”页、标签右键菜单或失效路径状态。

- [ ] **Step 3: 增加书签侧栏**

在 `MainWindow.xaml` 的 `SidebarTabs` 增加“书签”页，包含：

- 当前文档书签列表和其他文档书签分组。
- `MouseDoubleClick`、Enter 跳转。
- 删除按钮或右键“删除书签”。
- 文档不存在时的状态文本。

保持 `OutlineList` 和“最近打开”已有控件名称不变，避免破坏当前 UI 测试和恢复流程。

- [ ] **Step 4: 增加标签右键菜单**

在标签头模板的根 `StackPanel` 设置 `ContextMenu`，菜单项使用以下固定 Tag：

```text
closeTab
closeOthers
closeRight
moveLeft
moveRight
copyPath
revealPath
```

右键菜单在未命名文档上禁用复制路径和打开目录。关闭操作继续走现有 `CloseTab` / `CloseOthers` 逻辑；关闭右侧标签先复制当前标签右侧列表，再按从右到左关闭，避免索引变化。

- [ ] **Step 5: 增强最近文件状态和移除入口**

将 `RefreshRecent` 的状态检查限制为路径和目录，不读取正文。不存在路径保留在列表中并显示 `文件不存在`；无法访问目录显示 `暂时无法访问`。

增加最近项右键“移除记录”，调用 `RemoveRecentPath` 后更新 `settings.Recent`、保存设置并刷新列表。双击失效项只显示现有通知，不新建空标签。

- [ ] **Step 6: 运行 UI 交互回归**

运行：

```powershell
dotnet run --project tests/Qingyue.UITests/Qingyue.UITests.csproj -c Release --no-restore
dotnet run --project tests/Qingyue.ExternalChangesTests/Qingyue.ExternalChangesTests.csproj -c Release --no-restore -- --repro
```

预期：书签侧栏、标签右键关闭/移动/路径操作、失效最近文件和原有阅读/编辑切换全部通过。

- [ ] **Step 7: 提交 UI 组织能力**

```powershell
git add '开发空间/Windows/src/Qingyue.Windows/MainWindow.xaml' '开发空间/Windows/src/Qingyue.Windows/MainWindow.Commands.cs' '开发空间/Windows/src/Qingyue.Windows/MainWindow.xaml.cs' '开发空间/Windows/src/Qingyue.Windows/ReaderSettings.cs' '开发空间/Windows/tests/Qingyue.UITests/Program.cs'
git commit -m "feat(windows): improve bookmarks tabs and recent files"
```

### Task 4: 外部更新后的阅读位置映射和 Markdown 连续性

**Files:**

- Modify: `开发空间/Windows/src/Qingyue.Windows/MainWindow.ExternalChanges.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/MainWindow.Navigation.cs`
- Modify: `开发空间/Windows/src/Qingyue.Windows/MainWindow.Markdown.cs`
- Modify: `开发空间/Windows/tests/Qingyue.ExternalChangesTests/RegressionTests.cs`
- Modify: `开发空间/Windows/tests/Qingyue.UITests/Program.cs`

**Interfaces:**

- `ExternalRefreshResult.Text` 继续作为新正文来源。
- `MainWindow.ExternalChanges.ApplyExternalRefresh` 在 `TryApply` 前保存旧文本和源偏移，成功后调用：

  ```csharp
  int mappedStart = TextAnchorMapper.Map(oldText, tab.File.Text, readingStart);
  int mappedEnd = TextAnchorMapper.Map(oldText, tab.File.Text, readingEnd);
  ```

- `Markdown` 预览继续使用现有 `UpdateMarkdownPreview`、`RestoreExternalMarkdownSelection` 和 `ViewportAnchor`。

- [ ] **Step 1: 增加外部更新位置失败测试**

在 `RegressionTests.cs` 增加：

```csharp
string path = Path.Combine(root, "anchor.md");
File.WriteAllText(path, "# 开始\n\n目标段落\n第二行\n\n结尾");
await window.OpenPathsAsync([path]);
var tab = window.CurrentTab!;
window.ToggleReading();
await WaitFor(() => tab.IsReading && tab.Reading is not null);
int target = tab.File.Text.IndexOf("目标段落", StringComparison.Ordinal);
tab.Preview.Selection.Select(tab.Rendered!.PointerAt(tab.Reading!.SourceToDisplay(target)),
    tab.Rendered.PointerAt(tab.Reading.SourceToDisplay(target)));
File.WriteAllText(path, "前置插入\n\n# 开始\n\n目标段落\n第二行\n\n结尾");
window.QueueExternalRefresh(tab);
await WaitFor(() => tab.File.Text.StartsWith("前置插入"));
int expected = tab.File.Text.IndexOf("目标段落", StringComparison.Ordinal);
int actual = tab.Reading!.DisplayToSource(tab.Rendered!.OffsetAt(tab.Preview.Selection.Start));
Check(Math.Abs(actual - expected) <= 1, "外部前置插入后阅读选区跟随原段落");
```

再覆盖编辑态首个可见行、Markdown 搜索选区和外部更新期间快速切换标签。

- [ ] **Step 2: 运行外部更新测试确认失败**

运行：

```powershell
dotnet run --project tests/Qingyue.ExternalChangesTests/Qingyue.ExternalChangesTests.csproj -c Release --no-restore
```

预期：外部内容虽然刷新，但选区仍停留在旧数字偏移，新增前置内容后不会回到原段落。

- [ ] **Step 3: 接入源锚点映射**

在 `ApplyExternalRefresh` 中保存：

- `oldText = tab.File.Text`
- 阅读起止源偏移
- 编辑器选区源偏移
- Markdown 预览选区和滚动偏移

`TryApply` 成功后用 `TextAnchorMapper.Map` 转换所有源偏移，再生成新的编辑器选区或阅读选区。映射后的起止值排序、限制到新文本长度，并在单点选区时保持单点。

- [ ] **Step 4: 保持 Markdown 目录、搜索和视口**

外部刷新后：

- 先重建 `ReadingModel` 和 `RenderedReading`。
- 再恢复源文选区和 `ViewportAnchor`。
- 重新绑定目录时保持 `followingOutline = true`，防止目录选择触发额外跳转。
- 有搜索输入时重新生成匹配列表，选择映射后的当前匹配。
- 纯窗口缩放、侧栏切换和阅读宽度更新继续调用 `UpdatePresentation`，禁止重新加载文件或清空 `ViewportAnchor`。

- [ ] **Step 5: 运行外部刷新和 Markdown 回归**

运行：

```powershell
dotnet run --project tests/Qingyue.ExternalChangesTests/Qingyue.ExternalChangesTests.csproj -c Release --no-restore
dotnet run --project tests/Qingyue.UITests/Qingyue.UITests.csproj -c Release --no-restore
dotnet run --project tests/Qingyue.ReaderExperienceTests/Qingyue.ReaderExperienceTests.csproj -c Release --no-restore
```

预期：插入/删除前置行、段落重写、冲突保护、Markdown 搜索、目录跳转、编辑/阅读往返和窗口缩放全部通过。

- [ ] **Step 6: 提交连续阅读能力**

```powershell
git add '开发空间/Windows/src/Qingyue.Windows/MainWindow.ExternalChanges.cs' '开发空间/Windows/src/Qingyue.Windows/MainWindow.Navigation.cs' '开发空间/Windows/src/Qingyue.Windows/MainWindow.Markdown.cs' '开发空间/Windows/tests/Qingyue.ExternalChangesTests/RegressionTests.cs' '开发空间/Windows/tests/Qingyue.UITests/Program.cs'
git commit -m "feat(windows): preserve reading anchors after external updates"
```

### Task 5: 版本、完整测试、构建和本机安装

**Files:**

- Modify: `开发空间/Windows/Directory.Build.props`
- Modify: `开发空间/Windows/installer/version.nsh`
- Modify: `开发空间/Windows/src/Qingyue.Windows/Dialogs.cs`
- Modify: `docs/test.md`
- Create: `开发空间/Windows/交付产品/轻阅-0.5.0-Windows构建1-x64-setup.exe` (generated artifact, do not commit unless repository release policy requires it)

**Interfaces:**

- 版本元数据必须一致：

  ```text
  ProductVersion = 0.5.0
  WindowsBuild = 1
  FileVersion = 0.5.0.1
  ```

- 安装器继续使用上一版的单进度条、路径选择、默认保留用户信息和完成后自动退出逻辑。

- [ ] **Step 1: 添加版本和交付失败检查**

在现有 Windows 测试或构建脚本中增加版本检查，要求：

```powershell
$assembly = [System.Reflection.AssemblyName]::GetAssemblyName($exe)
if ($assembly.Version.ToString() -ne '0.5.0.1') { throw '文件版本不正确' }
if (-not (Test-Path $installer)) { throw '安装包未生成' }
```

同时断言关于弹窗显示 `0.5.0 · Windows 构建 1`，安装器输出文件名为 `轻阅-0.5.0-Windows构建1-x64-setup.exe`。

- [ ] **Step 2: 运行已有全量测试确认版本检查失败**

运行：

```powershell
pwsh -NoProfile -File scripts/test.ps1 -Configuration Release
```

预期：功能测试继续通过，但版本检查因当前仍为 0.4.0 而失败。

- [ ] **Step 3: 更新版本元数据**

修改 `Directory.Build.props`、`installer/version.nsh` 和 `Dialogs.cs`，只更新版本常量、安装包命名和关于信息，不修改安装流程。

- [ ] **Step 4: 运行完整 Windows 测试**

运行：

```powershell
pwsh -NoProfile -File scripts/test.ps1 -Configuration Release
```

预期：核心、恢复、WPF、Markdown、图片、剪贴板、外部刷新和 UI 测试全部通过。

- [ ] **Step 5: 发布自包含 Windows 构建**

运行：

```powershell
pwsh -NoProfile -File scripts/publish.ps1 -Configuration Release
```

检查发布目录内的 `Qingyue.exe` 文件版本、产品版本和资源文件。

- [ ] **Step 6: 构建并测试安装器**

运行：

```powershell
pwsh -NoProfile -File scripts/build-installer.ps1
pwsh -NoProfile -File scripts/test-installer.ps1 -SkipBuild -NoGui
pwsh -NoProfile -File scripts/test-installer-directory.ps1
pwsh -NoProfile -File scripts/test-installer-purge.ps1
pwsh -NoProfile -File scripts/test-installer-theme.ps1
```

预期：安装器页面数量、默认保留用户信息、彻底卸载保护、目录安全和主题检查继续通过。

- [ ] **Step 7: 本机安装和功能验收**

在上一版客户端已退出后，安装到当前批准的本机测试目录，检查：

```powershell
Get-Item 'D:\Program File\Qingyue\Qingyue.exe'
Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Qingyue'
```

手工验证：

- 打开含图片的 Markdown，进入阅读、搜索、复制和书签跳转。
- 关闭并重新打开轻阅，确认标签、位置和书签恢复。
- 修改外部 Markdown，切换标签后确认显示最新正文且位置尽量保持。
- 删除一个最近文件，确认列表有状态并可移除。
- 右键标签执行关闭其他、移动、复制路径和打开目录。

- [ ] **Step 8: 写入验证记录并检查工作区**

在 `docs/test.md` 记录每条命令、退出码、版本信息、本机安装路径、书签恢复和外部刷新结果。然后运行：

```powershell
git diff --check
git status --short
git diff --stat
```

确认测试产物不在待提交变更中。

- [ ] **Step 9: 提交版本并推送**

```powershell
git add docs/superpowers/specs docs/superpowers/plans docs/test.md '开发空间/Windows/Directory.Build.props' '开发空间/Windows/installer/version.nsh' '开发空间/Windows/src/Qingyue.Windows/Dialogs.cs' '开发空间/Windows/src/Qingyue.Core' '开发空间/Windows/src/Qingyue.Windows' '开发空间/Windows/tests'
git commit -m "feat(windows): deliver 0.5.0 reading continuity"
git push origin feat/windows-client
git status --short --branch
git log -1 --oneline --decorate
```

预期：`feat/windows-client` 与远程同步，最终工作区只保留被 `.gitignore` 忽略的构建产物。

## 自检结果

### 规格覆盖

- 阅读进度和现有位置恢复：Task 2。
- 书签模型、独立持久化、侧栏、跳转、删除和路径迁移：Task 1、Task 2、Task 3。
- 标签右键菜单、关闭/移动/路径操作：Task 3。
- 最近文件失效状态和清理：Task 3。
- 外部更新后的段落锚点映射和冲突保护：Task 1、Task 4。
- Markdown 目录、搜索、编辑/预览往返、缩放和图片连续性：Task 4。
- 版本、构建、本机安装和交付证据：Task 5。

### 占位符检查

每个任务均给出具体文件、接口、失败测试、运行命令和提交边界，没有依赖未定义的后续步骤。

### 类型一致性检查

- `DocumentBookmark` 在 Task 1 定义，Task 2/3 只通过该类型读写。
- `BookmarkStore.SaveAsync()` 返回 `BookmarkSaveResult`，Task 2 的退出流程按 `Errors` 处理。
- `TextAnchorMapper.Map(string, string, int)` 在 Task 1 定义，Task 4 直接使用。
- `ReadingStatus`、`SidebarTabs`、`RecentList` 和标签头命名沿用现有 XAML 控件，Task 2/3 的测试与实现一致。
- `ExternalRefreshResult.Text` 已存在，Task 4 不新增重复正文属性。

## 执行选择

计划完成并保存到 `docs/superpowers/plans/2026-11-12-windows-050-reading-continuity.md`。执行有两种方式：

1. **Subagent-Driven（推荐）**：每个任务使用新的子任务执行，任务间做代码和测试审查。
2. **Inline Execution**：在当前会话按任务批量执行，并在每个任务后做验证检查点。
