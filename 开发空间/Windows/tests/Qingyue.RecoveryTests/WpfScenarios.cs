using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Qingyue.Core;
using Qingyue.Windows;

internal static partial class Program
{
    private static async Task RunWpfScenarios()
    {
        await Scenario("restore_false_empty_autosave_preserves_old_draft", root => StartupPreservation(root, true));
        await Scenario("restore_false_recovery_disabled_preserves_old_draft", root => StartupPreservation(root, false));
        await Scenario("capture_keeps_normalized_selection_offsets", async root =>
        {
            string profile = Path.Combine(root, "profile");
            var window = await OpenWindow(profile, false);
            try
            {
                var tab = window.NewDocument("alpha\nbeta\ngamma");
                tab.Editor.Text = "alpha\r\nbeta\r\ngamma";
                tab.Editor.Select(7, 4);
                Check(tab.Editor.SelectedText == "beta" && tab.File.Text == "alpha\nbeta\ngamma", "CRLF selection fixture is invalid");
                var result = await window.PersistWorkspaceAsync();
                Check(result.Errors.Count == 0, string.Join("; ", result.Errors));
                var restored = (await new WorkspaceStore(profile).LoadAsync()).Documents.Single(d => d.File.Id == tab.File.Id);
                Check(restored.Entry.SelectionStart == 6 && restored.Entry.SelectionLength == 4,
                    "capture changed persisted selection from normalized source offsets to raw WPF offsets");
            }
            finally { window.CloseForShutdown(); }
        });
        await Scenario("restored_tab_closed_before_first_autosave_is_not_resurrected", async root =>
        {
            var file = new DocumentFile("RESTORED_THEN_DISCARDED");
            string profile = Path.Combine(root, "profile");
            await Save(new WorkspaceStore(profile), Session(file), file.CaptureSnapshot());
            var window = await OpenWindow(profile, true);
            try
            {
                Check(window.Tabs.Any(t => t.File.Id == file.Id), "fixture did not restore tab");
                using var dialogs = new DialogResponses(_ => "放弃");
                Invoke(window, "CloseTab", window.Tabs.Single(t => t.File.Id == file.Id), true);
                await window.PersistWorkspaceAsync();
                Check(!File.Exists(Draft(profile, file.Id)), "closed restored tab was retained as unopened");
            }
            finally { window.CloseForShutdown(); }
        });
        await Scenario("shutdown_freezes_body_and_metadata", async root => await ClosingFreeze(root, false));
        await Scenario("failed_persistence_restores_body_metadata_and_readonly", async root => await ClosingFreeze(root, true));
        await Scenario("cancel_exit_restores_original_state", async root =>
        {
            var window = await OpenWindow(Path.Combine(root, "profile"), false);
            try
            {
                var first = window.NewDocument("FIRST");
                var second = window.NewDocument("SECOND");
                first.Editor.IsReadOnly = true;
                ((FrameworkElement)window.Content).IsEnabled = true;
                using var dialogs = new DialogResponses(index => index == 0 ? "放弃" : "取消");
                window.Close();
                Check(window.IsVisible && !Get<bool>(window, "exitInProgress"), "cancel did not retain window");
                Check(first.Editor.IsReadOnly && !second.Editor.IsReadOnly, "cancel changed original read-only flags");
                Check(((UIElement)window.Content).IsEnabled && Get<HashSet<Guid>>(window, "discardedAtExit").Count == 0,
                    "cancel left metadata disabled or discard decisions behind");
                var result = await window.PersistWorkspaceAsync();
                Check(result.Errors.Count == 0, string.Join("; ", result.Errors));
                Check((await new WorkspaceStore(Path.Combine(root, "profile")).LoadAsync()).Documents.Count == 2,
                    "cancel still discarded a document");
            }
            finally { window.CloseForShutdown(); }
        });
        foreach (bool excludePath in new[] { false, true })
            await Scenario(excludePath ? "path_exclusion_survives_failed_cleanup" : "global_recovery_off_survives_failed_cleanup", async root =>
            {
                string profile = Path.Combine(root, "profile"), path = Path.Combine(root, "document.txt");
                File.WriteAllText(path, "DISK_BASELINE");
                var window = await OpenWindow(profile, false);
                try
                {
                    await window.OpenPathsAsync([path]);
                    var tab = window.CurrentTab!;
                    tab.Editor.Text = "PREVIOUS_DIRTY_DRAFT";
                    Check((await window.PersistWorkspaceAsync()).Errors.Count == 0, "could not seed dirty draft");
                    var settings = Get<ReaderSettings>(window, "settings");
                    if (excludePath) settings.ExcludedDraftPaths.Add(path);
                    else settings.RecoveryEnabled = false;
                    using var locked = new FileStream(Draft(profile, tab.File.Id), FileMode.Open, FileAccess.Read, FileShare.Read);
                    var result = await window.PersistWorkspaceAsync();
                    Check(result.Errors.Count > 0 && File.Exists(Draft(profile, tab.File.Id)), "fixture did not fail draft cleanup");
                    var load = await new WorkspaceStore(profile).LoadAsync();
                    var restored = load.Documents.Single(d => d.File.Id == tab.File.Id);
                    Check(!restored.Entry.RecoverDraft && restored.File.Text == "DISK_BASELINE",
                        "session allowed excluded content to resurrect after failed cleanup");
                }
                finally { window.CloseForShutdown(); }
            });
        await Scenario("final_close_cancel_restores_editability", async root =>
        {
            var window = await OpenWindow(Path.Combine(root, "profile"), false);
            var editable = window.NewDocument("KEEP_WINDOW");
            var readOnly = window.NewDocument("READ_ONLY");
            readOnly.Editor.IsReadOnly = true;
            CancelEventHandler veto = (_, e) => { if (Get<bool>(window, "approvedShutdown")) e.Cancel = true; };
            window.Closing += veto;
            try
            {
                using var dialogs = new DialogResponses(_ => "保留草稿");
                window.Close();
                await Until(() => !Get<bool>(window, "exitInProgress") || Get<bool>(window, "approvedShutdown"), "final close decision");
                Check(window.IsVisible && !Get<bool>(window, "approvedShutdown") && !Get<bool>(window, "exitInProgress"),
                    "cancelled final close left approved/in-progress state set");
                Check(!editable.Editor.IsReadOnly && readOnly.Editor.IsReadOnly && ((UIElement)window.Content).IsEnabled,
                    "cancelled final close did not restore original state");
            }
            finally { window.Closing -= veto; window.CloseForShutdown(); }
        });
        await Scenario("rename_migrates_exclusion_only_after_success", async root =>
        {
            string profile = Path.Combine(root, "profile"), path = Path.Combine(root, "old.txt");
            File.WriteAllText(path, "DISK");
            var window = await OpenWindow(profile, false);
            try
            {
                await window.OpenPathsAsync([path]);
                var tab = window.CurrentTab!;
                var settings = Get<ReaderSettings>(window, "settings");
                settings.ExcludedDraftPaths.Add(path.ToUpperInvariant());
                settings.ExcludedDraftPaths.Add(Path.Combine(root, "unrelated.txt"));
                window.RenameTab(tab, "renamed.txt");
                string renamed = tab.File.Path!;
                // Main controller integration is exercised here; no manual callback hides a missing hook.
                Check(!settings.ExcludedDraftPaths.Contains(path, StringComparer.OrdinalIgnoreCase)
                    && settings.ExcludedDraftPaths.Contains(renamed, StringComparer.OrdinalIgnoreCase),
                    "successful rename did not move exclusion to new path");
                tab.RecoverDraft = true;
                tab.Editor.Text = "EXCLUDED_EDIT";
                var result = await window.PersistWorkspaceAsync();
                Check(result.Errors.Count == 0 && !File.Exists(Draft(profile, tab.File.Id)), "renamed/reopened exclusion wrote a draft");
                Check(ReaderSettings.Load(profile).ExcludedDraftPaths.Contains(renamed, StringComparer.OrdinalIgnoreCase),
                    "exclusion migration was not persisted");
                File.WriteAllText(Path.Combine(root, "exists.txt"), "DO_NOT_OVERWRITE");
                string[] before = settings.ExcludedDraftPaths.ToArray();
                bool failedRename = false;
                try { window.RenameTab(tab, "exists.txt"); } catch (IOException) { failedRename = true; }
                Check(failedRename && before.SequenceEqual(settings.ExcludedDraftPaths), "failed rename migrated exclusion");
                var callback = typeof(MainWindow).GetMethod("OnRecoveryPathRenamed");
                Check(callback is not null, "rename callback API is missing");
                callback!.Invoke(window, [renamed, renamed.ToUpperInvariant()]);
                Check(settings.ExcludedDraftPaths.Count(p => string.Equals(p, renamed, StringComparison.OrdinalIgnoreCase)) == 1,
                    "case-only rename duplicated exclusion");
                callback.Invoke(window, [null, renamed]);
                callback.Invoke(window, [renamed, null]);
                Check(settings.ExcludedDraftPaths.Contains(renamed, StringComparer.OrdinalIgnoreCase), "null callback removed exclusion");
                Check(File.ReadAllText(renamed) == "DISK"
                    && File.ReadAllText(Path.Combine(root, "exists.txt")) == "DO_NOT_OVERWRITE", "rename wrote unsaved text to disk");
            }
            finally { window.CloseForShutdown(); }
        });
    }

    private static async Task StartupPreservation(string root, bool recoveryEnabled)
    {
        var file = new DocumentFile("OLD_UNOPENED_DRAFT");
        string profile = Path.Combine(root, "profile");
        await Save(new WorkspaceStore(profile), Session(file), file.CaptureSnapshot());
        new ReaderSettings { RestoreSession = false, RecoveryEnabled = recoveryEnabled }.Save(profile);
        var window = await OpenWindow(profile, true);
        try
        {
            Check(window.Tabs.All(t => t.File.Id != file.Id), "restore=false unexpectedly opened old document");
            var result = await window.PersistWorkspaceAsync();
            Check(result.Errors.Count == 0 && File.Exists(Draft(profile, file.Id)), "empty startup autosave deleted unopened content");
            var fresh = window.NewDocument("NEW_SESSION_DRAFT");
            result = await window.PersistWorkspaceAsync();
            Check(result.Errors.Count == 0 && File.Exists(Draft(profile, file.Id)), "new session replaced unopened content");
            Check(typeof(MainWindow).GetMethod("PrepareRecoveryAsync") is not null, "preparation API is missing");
            await Prepare(window); // Idempotent after opening new tabs.
            await window.PersistWorkspaceAsync();
            Check((await new WorkspaceStore(profile).LoadAsync()).Documents.Any(d => d.File.Id == file.Id
                && d.File.Text == "OLD_UNOPENED_DRAFT"), "retained content no longer recoverable");
            Check(fresh.File.Text == "NEW_SESSION_DRAFT", "preservation changed live current tab");
        }
        finally { window.CloseForShutdown(); }
    }

    private static async Task ClosingFreeze(string root, bool failSettings)
    {
        string profile = Path.Combine(root, "profile");
        var window = await OpenWindow(profile, false);
        var editable = window.NewDocument("BODY_BEFORE_CLOSE");
        var originalReadOnly = window.NewDocument("ORIGINALLY_READONLY");
        originalReadOnly.Editor.IsReadOnly = true;
        window.SelectTab(editable);
        var store = Get<WorkspaceStore>(window, "workspace");
        var gate = Get<SemaphoreSlim>(store, "_gate");
        Get<ReaderSettings>(window, "settings").Save(profile);
        FileStream? settingsLock = failSettings
            ? new FileStream(Path.Combine(profile, "Settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read) : null;
        using var dialogs = new DialogResponses(_ => "保留草稿");
        bool frozen = false, bodyRejected = false;
        await gate.WaitAsync();
        try
        {
            window.Close();
            Check(Get<bool>(window, "exitInProgress") && window.IsVisible, "close did not await serialized persistence");
            frozen = editable.Editor.IsReadOnly && originalReadOnly.Editor.IsReadOnly
                && !((UIElement)window.Content).IsEnabled
                && !((ComboBox)window.FindName("CodecCombo")).IsEnabled
                && !((ComboBox)window.FindName("EndingCombo")).IsEnabled
                && !((TabControl)window.FindName("TabStrip")).IsEnabled;
            // Exercise the WPF TextBox handler, rather than assigning Text (which is a programmatic bypass).
            editable.Editor.CaretIndex = editable.Editor.Text.Length;
            editable.Editor.RaiseEvent(new TextCompositionEventArgs(InputManager.Current.PrimaryKeyboardDevice,
                new TextComposition(InputManager.Current, editable.Editor, "_LATE_INPUT"))
            { RoutedEvent = TextCompositionManager.TextInputEvent });
            bodyRejected = editable.Editor.Text == "BODY_BEFORE_CLOSE";
            window.Close(); // A second close cannot start a second persistence sequence.
        }
        finally { gate.Release(); }
        try
        {
            await Until(() => !window.IsVisible || !Get<bool>(window, "exitInProgress"), "shutdown completion");
            Check(frozen, "awaited shutdown left editor or metadata UI mutable");
            Check(bodyRejected, "TextInput changed body after shutdown snapshot");
            if (failSettings)
            {
                Check(window.IsVisible && !Get<bool>(window, "approvedShutdown"), "settings failure closed window");
                Check(!editable.Editor.IsReadOnly && originalReadOnly.Editor.IsReadOnly
                    && ((UIElement)window.Content).IsEnabled, "failure did not restore original edit/metadata states");
                Check(Get<DispatcherTimer>(window, "persistTimer").IsEnabled, "failure did not restart autosave");
                editable.Editor.Text += "_RETRY";
                settingsLock?.Dispose(); settingsLock = null;
                var retry = await window.PersistWorkspaceAsync();
                Check(retry.Errors.Count == 0, "retry after settings failure did not save");
            }
            else
            {
                Check(!window.IsVisible, "successful shutdown did not close window");
                var load = await new WorkspaceStore(profile).LoadAsync();
                Check(load.Documents.Single(d => d.File.Id == editable.File.Id).File.Text == "BODY_BEFORE_CLOSE",
                    "shutdown persisted a stale/different body");
            }
        }
        finally { settingsLock?.Dispose(); window.CloseForShutdown(); }
    }

    private static async Task<MainWindow> OpenWindow(string profile, bool restoreSession)
    {
        var window = new MainWindow(profile, restoreSession)
        { ShowActivated = false, ShowInTaskbar = false, Left = -16000, Top = -16000 };
        window.Show();
        // Only the real initializer may call PrepareRecoveryAsync; do not mask missing integration.
        await window.InitializeAsync();
        return window;
    }

    private static T Get<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static object? Invoke(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    private static async Task Until(Func<bool> predicate, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!predicate() && DateTime.UtcNow < deadline) await Task.Delay(15);
        Check(predicate(), "timeout: " + message);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private sealed class DialogResponses : IDisposable
    {
        private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(15) };
        private readonly Func<int, string> response;
        private int choices;

        public DialogResponses(Func<int, string> response)
        {
            this.response = response;
            timer.Tick += Respond;
            timer.Start();
        }

        private void Respond(object? sender, EventArgs e)
        {
            foreach (var dialog in Application.Current.Windows.Cast<Window>().Where(w => w.IsVisible && w is not MainWindow).ToArray())
            {
                var buttons = Descendants(dialog).OfType<Button>().ToArray();
                string label = dialog.Title == "处理未保存的修改" ? response(choices++) : "知道了";
                var button = buttons.FirstOrDefault(b => Equals(b.Content, label) && b.IsEnabled);
                button?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                yield return child;
                foreach (var nested in Descendants(child)) yield return nested;
            }
        }

        public void Dispose() { timer.Stop(); timer.Tick -= Respond; }
    }
}
