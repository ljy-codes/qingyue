using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Qingyue.Windows;

public partial class MainWindow
{
    private Task? recoveryPreparation;
    private readonly HashSet<Guid> openedRecoveryDocuments = [];

    /// <summary>
    /// Call at the start of InitializeAsync, before loading or opening any tabs,
    /// even when RestoreSession or RecoveryEnabled is false. Failure must abort
    /// initialization; persistence also refuses to proceed without preparation.
    /// </summary>
    public Task PrepareRecoveryAsync()
    {
        Dispatcher.VerifyAccess();
        return recoveryPreparation ??= PrepareRecoveryCoreAsync();
    }

    private async Task PrepareRecoveryCoreAsync()
    {
        await workspace.PrepareRecoveryAsync();
        foreach (var tab in tabs) openedRecoveryDocuments.Add(tab.File.Id);
        // Observe opens immediately: a restored tab can be explicitly discarded
        // before the first autosave. It must not remain protected as "unopened".
        tabs.CollectionChanged += (_, change) =>
        {
            if (change.NewItems is null) return;
            foreach (EditorTab tab in change.NewItems) openedRecoveryDocuments.Add(tab.File.Id);
        };
    }

    /// <summary>Call only after File.Rename succeeds, using its old and new paths.</summary>
    public void OnRecoveryPathRenamed(string? oldPath, string? newPath)
    {
        Dispatcher.VerifyAccess();
        if (oldPath is null || newPath is null
            || !settings.ExcludedDraftPaths.Contains(oldPath, StringComparer.OrdinalIgnoreCase)) return;
        settings.ExcludedDraftPaths.RemoveAll(path =>
            string.Equals(path, oldPath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, newPath, StringComparison.OrdinalIgnoreCase));
        settings.ExcludedDraftPaths.Add(newPath);
        SchedulePersist();
    }

    private Action FreezeRecoveryWorkspace()
    {
        var editors = tabs.Select(tab => (Editor: tab.Editor, WasReadOnly: tab.Editor.IsReadOnly)).ToArray();
        var surface = Content as UIElement;
        bool wasEnabled = surface?.IsEnabled ?? false;
        var focused = Keyboard.FocusedElement;
        foreach (var (editor, _) in editors) editor.SetCurrentValue(TextBox.IsReadOnlyProperty, true);
        // Freeze metadata, tab operations, context menus and drag/drop as well as
        // text input. Keep the owner Window enabled so close/save dialogs work.
        surface?.SetCurrentValue(UIElement.IsEnabledProperty, false);
        return () =>
        {
            foreach (var (editor, wasReadOnly) in editors) editor.SetCurrentValue(TextBox.IsReadOnlyProperty, wasReadOnly);
            surface?.SetCurrentValue(UIElement.IsEnabledProperty, wasEnabled);
            if (focused is UIElement { IsEnabled: true, IsVisible: true }) Keyboard.Focus(focused);
        };
    }
}
