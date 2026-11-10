using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Qingyue.Windows;

/// <summary>A local viewer of an already decoded bitmap. Never resolves image destinations.</summary>
internal sealed class ReaderImageWindow : Window
{
    private readonly BitmapSource bitmap;
    private readonly Image image;
    private readonly ScrollViewer viewport;
    private readonly TextBlock status = new() { Margin = new Thickness(12, 6, 12, 6) };
    private bool fit = true;
    private double scale = 1;

    internal ReaderImageWindow(BitmapSource bitmap, string caption, Window? owner = null)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        this.bitmap = bitmap;
        ReaderWindowTheme.Apply(this, owner);
        Title = string.IsNullOrWhiteSpace(caption) ? "图片" : caption;
        Width = 1000;
        Height = 720;
        MinWidth = 380;
        MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "EditorBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        image = new Image { Source = bitmap, Stretch = Stretch.Fill, Margin = new Thickness(12) };
        AutomationProperties.SetName(image, Title);
        viewport = new ScrollViewer
        {
            Content = image, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
        AddButton(toolbar, "缩小", () => Zoom(1 / 1.25));
        AddButton(toolbar, "放大", () => Zoom(1.25));
        AddButton(toolbar, "适应窗口", () => { fit = true; Fit(); });
        AddButton(toolbar, "原图", () => { fit = false; SetScale(1); });
        AddButton(toolbar, "复制图片", Copy);
        var layout = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(status, Dock.Bottom);
        layout.Children.Add(toolbar);
        layout.Children.Add(status);
        layout.Children.Add(viewport);
        Content = layout;
        viewport.SizeChanged += (_, _) => { if (fit) Fit(); };
        Loaded += (_, _) => Fit();
        PreviewMouseWheel += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            Zoom(e.Delta > 0 ? 1.25 : 1 / 1.25);
            e.Handled = true;
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key is Key.Add or Key.OemPlus) { Zoom(1.25); e.Handled = true; }
            else if (e.Key is Key.Subtract or Key.OemMinus) { Zoom(1 / 1.25); e.Handled = true; }
        };
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, e) =>
        { Copy(); e.Handled = true; }, (_, e) => { e.CanExecute = true; e.Handled = true; }));
    }

    private static void AddButton(Panel panel, string text, Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(3), MinHeight = 32 };
        button.Click += (_, _) => action();
        panel.Children.Add(button);
    }

    private void Fit()
    {
        double width = Math.Max(1, viewport.ActualWidth - 42), height = Math.Max(1, viewport.ActualHeight - 42);
        SetScale(Math.Min(1, Math.Min(width / bitmap.PixelWidth, height / bitmap.PixelHeight)));
    }

    private void Zoom(double factor) { fit = false; SetScale(scale * factor); }

    private void SetScale(double value)
    {
        // Fit very large decoded pictures too; manual zoom is bounded without
        // allocating resized bitmaps. "Original" means the safe decoded pixel size.
        scale = Math.Clamp(value, .01, 8);
        image.Width = bitmap.PixelWidth * scale;
        image.Height = bitmap.PixelHeight * scale;
        status.Text = $"{scale:P0} · {bitmap.PixelWidth} × {bitmap.PixelHeight} · Ctrl+滚轮缩放";
    }

    private void Copy()
    {
        if (!CopyBitmap(bitmap)) status.Text = "剪贴板暂时不可用，请重试";
        else status.Text = "已复制图片";
    }

    internal static bool CopyBitmap(BitmapSource bitmap)
    {
        // Only the decoded bitmap enters the native clipboard, never a URI/path.
        try { Clipboard.SetImage(bitmap); return true; }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException or System.Security.SecurityException)
        { return false; }
    }

    internal static void Attach(FrameworkElement view, Image image, string caption)
    {
        BitmapSource? Decoded() => image.Source as BitmapSource;
        void Open()
        {
            if (Decoded() is not { } bitmap) return;
            var window = new ReaderImageWindow(bitmap, caption, Window.GetWindow(view));
            window.Show();
        }
        var menu = new ContextMenu();
        var open = new MenuItem { Header = "放大图片" };
        var copy = new MenuItem { Header = "复制图片" };
        open.Click += (_, _) => Open();
        copy.Click += (_, _) =>
        {
            if (Decoded() is { } bitmap && !CopyBitmap(bitmap))
                view.ToolTip = "剪贴板暂时不可用，请重试";
        };
        menu.Items.Add(open);
        menu.Items.Add(copy);
        menu.Opened += (_, _) => open.IsEnabled = copy.IsEnabled = Decoded() is not null;
        view.ContextMenu = menu;
        view.ToolTip = "点击放大图片；右键复制已加载图片";
        image.Cursor = Cursors.Hand;
        view.Focusable = true;
        view.PreviewMouseLeftButtonDown += (_, e) => { if (Decoded() is not null) e.Handled = true; };
        view.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (Decoded() is null) return;
            Open();
            e.Handled = true;
        };
        view.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Space) || Decoded() is null) return;
            Open();
            e.Handled = true;
        };
    }
}

// Keep this helper with the viewer so source-link hosts need only the two window
// files. Owned windows have their own resource lookup tree, unlike child controls.
internal static class ReaderWindowTheme
{
    internal static void Apply(Window window, Window? owner)
    {
        if (owner is null) return;
        if (owner.IsVisible) window.Owner = owner;
        window.Icon = owner.Icon;
        Copy(owner.Resources, window.Resources);
    }

    private static void Copy(ResourceDictionary source, ResourceDictionary destination)
    {
        // Copy dictionary containers and precedence, including merged themes.
        // Brush/style values are safe to reuse; never reuse a resource Source URI
        // or add the owner's mutable dictionary to another window's resource tree.
        foreach (var merged in source.MergedDictionaries)
        {
            var copy = new ResourceDictionary();
            Copy(merged, copy);
            destination.MergedDictionaries.Add(copy);
        }
        foreach (var key in source.Keys) destination[key] = source[key];
    }
}
