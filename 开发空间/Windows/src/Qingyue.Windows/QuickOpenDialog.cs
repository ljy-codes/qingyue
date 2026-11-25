using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Qingyue.Core;

namespace Qingyue.Windows;

public static class QuickOpenDialog
{
    public static QuickOpenItem? Show(Window owner, IReadOnlyList<QuickOpenItem> items)
    {
        var session = CreateSession(owner, items);
        session.Dialog.ShowDialog();
        return session.Result;
    }

    public static Task<QuickOpenItem?> ShowAsync(Window owner, IReadOnlyList<QuickOpenItem> items)
    {
        var session = CreateSession(owner, items);
        session.Dialog.Show();
        return session.Completion.Task;
    }

    private static Session CreateSession(Window owner, IReadOnlyList<QuickOpenItem> items)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(items);
        foreach (var open in Application.Current.Windows.OfType<Window>()
                     .Where(window => window.IsVisible && window.Title == "快速打开" &&
                         ReferenceEquals(window.Owner, owner)).ToArray())
            open.Close();

        var dialog = Dialogs.Shell(owner, "快速打开", 640);
        var session = new Session(dialog);
        dialog.Width = 640;
        dialog.Height = 450;
        dialog.MinWidth = 500;
        dialog.MinHeight = 300;
        dialog.SizeToContent = SizeToContent.Manual;
        dialog.ResizeMode = ResizeMode.CanResize;
        dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        dialog.MaxHeight = double.PositiveInfinity;

        var root = new Grid { Margin = new Thickness(20, 16, 20, 12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new TextBlock
        {
            Text = "搜索已打开和最近使用的文件",
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 9)
        };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        var search = new TextBox
        {
            Height = 34,
            FontSize = 15,
            Name = "QuickOpenSearch",
            ToolTip = "输入文件名或路径"
        };
        AutomationProperties.SetName(search, "快速打开搜索");
        Grid.SetRow(search, 1);
        root.Children.Add(search);

        var resultHost = new Grid { Margin = new Thickness(0, 12, 0, 10) };
        Grid.SetRow(resultHost, 2);
        var list = new ListBox
        {
            Name = "QuickOpenResults",
            DisplayMemberPath = null,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(3)
        };
        list.SetResourceReference(Control.BackgroundProperty, "EditorBrush");
        list.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");
        AutomationProperties.SetName(list, "快速打开文件结果");
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        resultHost.Children.Add(list);

        var empty = new TextBlock
        {
            Text = items.Count == 0 ? "尚无已打开或最近使用的文件" : "没有匹配的文件",
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        empty.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        resultHost.Children.Add(empty);
        root.Children.Add(resultHost);

        var summary = new TextBlock
        {
            FontSize = 11,
            Name = "QuickOpenSummary"
        };
        summary.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        AutomationProperties.SetName(summary, "快速打开状态");
        Grid.SetRow(summary, 3);
        root.Children.Add(summary);

        bool completed = false;
        bool composing = false;

        void Refresh()
        {
            string? previousId = (list.SelectedItem as ListBoxItem)?.Tag is QuickOpenItem previous
                ? previous.Id
                : null;
            var filtered = QuickOpenRanker.Filter(items, search.Text);
            list.ItemsSource = null;
            list.Items.Clear();
            foreach (var item in filtered)
            {
                var row = new ListBoxItem
                {
                    Tag = item,
                    Content = CreateRow(item),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(9, 7, 9, 7),
                    Margin = new Thickness(0, 1, 0, 1)
                };
                AutomationProperties.SetName(row, $"{item.Title}，{(item.IsOpen ? "已打开" : "最近")}，{item.Subtitle}");
                row.ToolTip = item.Subtitle;
                row.MouseDoubleClick += (_, _) =>
                {
                    list.SelectedItem = row;
                    Choose();
                };
                list.Items.Add(row);
            }
            if (list.Items.Count > 0)
            {
                int index = -1;
                if (previousId is not null)
                {
                    for (int row = 0; row < list.Items.Count; row++)
                    {
                        if (list.Items[row] is ListBoxItem { Tag: QuickOpenItem item } &&
                            item.Id == previousId)
                        {
                            index = row;
                            break;
                        }
                    }
                }
                list.SelectedIndex = index >= 0 ? index : 0;
                list.ScrollIntoView(list.SelectedItem);
            }
            empty.Visibility = list.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            empty.Text = items.Count == 0 ? "尚无已打开或最近使用的文件" : "没有匹配的文件";
            summary.Text = $"{list.Items.Count} 个结果 · ↑↓ 选择 · Return 打开 · Esc 取消";
        }

        void MoveSelection(int delta)
        {
            if (list.Items.Count == 0) return;
            int current = Math.Max(0, list.SelectedIndex);
            list.SelectedIndex = (current + delta + list.Items.Count) % list.Items.Count;
            list.ScrollIntoView(list.SelectedItem);
        }

        void Choose()
        {
            if (completed || list.SelectedItem is not ListBoxItem { Tag: QuickOpenItem item }) return;
            completed = true;
            session.Result = item;
            dialog.Close();
        }

        void Cancel()
        {
            if (completed) return;
            completed = true;
            dialog.Close();
        }

        bool HandleKey(Key key)
        {
            if (composing || key == Key.ImeProcessed) return false;
            switch (key)
            {
                case Key.Down:
                    MoveSelection(1);
                    return true;
                case Key.Up:
                    MoveSelection(-1);
                    return true;
                case Key.Enter:
                    Choose();
                    return true;
                case Key.Escape:
                    Cancel();
                    return true;
                default:
                    return false;
            }
        }

        search.TextChanged += (_, _) => Refresh();
        TextCompositionManager.AddPreviewTextInputStartHandler(search, (_, _) => composing = true);
        TextCompositionManager.AddPreviewTextInputHandler(search, (_, _) => composing = false);
        search.LostKeyboardFocus += (_, _) => composing = false;
        search.PreviewKeyDown += (_, e) =>
        {
            if (HandleKey(e.Key)) e.Handled = true;
        };
        list.PreviewKeyDown += (_, e) =>
        {
            if (HandleKey(e.Key)) e.Handled = true;
        };
        list.MouseDoubleClick += (_, _) => Choose();
        dialog.Closed += (_, _) =>
        {
            completed = true;
            session.Completion.TrySetResult(session.Result);
        };

        dialog.Content = root;
        dialog.Loaded += (_, _) =>
        {
            search.Focus();
            search.SelectAll();
        };
        Refresh();
        return session;
    }

    private sealed class Session
    {
        public Session(Window dialog) => Dialog = dialog;

        public Window Dialog { get; }
        public QuickOpenItem? Result { get; set; }
        public TaskCompletionSource<QuickOpenItem?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static FrameworkElement CreateRow(QuickOpenItem item)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = item.Title,
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetRow(title, 0);
        Grid.SetColumn(title, 0);
        grid.Children.Add(title);

        var source = new TextBlock
        {
            Text = item.IsOpen ? "已打开" : "最近",
            FontSize = 10,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        source.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        Grid.SetRow(source, 0);
        Grid.SetColumn(source, 1);
        grid.Children.Add(source);

        var path = new TextBlock
        {
            Text = item.Subtitle,
            FontSize = 11,
            Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        path.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        Grid.SetRow(path, 1);
        Grid.SetColumn(path, 0);
        Grid.SetColumnSpan(path, 2);
        grid.Children.Add(path);
        return grid;
    }
}
