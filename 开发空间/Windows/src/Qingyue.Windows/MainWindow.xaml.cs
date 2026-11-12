using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Qingyue.Core;

namespace Qingyue.Windows;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<EditorTab> tabs = [];
    private readonly WorkspaceStore workspace;
    private readonly BookmarkStore bookmarks;
    private readonly ReaderSettings settings;
    private readonly string dataDirectory;
    private readonly bool restoreSession;
    private readonly DispatcherTimer persistTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly DispatcherTimer bookmarkSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly Queue<Action> pendingOpen = new();
    private bool initialized, updatingControls, approvedShutdown, exitInProgress, focusMode;
    private bool bookmarksDirty;
    private Task? bookmarkSaveTask;
    private int modalDepth;
    private long outlineGeneration;
    private readonly HashSet<Guid> discardedAtExit = [];
    public IReadOnlyList<EditorTab> Tabs => tabs;
    public EditorTab? CurrentTab => TabStrip.SelectedItem as EditorTab;

    public MainWindow(string dataDirectory, bool restoreSession = true)
    {
        this.dataDirectory = dataDirectory;
        this.restoreSession = restoreSession;
        settings = ReaderSettings.Load(dataDirectory);
        workspace = new WorkspaceStore(dataDirectory);
        bookmarks = new BookmarkStore(dataDirectory);
        InitializeComponent();
        InitializeEditingInteractions();
        InitializeMarkdownPreview();
        InitializeExternalChanges();
        TabStrip.ItemsSource = tabs;
        KindCombo.ItemsSource = Enum.GetValues<TextKind>();
        CodecCombo.ItemsSource = Enum.GetValues<TextCodec>();
        EndingCombo.ItemsSource = Enum.GetValues<LineEnding>();
        Width = Math.Min(settings.Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(settings.Height, SystemParameters.WorkArea.Height);
        ApplyTheme(settings.Dark);
        ApplySidebar();
        RefreshRecent();
        InstallShortcuts();
        bookmarkSaveTimer.Tick += async (_, _) =>
        {
            bookmarkSaveTimer.Stop();
            await SaveBookmarksAsync();
        };
        persistTimer.Tick += async (_, _) =>
        {
            persistTimer.Stop();
            if (!initialized || exitInProgress) return;
            var result = await PersistWorkspaceAsync();
            if (result.Errors.Count > 0) StatusText.Text = "恢复草稿保存失败：" + result.Errors[0];
        };
        Closing += WindowClosing;
        Closing += (_, _) => CancelLoading();
        SizeChanged += (_, _) => UpdateReadingWidths();
    }

    public async Task InitializeAsync()
    {
        if (initialized) return;
        await PrepareRecoveryAsync();
        await bookmarks.LoadAsync();
        if (restoreSession && settings.RestoreSession)
        {
            try
            {
                var result = await workspace.LoadAsync();
                foreach (var restored in result.Documents.Take(200))
                {
                    var tab = AddDocument(restored.File);
                    tab.RecoverDraft = restored.Entry.RecoverDraft;
                    tab.AutomaticKind = restored.Entry.AutomaticKind;
                    tab.Kind = tab.AutomaticKind ? TextServices.DetectKind(tab.File.Path ?? tab.File.SuggestedName, tab.File.Text) : restored.Entry.Kind;
                    tab.SourceAnchor = Math.Clamp(restored.Entry.SourceAnchor, 0, tab.File.Text.Length);
                    int selectionStart = EditorOffset(tab, restored.Entry.SelectionStart);
                    int selectionEnd = EditorOffset(tab, (int)Math.Min(tab.File.Text.Length,
                        (long)restored.Entry.SelectionStart + restored.Entry.SelectionLength));
                    tab.Editor.Select(selectionStart, Math.Max(0, selectionEnd - selectionStart));
                    if (restored.Entry.IsReading)
                    {
                        await EnterReadingAsync(tab, restored.Entry.SourceAnchor, focus: false);
                        RestoreReadingViewport(tab, restored.Entry);
                    }
                }
                var active = tabs.FirstOrDefault(t => t.File.Id == result.ActiveId);
                if (active is not null) SelectTab(active);
                if (result.Warnings.Count > 0)
                    StatusText.Text = "恢复提示：" + string.Join("；", result.Warnings.Take(3));
            }
            catch (Exception ex) { Notify("无法恢复上次会话，原有数据仍保留。\n" + ex.Message); }
        }
        if (tabs.Count == 0) NewDocument();
        initialized = true;
        RefreshCurrent();
        QueueExternalRefresh(CurrentTab);
    }

    private EditorTab AddDocument(DocumentFile file)
    {
        if (tabs.Count >= 200) throw new InvalidOperationException("最多同时打开 200 个标签，请先关闭部分文档。");
        var tab = new EditorTab(file);
        ConfigureEditor(tab);
        AttachSyntax(tab);
        tab.Editor.TextChanged += (_, _) =>
        {
            tab.File.Text = Normalize(tab.Editor.Text);
            tab.Reading = null;
            tab.Rendered = null;
            RefreshAutomaticKind(tab);
            tab.NotifyChanged();
            tab.UpdateLineNumbers();
            if (CurrentTab == tab)
            {
                OutlineList.ItemsSource = null;
                RefreshStatus(); RefreshLocation(); QueueSearch(); QueueOutline(tab);
            }
            SchedulePersist();
        };
        tab.Editor.SelectionChanged += (_, _) => { if (CurrentTab == tab) RefreshLocation(); };
        tab.Preview.SelectionChanged += (_, _) => { if (CurrentTab == tab) RefreshLocation(); };
        tab.Preview.Loaded += (_, _) => UpdateReadingWidth(tab);
        tab.Preview.SizeChanged += (_, _) => UpdateReadingWidth(tab);
        AttachReadingNavigation(tab);
        ReadingClipboard.Attach(tab.Preview, () => tab.Kind == TextKind.Markdown && tab.IsReading,
            reason => StatusText.Text = "复制已降级：" + reason);
        tabs.Add(tab);
        SelectTab(tab);
        return tab;
    }

    public EditorTab NewDocument(string text = "")
    {
        var file = new DocumentFile(text);
        file.Rename(NextDocumentName());
        return AddDocument(file);
    }

    public void SelectTab(EditorTab tab)
    {
        if (!tabs.Contains(tab)) return;
        TabStrip.SelectedItem = tab;
        RefreshCurrent();
        QueueExternalRefresh(tab);
    }

    private static string Normalize(string value) => value.Replace("\r\n", "\n").Replace('\r', '\n');

    private void ConfigureEditor(EditorTab tab)
    {
        tab.Editor.FontSize = settings.FontSize;
        // TextBox line-index APIs assume the font's natural line height.
        // A custom attached LineHeight makes their visual-row offsets drift.
        tab.Editor.TextWrapping = settings.Wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        tab.Editor.HorizontalScrollBarVisibility = settings.Wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        tab.LineNumbers.FontSize = settings.FontSize;
        ((Border)tab.EditorSurface.Children[0]).Visibility = settings.LineNumbers ? Visibility.Visible : Visibility.Collapsed;
        tab.Preview.FontSize = settings.FontSize + 1;
        RefreshSyntax(tab);
    }

    private void RefreshCurrent()
    {
        ReleaseClosedPresentations();
        DetachMarkdownEditor();
        if (CurrentTab is not { } tab) { DocumentHost.Content = null; return; }
        NavigationTabChanging(tab);
        DocumentHost.Content = tab.IsReading && tab.Rendered is not null ? tab.Preview : tab.EditorSurface;
        ReturnPositionButton.IsEnabled = tab.ReturnAnchor is not null;
        ReadingButton.Content = tab.Kind == TextKind.Markdown
            ? tab.IsReading ? "编辑原文" : "返回预览"
            : tab.IsReading ? "返回编辑" : "阅读模式";
        Title = tab.File.Title + " · 轻阅";
        if (tab.Reading is not null)
        {
            outlineGeneration++;
            OutlineList.ItemsSource = tab.Reading.Headings;
        }
        else
        {
            OutlineList.ItemsSource = null;
            QueueOutline(tab);
        }
        RefreshStatus();
        RefreshBookmarks();
        RefreshLocation();
        QueueSearch();
        SchedulePersist();
    }

    private async void QueueOutline(EditorTab tab)
    {
        long generation = ++outlineGeneration;
        string text = tab.File.Text;
        var kind = tab.Kind;
        try
        {
            await Task.Delay(180);
            if (generation != outlineGeneration || CurrentTab != tab) return;
            var model = await Task.Run(() => ReadingModel.Build(text, kind));
            if (generation != outlineGeneration || CurrentTab != tab || tab.File.Text != text || tab.Kind != kind || tab.IsReading) return;
            tab.Reading = model;
            OutlineList.ItemsSource = model.Headings;
        }
        catch (Exception ex)
        {
            if (generation == outlineGeneration) StatusText.Text = "目录生成失败：" + ex.Message;
        }
    }

    private void RefreshStatus()
    {
        if (CurrentTab is not { } tab) return;
        updatingControls = true;
        KindCombo.SelectedItem = tab.Kind;
        CodecCombo.SelectedItem = tab.File.Codec;
        EndingCombo.SelectedItem = tab.File.LineEnding;
        updatingControls = false;
        StatusText.Text = $"{(tab.IsReading ? "阅读" : "编辑")} · {tab.File.Text.Length:N0} 字符" +
            (tab.File.IsDirty ? " · 未保存" : " · 已保存") +
            (!PreviewLimits.CanRender(tab.File.Text, tab.Kind) && tab.IsReading ? " · 超过 50 万正文字符，原文阅读" : "") +
            (tab.File.Path is null ? " · 未命名文档" : " · " + tab.File.Path);
        ReadingTitle.Text = tab.File.Title + (tab.File.IsDirty ? "  •" : "");
        ReadingStatus.Text = ReadingProgressText(tab);
        BookmarkButton.Content = CurrentBookmarks(tab).Any() ? "书签已标记" : "添加书签";
        Title = tab.File.Title + (tab.File.IsDirty ? " *" : "") + " · 轻阅";
        RefreshPresentation(tab);
    }

    private void RefreshLocation()
    {
        if (CurrentTab is not { } tab) return;
        int caret = tab.Editor.CaretIndex;
        if (tab.IsReading && tab.Rendered is not null && tab.Reading is not null)
            caret = tab.Reading.DisplayToSource(tab.Rendered.OffsetAt(tab.Preview.Selection.Start));
        // WPF may insert CRLF; source offsets are always normalized to UTF-16 LF.
        if (tab.IsReading && tab.Rendered is not null) caret = EditorOffset(tab, caret);
        var location = tab.Positions.Location(caret);
        LocationText.Text = $"行 {location.Line:N0}，列 {location.Column:N0}" +
            (!tab.IsReading && tab.Editor.SelectionLength > 0 ? $" · 已选 {tab.Editor.SelectionLength:N0}" : "");
    }

    private void TabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == TabStrip)
        {
            RefreshCurrent();
            QueueExternalRefresh(CurrentTab);
        }
    }

    private async void WindowDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            e.Handled = true;
            await OpenPathsAsync(paths);
        }
    }

    private void WindowDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    public async Task OpenPathsAsync(IEnumerable<string> paths)
    {
        string[] requested = paths.Take(200).ToArray();
        await openGate.WaitAsync();
        try
        {
            using var operation = BeginLoading("正在读取文件");
            for (int index = 0; index < requested.Length; index++)
            {
                operation.Token.ThrowIfCancellationRequested();
                string fullPath;
                try { fullPath = Path.GetFullPath(requested[index]); }
                catch (Exception ex) { Notify("无法打开路径：" + ex.Message); continue; }
                var existing = tabs.FirstOrDefault(t => string.Equals(t.File.Path, fullPath, StringComparison.OrdinalIgnoreCase));
                if (existing is not null) { operation.Tab = null; SelectTab(existing); continue; }
                try
                {
                    operation.Tab = null;
                    operation.Stage($"正在读取文件（{index + 1}/{requested.Length}）");
                    var file = await Task.Run(() => DocumentFile.Open(fullPath), operation.Token).WaitAsync(operation.Token);
                    operation.Token.ThrowIfCancellationRequested();
                    // An OS file-open request may arrive while a modal dialog is active.
                    // Complete the publication only when the dialog has returned.
                    if (modalDepth > 0)
                    {
                        pendingOpen.Enqueue(() => _ = OpenPathsAsync([fullPath]));
                        continue;
                    }
                    if (approvedShutdown || exitInProgress) return;
                    var duplicate = tabs.FirstOrDefault(t => string.Equals(t.File.Path, fullPath, StringComparison.OrdinalIgnoreCase));
                    if (duplicate is not null) { SelectTab(duplicate); continue; }
                    if (tabs.Count >= 200) { Notify("最多同时打开 200 个标签。"); continue; }
                    var empty = tabs.Count == 1 && tabs[0].IsEmpty ? tabs[0] : null;
                    var opened = AddDocument(file);
                    if (empty is not null) tabs.Remove(empty);
                    AddRecent(fullPath);
                    if (opened.Kind == TextKind.Markdown)
                    {
                        await EnterReadingAsync(opened, 0, existingOperation: operation);
                        if (CurrentTab == opened) RefreshCurrent();
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Notify($"无法打开“{Path.GetFileName(fullPath)}”：\n{ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { StatusText.Text = "已取消打开，已加载的正文与编辑仍保留。"; }
        finally { openGate.Release(); }
    }

    private T Modal<T>(Func<T> action)
    {
        modalDepth++;
        try { return action(); }
        finally
        {
            modalDepth--;
            if (modalDepth == 0 && pendingOpen.Count > 0)
                Dispatcher.BeginInvoke(() => { while (modalDepth == 0 && pendingOpen.TryDequeue(out var next)) next(); });
        }
    }

    private void Notify(string text, string title = "轻阅") =>
        Modal(() => Dialogs.Message(this, text, title));

    private async Task ChooseOpenAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "打开本机文本文件", Multiselect = true,
            Filter = "文本与配置|*.txt;*.md;*.markdown;*.json;*.jsonc;*.yaml;*.yml;*.ini;*.xml;*.log;*.csv;*.conf|所有文件|*.*"
        };
        if (Modal(() => dialog.ShowDialog(this)) == true) await OpenPathsAsync(dialog.FileNames);
    }

    public bool SaveTabToPath(EditorTab tab, string path, bool allowOverwrite = false)
    {
        string fullPath = Path.GetFullPath(path);
        if (tabs.Any(t => t != tab && string.Equals(t.File.Path, fullPath, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("该目标已在另一标签中打开，请先关闭目标标签。");
        string? oldPath = tab.File.Path;
        tab.File.Text = Normalize(tab.Editor.Text);
        tab.File.Save(fullPath, allowOverwrite);
        bookmarks.MigratePath(tab.File.Id, oldPath, fullPath);
        bookmarksDirty = true;
        TrackSavedBaseline(tab);
        bool kindChanged = RefreshAutomaticKind(tab);
        tab.NotifyChanged();
        AddRecent(fullPath);
        if (kindChanged && CurrentTab == tab) RefreshCurrent(); else RefreshStatus();
        RefreshBookmarks();
        SchedulePersist();
        return true;
    }

    private bool SaveDocument(EditorTab tab, bool saveAs = false)
    {
        string? target = tab.File.Path;
        bool overwrite = false;
        if (saveAs || target is null)
        {
            var dialog = new SaveFileDialog
            {
                Title = "保存文档", FileName = tab.File.Title,
                DefaultExt = LanguageCatalog.DefaultExtension(tab.Kind),
                Filter = "文本文件|*.txt|Markdown|*.md|JSON|*.json|所有文件|*.*", OverwritePrompt = true
            };
            if (target is not null) dialog.InitialDirectory = Path.GetDirectoryName(target);
            if (Modal(() => dialog.ShowDialog(this)) != true) return false;
            target = dialog.FileName;
            // The native dialog has explicitly confirmed an existing Save As target.
            overwrite = File.Exists(target) && !string.Equals(target, tab.File.Path, StringComparison.OrdinalIgnoreCase);
        }
        try { return SaveTabToPath(tab, target, overwrite); }
        catch (MixedLineEndingsException)
        {
            var answer = Modal(() => MessageBox.Show(this,
                "文件包含混合换行。选择“是”统一为 LF，“否”统一为 CRLF；取消则保持文档。",
                "选择换行格式", MessageBoxButton.YesNoCancel, MessageBoxImage.Question));
            if (answer == MessageBoxResult.Cancel) return false;
            tab.File.LineEnding = answer == MessageBoxResult.Yes ? LineEnding.Lf : LineEnding.CrLf;
            return SaveDocument(tab, saveAs);
        }
        catch (FileConflictException ex)
        {
            var answer = Modal(() => MessageBox.Show(this, ex.Message +
                "\n\n“是”：明确覆盖磁盘内容。\n“否”：另存为。\n“取消”：保留当前编辑。",
                "磁盘内容发生变化", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning));
            if (answer == MessageBoxResult.No) return SaveDocument(tab, true);
            if (answer == MessageBoxResult.Yes)
            {
                try { return SaveTabToPath(tab, target, true); }
                catch (Exception failure) { Notify("保存失败，编辑仍保留：\n" + failure.Message); }
            }
            return false;
        }
        catch (Exception ex) { Notify("保存失败，编辑仍保留，可尝试另存为：\n" + ex.Message); return false; }
    }

    public void RenameTab(EditorTab tab, string name)
    {
        var old = tab.File.Path;
        tab.File.Rename(name);
        if (tab.File.Path is { } newPath)
        {
            bookmarks.MigratePath(tab.File.Id, old, newPath);
            bookmarksDirty = true;
        }
        OnRecoveryPathRenamed(old, tab.File.Path);
        if (old is not null) settings.Recent.RemoveAll(p => string.Equals(p, old, StringComparison.OrdinalIgnoreCase));
        if (tab.File.Path is not null) AddRecent(tab.File.Path);
        bool kindChanged = RefreshAutomaticKind(tab);
        tab.NotifyChanged();
        if (kindChanged && CurrentTab == tab) RefreshCurrent(); else RefreshStatus();
        RefreshBookmarks();
        SchedulePersist();
    }

    private bool RefreshAutomaticKind(EditorTab tab)
    {
        if (!tab.AutomaticKind) return false;
        var kind = TextServices.DetectKind(tab.File.Path ?? tab.File.SuggestedName, tab.File.Text);
        if (kind == tab.Kind) return false;
        int anchor = CurrentSourceAnchor(tab);
        tab.Kind = kind;
        tab.Reading = null;
        tab.Rendered = null;
        if (tab.IsReading) EnterReading(tab, anchor);
        return true;
    }

    private bool CloseTab(EditorTab tab, bool ensureDocument = true)
    {
        if (!tab.IsEmpty && tab.File.IsDirty)
        {
            var choice = Modal(() => Dialogs.CloseChoice(this, tab.File.Title, false));
            if (choice == "cancel" || choice == "save" && !SaveDocument(tab)) return false;
        }
        tabs.Remove(tab);
        if (tabs.Count == 0 && ensureDocument) NewDocument();
        RefreshCurrent();
        SchedulePersist();
        return true;
    }

    private void CloseTabClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { Tag: EditorTab tab }) CloseTab(tab);
    }

    private void TabDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not FrameworkElement { DataContext: EditorTab tab }) return;
        RenamePrompt(tab);
        e.Handled = true;
    }

    private void RenamePrompt(EditorTab tab)
    {
        var name = Modal(() => Dialogs.Prompt(this, "重命名", "仅更改文件名，未保存的编辑将继续保留。", tab.File.Title));
        if (string.IsNullOrWhiteSpace(name)) return;
        try { RenameTab(tab, name); } catch (Exception ex) { Notify("重命名失败：\n" + ex.Message); }
    }
}
