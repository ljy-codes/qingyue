using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Qingyue.Core;

namespace Qingyue.Windows;

public partial class MainWindow
{
    private IInputElement? menuReturnFocus;

    private void MenuGroupOpened(object sender, RoutedEventArgs e)
    {
        var focused = Keyboard.FocusedElement;
        if (focused is not null && !IsWithinMainMenu(focused))
            menuReturnFocus = focused;
        RefreshMenuState();
    }

    private void MenuGroupClosed(object sender, RoutedEventArgs e)
    {
        // WPF leaves keyboard focus on the menu when a submenu is dismissed
        // without invoking a command. macOS returns focus to the prior target.
        // If a command already moved focus (editor, dialog, search, etc.),
        // preserve that intentional destination.
        var focused = Keyboard.FocusedElement;
        if (focused is not null && !IsWithinMainMenu(focused)) return;
        var target = menuReturnFocus;
        menuReturnFocus = null;
        if (target is UIElement element && element.IsVisible && element.IsEnabled)
            element.Focus();
        else if (target is ContentElement content && content.IsEnabled)
            Keyboard.Focus(content);
    }

    private bool IsWithinMainMenu(IInputElement focused) =>
        focused is DependencyObject node && MainMenu.IsAncestorOf(node);

    private void RefreshMenuState()
    {
        if (MainMenu is null) return;

        var tab = CurrentTab;
        bool hasTab = tab is not null;
        bool hasPath = tab?.File.Path is not null;
        bool modal = modalDepth > 0;

        // NSMenuItem validation applies to every command, including commands
        // whose UI-level preconditions are narrower. Most menu actions only
        // require an active document; their implementation validates the rest.
        foreach (var item in AllMenuItems())
            if (item.Tag is string action)
                item.IsEnabled = CanExecuteAction(action);

        SetStandardCommandState();
        UndoMenuItem.IsEnabled = CanExecuteAction("undo");
        RedoMenuItem.IsEnabled = CanExecuteAction("redo");
        SetEnabled("reload", hasPath && CanExecuteAction("reload"));
        foreach (var codec in Enum.GetValues<TextCodec>())
            SetEnabled("reopen:" + codec, hasPath && CanExecuteAction("reopen:" + codec));
        SetEnabled("copyPath", hasPath && CanExecuteAction("copyPath"));
        SetEnabled("reveal", hasPath && CanExecuteAction("reveal"));
        SetEnabled("formatJson", CanExecuteAction("formatJson"));
        for (int index = 1; index <= 9; index++)
            SetEnabled("tab:" + index, CanExecuteAction("tab:" + index));
        SetEnabled("moveLeft", CanExecuteAction("moveLeft"));
        SetEnabled("moveRight", CanExecuteAction("moveRight"));

        SetChecked("sidebar", settings.Sidebar && !focusMode);
        SetChecked("reading", tab?.IsReading == true);
        SetChecked("focus", focusMode);
        SetChecked("wrap", settings.Wrap);
        SetChecked("toggleCurrentDraft", tab?.RecoverDraft == true);
        SetUnchecked("lines");
        SetUnchecked("bookmark");
        SetUnchecked("markRead");

        foreach (var command in shortcutCommands)
            command.RaiseCanExecuteChanged();
        CommandManager.InvalidateRequerySuggested();
    }

    private void SetStandardCommandState()
    {
        bool enabled = !exitInProgress && modalDepth == 0 && !restoringWorkspace && CurrentTab is not null;
        foreach (var item in AllMenuItems())
            if (item.Command is not null)
                item.IsEnabled = enabled;
    }

    private void SetEnabled(string tag, bool value)
    {
        foreach (var item in FindMenuItems(tag))
            item.IsEnabled = value;
    }

    private void SetChecked(string tag, bool value)
    {
        foreach (var item in FindMenuItems(tag))
        {
            item.IsCheckable = true;
            item.IsChecked = value;
        }
    }

    private void SetUnchecked(string tag)
    {
        foreach (var item in FindMenuItems(tag))
        {
            item.IsCheckable = false;
            item.IsChecked = false;
        }
    }

    private IEnumerable<MenuItem> AllMenuItems()
    {
        foreach (var root in MainMenu.Items.OfType<MenuItem>())
        {
            yield return root;
            foreach (var item in AllMenuItems(root))
                yield return item;
        }
    }

    private static IEnumerable<MenuItem> AllMenuItems(ItemsControl parent)
    {
        foreach (var item in parent.Items.OfType<MenuItem>())
        {
            yield return item;
            foreach (var nested in AllMenuItems(item))
                yield return nested;
        }
    }

    private IEnumerable<MenuItem> FindMenuItems(string tag)
    {
        foreach (var root in MainMenu.Items.OfType<MenuItem>())
        {
            foreach (var item in FindMenuItems(root, tag))
                yield return item;
        }
    }

    private static IEnumerable<MenuItem> FindMenuItems(ItemsControl parent, string tag)
    {
        foreach (var item in parent.Items.OfType<MenuItem>())
        {
            if (Equals(item.Tag, tag)) yield return item;
            foreach (var nested in FindMenuItems(item, tag))
                yield return nested;
        }
    }
}
