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
    private readonly ReaderSettings settings;
    private readonly string dataDirectory;
    private readonly bool restoreSession;
    private readonly DispatcherTimer persistTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly Queue<Action> pendingOpen = new();
    private bool initialized, updatingControls, approvedShutdown, exitInProgress, focusMode;
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
        InitializeComponent();
        InitializeEditingInteractions();
        InitializeMarkdownPreview();
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
        persistTimer.Tick += async (_, _) =>
        {
            persistTimer.Stop();
            if (!initialized || exitInProgress) return;
            var result = await PersistWorkspaceAsync();
            if (result.Errors.Count > 0) StatusText.Text = "恢复草稿保存失败：" + result.Errors[0];
        };
        Closing += WindowClosing;
        SizeChanged += (_, _) => UpdateReadingWidths();
    }

    public async Task InitializeAsync()
    {
        if (initialized) return;
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
                    tab.SourceAnchor = Math.Clamp(restored.Entry.SourceAnchor, 0, tab.Editor.Text.Length);
                    tab.Editor.Select(Math.Clamp(restored.Entry.SelectionStart, 0, tab.Editor.Text.Length),
                        Math.Clamp(restored.Entry.SelectionLength, 0, tab.Editor.Text.Length - Math.Clamp(restored.Entry.SelectionStart, 0, tab.Editor.Text.Length)));
                    if (restored.Entry.IsReading) EnterReading(tab, restored.Entry.SourceAnchor);
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
        DocumentHost.Content = tab.IsReading && tab.Rendered is not null ? tab.Preview : tab.EditorSurface;
        ReadingButton.Content = tab.IsReading ? "返回编辑" : "阅读模式";
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
            (tab.File.Text.Length > 200_000 && tab.IsReading ? " · 大文档原文阅读" : "") +
            (tab.File.Path is null ? " · 未命名文档" : " · " + tab.File.Path);
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
        if (e.Source == TabStrip) RefreshCurrent();
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
        foreach (var path in paths.Take(200))
        {
            string fullPath;
            try { fullPath = Path.GetFullPath(path); }
            catch (Exception ex) { Notify("无法打开路径：" + ex.Message); continue; }
            var existing = tabs.FirstOrDefault(t => string.Equals(t.File.Path, fullPath, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) { SelectTab(existing); continue; }
            try
            {
                var file = await Task.Run(() => DocumentFile.Open(fullPath));
                void Add()
                {
                    if (approvedShutdown || exitInProgress) return;
                    var duplicate = tabs.FirstOrDefault(t => string.Equals(t.File.Path, fullPath, StringComparison.OrdinalIgnoreCase));
                    if (duplicate is not null) { SelectTab(duplicate); return; }
                    if (tabs.Count >= 200) { Notify("最多同时打开 200 个标签。"); return; }
                    var empty = tabs.Count == 1 && tabs[0].IsEmpty ? tabs[0] : null;
                    AddDocument(file);
                    if (empty is not null) tabs.Remove(empty);
                    AddRecent(fullPath);
                }
                if (modalDepth > 0) pendingOpen.Enqueue(Add); else Add();
            }
            catch (Exception ex) { Notify($"无法打开“{Path.GetFileName(fullPath)}”：\n{ex.Message}"); }
        }
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
        tab.File.Text = Normalize(tab.Editor.Text);
        tab.File.Save(fullPath, allowOverwrite);
        TrackSavedBaseline(tab);
        bool kindChanged = RefreshAutomaticKind(tab);
        tab.NotifyChanged();
        AddRecent(fullPath);
        if (kindChanged && CurrentTab == tab) RefreshCurrent(); else RefreshStatus();
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
        if (old is not null) settings.Recent.RemoveAll(p => string.Equals(p, old, StringComparison.OrdinalIgnoreCase));
        if (tab.File.Path is not null) AddRecent(tab.File.Path);
        bool kindChanged = RefreshAutomaticKind(tab);
        tab.NotifyChanged();
        if (kindChanged && CurrentTab == tab) RefreshCurrent(); else RefreshStatus();
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
