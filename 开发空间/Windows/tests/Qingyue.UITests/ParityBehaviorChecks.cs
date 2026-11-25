using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Qingyue.Core;

namespace Qingyue.Windows;

internal static class ParityBehaviorChecks
{
    public static async Task Run(MainWindow host, string root, Action<bool, string> check)
    {
        await CheckCurrentBookmarkMenu(root, check);
        await CheckAnnotationPanelOrder(root, check);
        await CheckWorkspaceParity(root, check);
        await CheckReadingWidthParity(root, check);
        CheckHelpVersionParity(check);
        await CheckTabCloseButton(root, check);
        await CheckReadingListParity(root, check);
        await CheckGlobalSearchLimit(root, check);
        await CheckTabPresentationLifecycle(root, check);
        await CheckPerTabSplitPreview(root, check);
        await CheckRecentPositionParity(root, check);
        await ModalSafetyChecks.Run(root, check);
    }

    private static async Task CheckCurrentBookmarkMenu(string root, Action<bool, string> check)
    {
        string profile = Path.Combine(root, "parity-bookmarks");
        MainWindow? window = null;
        try
        {
            window = await OpenWindow(profile);
            var tab = window.NewDocument("alpha\nbeta\ngamma");
            var store = Field<BookmarkStore>(window, "bookmarks");
            var first = store.Add(tab.File, 0, "alpha");
            await Task.Delay(20);
            var second = store.Add(tab.File, 0, "alpha again");
            await Task.Delay(20);
            var third = store.Add(tab.File, tab.File.Text.Length - 1, "gamma");
            check(first.GetType().GetProperty("CreatedAt") is not null,
                "书签：持久化模型包含创建时间");
            window.RefreshBookmarks();

            Program.ClickAction(window, "currentBookmarks");
            await Pump(window);
            var toolbar = (FrameworkElement)window.FindName("ContextToolbar");
            var menu = toolbar.ContextMenu;
            check(menu is { IsOpen: true },
                "当前文档书签：入口打开文档自己的书签菜单，而不是全局侧栏");
            if (menu is not null)
            {
                var items = menu.Items.OfType<MenuItem>().ToArray();
                check(items.Length == 3 &&
                    items.All(item => item.Tag is DocumentBookmark) &&
                    items.Select(item => ((DocumentBookmark)item.Tag!).SourceAnchor)
                        .SequenceEqual([0, 0, tab.File.Text.Length - 1]),
                    "当前文档书签：按源位置和创建时间排列，并只包含当前文档");
                var last = items.Single(item =>
                    ((DocumentBookmark)item.Tag!).SourceAnchor == tab.File.Text.Length - 1);
                tab.Editor.CaretIndex = 0;
                last.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await Pump(window);
                check(tab.Editor.CaretIndex == tab.Editor.Text.Length - 1,
                    "当前文档书签：点击菜单项跳转到保存的源位置");
            }

            window.NewDocument("没有书签");
            Program.ClickAction(window, "currentBookmarks");
            await Pump(window);
            menu = ((FrameworkElement)window.FindName("ContextToolbar")).ContextMenu;
            check(menu is { IsOpen: true } &&
                menu.Items.OfType<MenuItem>().SingleOrDefault() is
                    { Header: "暂无书签 · Ctrl+D 添加" },
                "当前文档书签：空菜单使用与 macOS 对齐的中文提示");

            var repairTab = window.NewDocument("目标段落\n第二行");
            var repairBookmark = store.Add(repairTab.File, 0, "目标段落");
            repairTab.Editor.Text = "前置插入\n\n目标段落\n第二行";
            int expectedAnchor = repairTab.File.Text.IndexOf("目标段落", StringComparison.Ordinal);
            check(await WaitUntil(() =>
                    store.For(repairTab.File).Single(item => item.Id == repairBookmark.Id).SourceAnchor == expectedAnchor,
                TimeSpan.FromSeconds(4)),
                "当前文档书签：正文前置插入后按源位置自动修复");
            await window.PersistWorkspaceAsync();
            var reloadedBookmarks = new BookmarkStore(profile);
            await reloadedBookmarks.LoadAsync();
            check(reloadedBookmarks.Items.Single(item => item.Id == repairBookmark.Id).SourceAnchor == expectedAnchor,
                "当前文档书签：修复后的锚点实际写入 Bookmarks.json");
        }
        finally
        {
            window?.CloseForShutdown();
        }
    }

    private static async Task CheckAnnotationPanelOrder(string root, Action<bool, string> check)
    {
        string profile = Path.Combine(root, "parity-annotations");
        MainWindow? window = null;
        Window? panel = null;
        Guid selectedId;
        try
        {
            window = await OpenWindow(profile);
            var tab = window.NewDocument("0123456789");
            var store = Field<AnnotationStore>(window, "annotations");
            store.Add(tab.File, new AnnotationDraft(
                tab.File.Id, tab.File.Path, 8, 9, "8", "", ""));
            await Task.Delay(20);
            store.Add(tab.File, new AnnotationDraft(
                tab.File.Id, tab.File.Path, 1, 2, "1", "", ""));

            Invoke(window, "ShowAnnotationPanel");
            await WaitFor(() => Application.Current.Windows.OfType<Window>()
                .Any(item => item.IsVisible && item.Title.Contains("批注", StringComparison.Ordinal)));
            panel = Application.Current.Windows.OfType<Window>()
                .First(item => item.IsVisible && item.Title.Contains("批注", StringComparison.Ordinal));
            await Pump(panel);
            check(panel.Title == "文档批注" && Nearly(panel.MinHeight, 670),
                "批注窗口：未命名文档标题为“文档批注”，最小高度 670");
            var list = Descendants(panel).OfType<ListView>().First();
            var rows = list.ItemsSource.Cast<object>().ToArray();
            var annotationProperty = rows[0].GetType().GetProperty("Annotation")
                ?? throw new MissingMemberException(rows[0].GetType().Name, "Annotation");
            var sourceStarts = rows.Select(row =>
                ((DocumentAnnotation)annotationProperty.GetValue(row)!).SourceStart).ToArray();
            check(sourceStarts.SequenceEqual([1, 8]),
                "批注面板：按源位置升序显示，同一位置再按创建时间");
            check(AutomationProperties.GetName(list) == "当前文档批注列表",
                "批注面板：列表提供可访问名称");
            check(list.View is GridView annotationGrid &&
                annotationGrid.Columns.Select(column => column.Header?.ToString())
                    .SequenceEqual(["摘录", "位置状态"]),
                "批注面板：两列表头严格匹配 macOS");

            var quote = Descendants(panel).OfType<TextBox>()
                .Single(item => AutomationProperties.GetName(item) == "批注摘录原文");
            var note = Descendants(panel).OfType<TextBox>()
                .Single(item => AutomationProperties.GetName(item) == "批注备注");
            var tags = Descendants(panel).OfType<TextBox>()
                .Single(item => AutomationProperties.GetName(item) == "批注标签");
            var color = Descendants(panel).OfType<ComboBox>()
                .Single(item => AutomationProperties.GetName(item) == "批注颜色");
            var status = Descendants(panel).OfType<TextBlock>()
                .Single(item => AutomationProperties.GetName(item) == "批注操作状态");
            var save = Descendants(panel).OfType<Button>()
                .Single(item => Equals(item.Content, "保存批注"));
            var delete = Descendants(panel).OfType<Button>()
                .Single(item => Equals(item.Content, "删除批注"));
            var jump = Descendants(panel).OfType<Button>()
                .Single(item => Equals(item.Content, "回到原文"));
            check(quote.IsReadOnly && note.IsEnabled && tags.IsEnabled && color.IsEnabled,
                "批注面板：摘录只读，选中时备注、标签和颜色可编辑");
            check(note.MaxLength == 0 && tags.MaxLength == 0,
                "批注面板：输入框不静默截断，超出长度交给存储层拒绝");

            list.SelectedIndex = -1;
            await Pump(panel);
            check(!note.IsEnabled && !tags.IsEnabled && !color.IsEnabled &&
                !save.IsEnabled && !delete.IsEnabled && !jump.IsEnabled,
                "批注面板：无选中时禁用备注、标签、颜色、保存、删除和回源");

            list.SelectedIndex = 0;
            await Pump(panel);
            selectedId = ((DocumentAnnotation)annotationProperty.GetValue(list.SelectedItem)!).Id;
            note.Text = "更新后的备注";
            tags.Text = "重点，复查, 带 空格";
            color.SelectedValue = AnnotationColor.Green;
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(await WaitUntil(() => store.Items.Single(item => item.Id == selectedId).Note == "更新后的备注",
                TimeSpan.FromSeconds(4)),
                "批注面板：明确保存按钮更新备注");
            check(store.Items.Single(item => item.Id == selectedId).Tags
                    .SequenceEqual(["重点", "复查", "带 空格"]) &&
                store.Items.Single(item => item.Id == selectedId).Color == AnnotationColor.Green,
                "批注面板：标签只按半角/全角逗号拆分，颜色同步更新");
            await WaitFor(() => status.Text.Contains("已保存", StringComparison.Ordinal));
            check(status.Text.Contains("已保存", StringComparison.Ordinal),
                "批注面板：保存成功状态不会被列表刷新覆盖");
            var persistedStore = new AnnotationStore(profile);
            await persistedStore.LoadAsync();
            check(persistedStore.Items.Single(item => item.Id == selectedId).Note == "更新后的备注",
                "批注面板：保存结果实际写入 Annotations.json");
            check(window.CurrentAnnotations(tab).Single(item => item.Id == selectedId).Note == "更新后的备注",
                "批注面板：保存后宿主批注数据立即刷新");

            note.Text = new string('字', 4001);
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => status.Text.Contains("4000", StringComparison.Ordinal));
            check(store.Items.Single(item => item.Id == selectedId).Note == "更新后的备注" &&
                status.Text.Contains("未保存", StringComparison.Ordinal),
                "批注面板：超长备注显示错误且不改动已保存批注");

            check(!panel.InputBindings.OfType<KeyBinding>().Any(item =>
                    item.Key == Key.S && item.Modifiers == ModifierKeys.Control),
                "批注面板：Ctrl+S 只保留一条触发路径");
            note.Text = "快捷键保存";
            var handled = (bool)Invoke(panel, "HandleShortcut", Key.S, ModifierKeys.Control)!;
            check(handled, "批注面板：Ctrl+S 快捷键被窗口处理");
            check(await WaitUntil(() =>
                    store.Items.Single(item => item.Id == selectedId).Note == "快捷键保存",
                TimeSpan.FromSeconds(4)),
                "批注面板：Ctrl+S 保存到批注存储");
            check(await WaitUntil(() =>
                    !Field<bool>(panel, "saving") &&
                    status.Text.Contains("已保存", StringComparison.Ordinal),
                TimeSpan.FromSeconds(4)),
                "批注面板：快捷键保存完成后再接受删除操作");

            int beforeCancel = store.Items.Count;
            panel.GetType().GetProperty("ConfirmDelete",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .SetValue(panel, new Func<bool>(() => false));
            await (Task)Invoke(panel, "DeleteSelectedAsync")!;
            check(store.Items.Count == beforeCancel && store.Items.Any(item => item.Id == selectedId),
                "批注面板：取消删除确认时保留原批注");

            string annotationsPath = Path.Combine(profile, "Annotations.json");
            panel.GetType().GetProperty("ConfirmDelete",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .SetValue(panel, new Func<bool>(() => true));
            File.SetAttributes(annotationsPath, File.GetAttributes(annotationsPath) | FileAttributes.ReadOnly);
            try
            {
                await (Task)Invoke(panel, "DeleteSelectedAsync")!;
            }
            finally
            {
                File.SetAttributes(annotationsPath, File.GetAttributes(annotationsPath) & ~FileAttributes.ReadOnly);
            }
            check(store.Items.Any(item => item.Id == selectedId),
                "批注面板：保存失败会重新加载原批注（实际 " +
                store.Items.Count + " 条，目标 " + selectedId + "）");
            check(status.Text.StartsWith("删除未能保存：", StringComparison.Ordinal),
                "批注面板：删除未保存时显示明确原因（实际：" + status.Text + "）");

            list.SelectedItem = list.ItemsSource.Cast<object>().Single(row =>
                ((DocumentAnnotation)annotationProperty.GetValue(row)!).Id == selectedId);
            await Pump(panel);
            await (Task)Invoke(panel, "DeleteSelectedAsync")!;
            bool removedAfterRetry = store.Items.All(item => item.Id != selectedId);
            bool successReported = status.Text.Contains("批注已删除。", StringComparison.Ordinal);
            check(removedAfterRetry,
                "批注面板：确认后删除批注（实际状态：" + status.Text + "）");
            check(successReported,
                "批注面板：删除成功后报告成功（实际状态：" + status.Text + "）");

            string documentPath = Path.Combine(profile, "带批注的文档.md");
            check(window.SaveTabToPath(tab, documentPath), "批注窗口：验证带路径文档可以保存");
            Invoke(window, "ShowAnnotationPanel");
            await Pump(panel);
            check(panel.Title == "文档批注 · " + Path.GetFileName(documentPath),
                "批注窗口：标题包含当前文档文件名");
        }
        finally
        {
            panel?.Close();
            window?.CloseForShutdown();
        }
    }

    private static async Task CheckWorkspaceParity(string root, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "parity-workspace");
        string workspaceA = Path.Combine(folder, "workspace-a");
        string workspaceB = Path.Combine(folder, "workspace-b");
        Directory.CreateDirectory(workspaceA);
        Directory.CreateDirectory(workspaceB);
        File.WriteAllText(Path.Combine(workspaceA, "alpha.md"), "# alpha");
        File.WriteAllText(Path.Combine(workspaceA, "beta.txt"), "beta");
        File.WriteAllText(Path.Combine(workspaceA, "gamma.json"), "{}");

        MainWindow? window = null;
        try
        {
            window = await OpenWindow(Path.Combine(folder, "profile"));
            var settings = Field<ReaderSettings>(window, "settings");
            settings.Sidebar = false;
            Invoke(window, "ApplySidebar");
            await (Task)Invoke(window, "ActivateWorkspaceAsync", workspaceA)!;
            await Pump(window);
            var sidebar = (Border)window.FindName("Sidebar");
            var sidebarTabs = (TabControl)window.FindName("SidebarTabs");
            check(sidebar.Visibility == Visibility.Visible &&
                Equals((sidebarTabs.SelectedItem as TabItem)?.Header, "工作区"),
                "工作区：打开目录后自动显示侧栏并切换到工作区页");

            Invoke(window, "ToggleFavoriteDirectory", workspaceA);
            var picker = (ComboBox)window.FindName("WorkspaceFavoritePicker");
            check(settings.FavoriteDirectories.Contains(workspaceA, StringComparer.OrdinalIgnoreCase) &&
                picker.Items.Cast<WorkspaceFavoriteOption>().Any(item =>
                    string.Equals(item.Path, workspaceA, StringComparison.OrdinalIgnoreCase)),
                "工作区：可收藏当前目录并出现在收藏目录下拉列表");

            await (Task)Invoke(window, "ActivateWorkspaceAsync", workspaceB)!;
            Invoke(window, "ToggleFavoriteDirectory", workspaceB);
            check(settings.FavoriteDirectories.Count >= 2 &&
                string.Equals(settings.FavoriteDirectories[^1], workspaceB,
                    StringComparison.OrdinalIgnoreCase),
                "工作区：新增收藏追加到末尾，与 macOS 一致");
            var favorite = picker.Items.Cast<WorkspaceFavoriteOption>().Single(item =>
                string.Equals(item.Path, workspaceA, StringComparison.OrdinalIgnoreCase));
            picker.SelectedItem = favorite;
            check(await WaitUntil(() =>
                string.Equals(settings.WorkspaceRoot, workspaceA, StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(4)),
                "工作区：从收藏目录下拉列表切换工作区");

            Invoke(window, "ToggleFavoriteDirectory", workspaceA);
            check(!picker.Items.Cast<WorkspaceFavoriteOption>().Any(item =>
                    string.Equals(item.Path, workspaceA, StringComparison.OrdinalIgnoreCase)),
                "工作区：可取消收藏并立即从下拉列表移除");

            var extensionFilter = (TextBox)window.FindName("WorkspaceExtensionFilter");
            extensionFilter.Text = "md";
            var tree = (TreeView)window.FindName("WorkspaceTree");
            check(await WaitUntil(() =>
                {
                    if (tree.Items.Count == 0 || tree.Items[0] is not WorkspaceNode node) return false;
                    var files = Flatten(node).Where(item => !item.IsDirectory)
                        .Select(item => item.Name).ToArray();
                    return files.SequenceEqual(["alpha.md"]);
                }, TimeSpan.FromSeconds(4)),
                "工作区：类型过滤支持 macOS 的扩展名列表语法");
            extensionFilter.Text = "";

            settings.FavoriteDirectories.Clear();
            for (int index = 1; index <= 51; index++)
            {
                string path = Path.Combine(folder, "favorite-" + index.ToString("00"));
                Directory.CreateDirectory(path);
                if (index <= 50) settings.FavoriteDirectories.Add(path);
            }
            Invoke(window, "ToggleFavoriteDirectory", Path.Combine(folder, "favorite-51"));
            check(settings.FavoriteDirectories.Count == 50 &&
                ((TextBlock)window.FindName("StatusText")).Text.Contains("最多收藏 50 个目录"),
                "工作区：收藏目录上限与 macOS 一致为 50");
            await window.PersistWorkspaceAsync();
            check(ReaderSettings.Load(Path.Combine(folder, "profile")).FavoriteDirectories.Count == 50,
                "工作区：收藏目录与工作区根路径持久化");

            string missingFavorite = Path.Combine(folder, "missing-favorite");
            settings.FavoriteDirectories.RemoveAt(settings.FavoriteDirectories.Count - 1);
            settings.FavoriteDirectories.Add(missingFavorite);
            Invoke(window, "RefreshWorkspaceFavorites");
            picker.SelectedItem = picker.Items.Cast<WorkspaceFavoriteOption>().Single(item =>
                string.Equals(item.Path, missingFavorite, StringComparison.OrdinalIgnoreCase));
            check(await WaitUntil(() =>
                ((TextBlock)window.FindName("StatusText")).Text.Contains("不存在", StringComparison.Ordinal),
                TimeSpan.FromSeconds(4)),
                "工作区：失效收藏目录保留并可选择，扫描后显示真实错误");
            check(settings.FavoriteDirectories.Contains(missingFavorite, StringComparer.OrdinalIgnoreCase),
                "工作区：选择失效收藏后不会自动删除该收藏");
        }
        finally
        {
            window?.CloseForShutdown();
        }
    }

    private static async Task CheckReadingWidthParity(string root, Action<bool, string> check)
    {
        MainWindow? window = null;
        try
        {
            window = await OpenWindow(Path.Combine(root, "parity-reading-width"));
            var settings = Field<ReaderSettings>(window, "settings");
            settings.ReadingWidth = 840;
            Invoke(window, "ExecuteAction", "width:520");
            await Pump(window);
            check(Nearly(settings.ReadingWidth, 680),
                "阅读宽度：菜单不接受设置面板专用的 520 动作");

            settings.ReadingWidth = 680;
            Program.ClickAction(window, "width:560");
            await Pump(window);
            check(Nearly(settings.ReadingWidth, 560),
                "阅读宽度：菜单窄档使用 macOS 的 560");

            Program.ClickAction(window, "width:900");
            await Pump(window);
            check(Nearly(settings.ReadingWidth, 900),
                "阅读宽度：菜单宽档使用 macOS 的 900");

            Program.ClickAction(window, "width:5000");
            await Pump(window);
            check(Nearly(settings.ReadingWidth, 1_000_000),
                "阅读宽度：菜单“铺满窗口”把 5000 转换为 macOS 的全宽值 1,000,000");

            var invalidSelection = new ReadingAppearanceSelection(
                ReadingFontCatalog.Fallback,
                ReadingPresentation.NaturalLineHeightFactor + 6d / 14d,
                13,
                ReaderThemeKind.Classic,
                AppIconThemeKind.Sky,
                LineSpacing: 6,
                ThemeId: "classic",
                Dark: false,
                FollowSystem: false,
                LineNumbers: true,
                Wrap: true,
                FontSize: 14,
                ReadingWidth: 900);
            invalidSelection.ApplyTo(settings);
            check(Nearly(settings.ReadingWidth, 680) &&
                Nearly(ReaderSettings.NormalizeReadingWidth(double.NaN), 680),
                "阅读宽度：外观对象与非法数值均归一化为 macOS 四档");

            var appearance = ReadingAppearanceDialog.Create(window, settings, _ => { });
            appearance.Window.Show();
            await Pump(appearance.Window);
            var picker = Descendants(appearance.Window).OfType<ComboBox>()
                .First(item => item.Name == "ReadingWidthPicker");
            var values = picker.Items.Cast<object>()
                .Select(item => (double)item.GetType().GetProperty("Value")!.GetValue(item)!)
                .ToArray();
            check(values.SequenceEqual([520d, 680d, 840d, 1_000_000d]),
                "阅读宽度：设置窗口保留 520 / 680 / 840 / 1,000,000 四档");
            appearance.Window.Close();
        }
        finally
        {
            window?.CloseForShutdown();
        }
    }

    private static void CheckHelpVersionParity(Action<bool, string> check)
    {
        var dialogs = typeof(MainWindow).Assembly.GetType("Qingyue.Windows.Dialogs")
            ?? throw new TypeLoadException("Qingyue.Windows.Dialogs");
        string caption = (string)(dialogs
            .GetMethod("VersionCaption", BindingFlags.Static | BindingFlags.NonPublic)?
            .Invoke(null, null) ?? "");
        var version = typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0, 0);
        check(caption == $"{version.Major}.{version.Minor}.{version.Build} · Windows 构建 1",
            "帮助与关于窗口版本动态读取程序集版本");
    }

    private static async Task CheckTabCloseButton(string root, Action<bool, string> check)
    {
        string profile = Path.Combine(root, "parity-close");
        MainWindow? window = null;
        try
        {
            window = await OpenWindow(profile);
            var tab = window.CurrentTab!;
            await Pump(window);
            var close = Descendants(window).OfType<Button>()
                .FirstOrDefault(button => ReferenceEquals(button.Tag, tab));
            check(close is not null, "标签关闭：标签页存在可用关闭按钮");
            if (close is null) return;
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(await WaitUntil(() => !window.IsVisible, TimeSpan.FromSeconds(4)),
                "标签关闭：关闭最后一个干净标签会关闭窗口，而不是新建空标签");
        }
        finally
        {
            if (window?.IsVisible == true) window.CloseForShutdown();
        }
    }

    private static async Task CheckReadingListParity(string root, Action<bool, string> check)
    {
        string rootFolder = Path.Combine(root, "parity-reading-list");
        string workspace = Path.Combine(rootFolder, "workspace");
        Directory.CreateDirectory(workspace);

        MainWindow? saveWindow = null;
        MainWindow? openWindow = null;
        MainWindow? listedWindow = null;
        MainWindow? progressWindow = null;
        try
        {
            string saveProfile = Path.Combine(rootFolder, "save-profile");
            string savePath = Path.Combine(workspace, "保存不建档.txt");
            saveWindow = await OpenWindow(saveProfile);
            var saveTab = saveWindow.NewDocument("第一行\n第二行");
            saveWindow.ToggleReading();
            check(saveWindow.SaveTabToPath(saveTab, savePath),
                "阅读清单：验证阅读态文档可以保存");
            await saveWindow.PersistWorkspaceAsync();
            var saveStore = new ReadingListStore(saveProfile);
            await saveStore.LoadAsync();
            check(saveStore.Find(savePath) is null,
                "阅读清单：保存或普通阅读不会自动创建阅读清单记录");

            string openProfile = Path.Combine(rootFolder, "open-profile");
            string openPath = Path.Combine(workspace, "普通打开不建档.txt");
            File.WriteAllText(openPath, "普通打开");
            openWindow = await OpenWindow(openProfile);
            await openWindow.OpenPathsAsync([openPath]);
            await openWindow.PersistWorkspaceAsync();
            var openStore = new ReadingListStore(openProfile);
            await openStore.LoadAsync();
            check(openStore.Find(openPath) is null,
                "阅读清单：普通打开不会自动创建阅读清单记录");

            string listedProfile = Path.Combine(rootFolder, "listed-profile");
            string listedPath = Path.Combine(workspace, "清单锚点.md");
            string listedText = string.Join("\n",
                Enumerable.Range(1, 100).Select(index => $"第 {index} 行"));
            File.WriteAllText(listedPath, listedText);
            int expectedAnchor = listedText.IndexOf("第 70 行", StringComparison.Ordinal);
            var listedStore = new ReadingListStore(listedProfile);
            var entry = listedStore.Upsert(listedPath, 70, expectedAnchor, isPinned: true);
            check((await listedStore.SaveAsync()).Errors.Count == 0,
                "阅读清单：验证锚点记录可以写入磁盘");

            listedWindow = await OpenWindow(listedProfile);
            await (Task)Invoke(listedWindow, "OpenReadingListEntryAsync", entry)!;
            await Pump(listedWindow);
            var listedTab = listedWindow.CurrentTab!;
            check(listedTab.IsReading && listedTab.SourceAnchor == expectedAnchor,
                $"阅读清单：从清单打开时显式恢复条目锚点（阅读={listedTab.IsReading}，实际={listedTab.SourceAnchor}，期望={expectedAnchor}）");

            string progressProfile = Path.Combine(rootFolder, "progress-profile");
            string progressPath = Path.Combine(workspace, "进度不自动已读.txt");
            string progressText = string.Join("\n",
                Enumerable.Range(1, 100).Select(index => $"进度第 {index} 行"));
            File.WriteAllText(progressPath, progressText);
            var progressStore = new ReadingListStore(progressProfile);
            progressStore.Upsert(progressPath, 10, 0);
            check((await progressStore.SaveAsync()).Errors.Count == 0,
                "阅读清单：验证未读进度记录可以写入磁盘");
            progressWindow = await OpenWindow(progressProfile);
            await progressWindow.OpenPathsAsync([progressPath]);
            var progressTab = progressWindow.CurrentTab!;
            progressTab.SourceAnchor = progressText.Length - 1;
            progressTab.Editor.CaretIndex = progressText.Length;
            Invoke(progressWindow, "UpdateReadingListProgress", progressTab);
            await progressWindow.PersistWorkspaceAsync();
            await progressStore.LoadAsync();
            check(progressStore.Find(progressPath)?.IsRead == false,
                "阅读清单：更新到末尾只记录进度，不自动标记已读");
        }
        finally
        {
            saveWindow?.CloseForShutdown();
            openWindow?.CloseForShutdown();
            listedWindow?.CloseForShutdown();
            progressWindow?.CloseForShutdown();
        }
    }

    private static async Task CheckGlobalSearchLimit(string root, Action<bool, string> check)
    {
        string workspace = Path.Combine(root, "parity-global-search");
        Directory.CreateDirectory(workspace);
        for (int index = 1; index <= 3; index++)
            File.WriteAllText(Path.Combine(workspace, $"result-{index}.txt"), "needle needle");

        var engine = new GlobalSearchEngine();
        var result = await engine.SearchAsync(workspace, "needle", new GlobalSearchOptions(
            MaximumMatches: 2, BuildReplacementPreview: false));
        check(result.Matches.Count == 2 &&
            result.Files.Sum(item => item.MatchCount) == 2 &&
            result.Warnings.Any(item => item.Contains("达到上限", StringComparison.Ordinal)),
            "全局搜索：普通搜索达到 10,000 级上限时截断并返回部分结果");

        string previewWorkspace = Path.Combine(root, "parity-global-search-preview");
        Directory.CreateDirectory(previewWorkspace);
        File.WriteAllText(Path.Combine(previewWorkspace, "a.txt"), "needle needle needle");
        await ThrowsAsync<InvalidOperationException>(() => engine.SearchAsync(previewWorkspace, "needle",
            new GlobalSearchOptions(
                MaximumMatches: 2,
                Replacement: "value",
                BuildReplacementPreview: true)),
            "全局搜索：替换预览超限仍明确失败，不能把截断结果当成完整成功");

        var clampedContext = await engine.SearchAsync(workspace, "needle",
            new GlobalSearchOptions(ContextLines: 100, BuildReplacementPreview: false));
        check(clampedContext.Matches.Count == 6,
            "全局搜索：上下文行数按 macOS 钳制到 0 到 10，而不是拒绝搜索");
        var zeroLimit = await engine.SearchAsync(workspace, "needle",
            new GlobalSearchOptions(MaximumMatches: 0, BuildReplacementPreview: false));
        check(zeroLimit.Matches.Count == 0 && zeroLimit.Files.Count == 0,
            "全局搜索：非正结果上限返回空结果，与 macOS 一致");
        var zeroCapacity = await engine.SearchAsync(workspace, "needle",
            new GlobalSearchOptions(MaximumFileBytes: 0, BuildReplacementPreview: false));
        check(zeroCapacity.Matches.Count == 0,
            "全局搜索：非正文件容量上限返回空结果，与 macOS 一致");
        string emptyWorkspace = Path.Combine(root, "parity-global-search-empty");
        Directory.CreateDirectory(emptyWorkspace);
        await ThrowsAsync<ArgumentException>(() => engine.SearchAsync(emptyWorkspace, "(",
            new GlobalSearchOptions(Search: new SearchOptions(Regex: true),
                BuildReplacementPreview: false)),
            "全局搜索：空工作区也先校验非法正则，不伪装成无匹配成功");
    }

    private static async Task CheckTabPresentationLifecycle(string root, Action<bool, string> check)
    {
        string profile = Path.Combine(root, "parity-presentation-lifecycle");
        MainWindow? window = null;
        try
        {
            window = await OpenWindow(profile);
            string firstPath = Path.Combine(root, "presentation-first.md");
            string secondPath = Path.Combine(root, "presentation-second.md");
            await File.WriteAllTextAsync(firstPath,
                string.Join("\n\n", Enumerable.Range(1, 60).Select(index => $"# 第一篇 {index}\n\n正文 {index}。")));
            await File.WriteAllTextAsync(secondPath,
                string.Join("\n\n", Enumerable.Range(1, 60).Select(index => $"# 第二篇 {index}\n\n正文 {index}。")));

            await window.OpenPathsAsync([firstPath]);
            await WaitFor(() => window.CurrentTab is { IsReading: true, Rendered: not null });
            var first = window.CurrentTab!;
            await window.OpenPathsAsync([secondPath]);
            await WaitFor(() => window.CurrentTab is { IsReading: true, Rendered: not null });
            var second = window.CurrentTab!;
            check(first.IsReading && first.Rendered is null && first.Reading is null,
                "标签生命周期：切换到另一标签后释放非当前阅读树，与 macOS 一致");

            window.SelectTab(first);
            await WaitFor(() => first.Rendered is not null);
            check(second.IsReading && second.Rendered is null && second.Reading is null,
                "标签生命周期：切回旧标签时只重建当前阅读树");

            await window.OpenPathsAsync([firstPath, secondPath]);
            await WaitFor(() => window.Tabs.Count(tab => tab.Rendered is not null) == 1);
            check(first.IsReading && second.IsReading &&
                window.Tabs.Count(tab => tab.Rendered is not null) == 1,
                "标签生命周期：批量打开后所有 Markdown 保持阅读意图，仅当前标签保留渲染树");
        }
        finally
        {
            window?.CloseForShutdown();
        }
    }

    private static async Task CheckPerTabSplitPreview(string root, Action<bool, string> check)
    {
        string profile = Path.Combine(root, "parity-per-tab-split");
        MainWindow? window = null;
        try
        {
            window = await OpenWindow(profile);
            var first = window.NewDocument("第一个文档\n\n分屏内容 A");
            window.RenameTab(first, "first.md");
            Invoke(window, "ToggleMarkdownPreview");
            await Pump(window);
            check(first.SplitPreview && window.FindName("DocumentHost") is ContentControl
                { Content: Grid },
                "分屏状态：第一个 Markdown 标签开启分屏");

            var second = window.NewDocument("第二个文档\n\n分屏内容 B");
            window.RenameTab(second, "second.md");
            Invoke(window, "ToggleMarkdownPreview");
            await Pump(window);
            check(first.SplitPreview && second.SplitPreview,
                "分屏状态：每个标签独立保存分屏意图，不覆盖其他标签");

            window.SelectTab(first);
            await Pump(window);
            check(first.SplitPreview && window.FindName("DocumentHost") is ContentControl { Content: Grid },
                "分屏状态：切回第一个标签恢复其分屏，而不是使用第二个标签状态");

            window.SelectTab(second);
            await Pump(window);
            check(second.SplitPreview && window.FindName("DocumentHost") is ContentControl { Content: Grid },
                "分屏状态：切回第二个标签仍恢复自己的分屏");
        }
        finally
        {
            window?.CloseForShutdown();
        }
    }

    private static async Task CheckRecentPositionParity(string root, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "parity-recent-position");
        Directory.CreateDirectory(folder);
        string documentPath = Path.Combine(folder, "position.md");
        string document = string.Join('\n', Enumerable.Range(1, 120)
            .Select(index => $"# 第 {index} 段\n正文 {index}")) + "\n";
        File.WriteAllText(documentPath, document);
        MainWindow? window = null;
        try
        {
            string profile = Path.Combine(folder, "profile");
            window = await OpenWindow(profile);
            await window.OpenPathsAsync([documentPath]);
            var reading = window.CurrentTab!;
            await WaitFor(() => reading.IsReading && reading.Reading is not null && reading.Rendered is not null);
            int desiredAnchor = reading.Reading!.Headings[72].SourceStart;
            var outline = (ListBox)window.FindName("OutlineList");
            outline.SelectedItem = outline.Items.Cast<object>().First(item =>
                item.GetType().GetProperty("SourceStart")?.GetValue(item) is int sourceStart &&
                sourceStart == desiredAnchor);
            await Pump(window);
            Invoke(window, "CaptureReadingViewport", reading);
            int capturedAnchor = Property<int?>(reading, "ViewportAnchor") ?? desiredAnchor;

            var other = window.NewDocument("切换标签触发位置保存");
            await window.PersistWorkspaceAsync();
            var saved = ReaderSettings.Load(profile);
            bool foundPosition = saved.RecentPositions.TryGetValue(documentPath, out var position);
            check(foundPosition && position is not null &&
                position.IsReading &&
                position.SelectionStart >= 0 &&
                position.ReadingViewportAnchor > 0,
                "最近位置：切换标签后按绝对路径保存阅读状态、选区和阅读锚点" +
                $"（存在={foundPosition}，阅读={position?.IsReading}，" +
                $"选区={position?.SelectionStart}，锚点={position?.ReadingViewportAnchor}）");

            window.CloseForShutdown();
            window = await OpenWindow(profile);
            await window.OpenPathsAsync([documentPath]);
            var restored = window.CurrentTab!;
            await WaitFor(() => restored.IsReading && restored.Rendered is not null);
            await Pump(window);
            check(restored.IsReading &&
                Property<int?>(restored, "ViewportAnchor") is { } restoredAnchor &&
                Math.Abs(restoredAnchor - capturedAnchor) <= 1,
                "最近位置：新窗口重新打开时恢复阅读模式和源码视口锚点");

            restored.Editor.AppendText("\n未保存的尾部内容\n");
            await window.PersistWorkspaceAsync();
            string drafts = Path.Combine(profile, "Drafts");
            var draftFilesBefore = Directory.Exists(drafts)
                ? Directory.GetFiles(drafts, "*.json").Order().ToArray()
                : [];
            int tabCountBefore = window.Tabs.Count;
            string textBefore = restored.File.Text;
            Invoke(window, "ExecuteAction", "clearRecent");
            await Pump(window);
            var settings = Field<ReaderSettings>(window, "settings");
            check(settings.Recent.Count == 0 && settings.RecentPositions.Count == 0,
                "最近位置：清除最近文件同时清空位置映射");
            check(window.Tabs.Count == tabCountBefore &&
                window.Tabs.Contains(restored) &&
                restored.File.Text == textBefore,
                "最近位置：清除历史不关闭标签也不清除当前未保存正文");
            await window.PersistWorkspaceAsync();
            var draftFilesAfter = Directory.Exists(drafts)
                ? Directory.GetFiles(drafts, "*.json").Order().ToArray()
                : [];
            check(draftFilesBefore.SequenceEqual(draftFilesAfter),
                "最近位置：清除最近文件不会删除会话恢复草稿");

            window.CloseForShutdown();
            window = await OpenWindow(profile);
            await window.OpenPathsAsync([documentPath]);
            var cleanReopen = window.CurrentTab!;
            await Pump(window);
            check(cleanReopen.IsReading && cleanReopen.SourceAnchor == 0 &&
                cleanReopen.Editor.SelectionStart == 0,
                "最近位置：清除后重新打开 Markdown 使用 macOS 默认阅读态，但不恢复旧选区或旧锚点");

            string renameSource = Path.Combine(folder, "rename-source.txt");
            File.WriteAllText(renameSource, "重命名位置迁移\n第二行\n第三行\n");
            await window.OpenPathsAsync([renameSource]);
            var renameTab = window.CurrentTab!;
            renameTab.Editor.Select(8, 4);
            await window.PersistWorkspaceAsync();
            window.RenameTab(renameTab, "rename-target.txt");
            await window.PersistWorkspaceAsync();
            string renameTarget = Path.Combine(folder, "rename-target.txt");
            var renamedSettings = ReaderSettings.Load(profile);
            check(renamedSettings.RecentPositions.ContainsKey(renameTarget) &&
                !renamedSettings.RecentPositions.ContainsKey(renameSource),
                "最近位置：重命名文件后位置跟随新路径并从旧键移除");

            string capProfile = Path.Combine(folder, "cap-profile");
            var capSettings = ReaderSettings.Load(capProfile);
            for (int index = 1; index <= 100; index++)
            {
                string path = Path.Combine(folder, "cap", $"doc-{index:000}.txt");
                capSettings.Recent.Add(path);
                capSettings.RememberRecentPosition(path,
                    new RecentPosition(0, 0, index, index, 0, index, 0, false));
            }
            string currentPath = Path.Combine(folder, "cap", "current.txt");
            capSettings.Recent.Insert(0, currentPath);
            capSettings.RememberRecentPosition(currentPath,
                new RecentPosition(1, 2, 3, 4, 0, 5, 0, false));
            capSettings.RememberRecentPosition(currentPath.ToUpperInvariant(),
                new RecentPosition(2, 3, 4, 5, 0, 6, 0, false));
            capSettings.Save(capProfile);
            var capped = ReaderSettings.Load(capProfile);
            check(capped.RecentPositions.Count <= 100 &&
                capped.RecentPositions.Keys.Count(path =>
                    string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase)) == 1 &&
                capped.RecentPositions[currentPath].SelectionStart == 2,
                "最近位置：路径大小写去重，且超过 100 条时按最近文件保留集合");
        }
        finally
        {
            window?.CloseForShutdown();
        }
    }

    private static async Task<MainWindow> OpenWindow(string profile)
    {
        Directory.CreateDirectory(profile);
        var window = new MainWindow(profile, restoreSession: false, enableStartupUpdateCheck: false);
        window.Show();
        await window.InitializeAsync();
        await Pump(window);
        return window;
    }

    private static T Field<T>(object target, string name) =>
        (T)(target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target)
            ?? throw new MissingFieldException(target.GetType().Name, name));

    private static T Property<T>(object target, string name) =>
        (T)(target.GetType().GetProperty(name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(target)
            ?? throw new MissingMemberException(target.GetType().Name, name));

    private static object? Invoke(object target, string name, params object?[] arguments)
    {
        var method = target.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .FirstOrDefault(method =>
                method.Name == name && method.GetParameters().Length == arguments.Length);
        if (method is null)
            throw new MissingMethodException(
                target.GetType().FullName,
                $"{name}（可反射方法：{string.Join(", ", target.GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    .Where(candidate => candidate.Name == name)
                    .Select(candidate => candidate.ToString()))}）");
        return method.Invoke(target, arguments);
    }

    private static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception(name);
    }

    private static async Task ThrowsAsync<T>(Func<Task> action, string name) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception(name);
    }

    private static async Task Pump(Window window)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        if (!await WaitUntil(condition, TimeSpan.FromSeconds(6)))
            throw new TimeoutException("等待窗口状态超时。");
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > timeout) return false;
            await Task.Delay(25);
        }
        return true;
    }

    private static bool Nearly(double actual, double expected) => Math.Abs(actual - expected) <= 1;

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static IEnumerable<WorkspaceNode> Flatten(WorkspaceNode root)
    {
        yield return root;
        foreach (var child in root.Children)
        foreach (var nested in Flatten(child))
            yield return nested;
    }
}
