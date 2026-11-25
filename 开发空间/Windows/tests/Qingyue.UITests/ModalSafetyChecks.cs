using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Qingyue.Core;

namespace Qingyue.Windows;

internal static class ModalSafetyChecks
{
    public static async Task Run(string root, Action<bool, string> check)
    {
        await Exercise(root, check, chooseSave: false);
        await Exercise(root, check, chooseSave: true);
    }

    private static async Task Exercise(string root, Action<bool, string> check, bool chooseSave)
    {
        string folder = Path.Combine(root, chooseSave ? "modal-save-panel" : "modal-cancel");
        Directory.CreateDirectory(folder);
        string incoming = Path.Combine(folder, "incoming.txt");
        byte[] originalDisk = "DISK INCOMING\r\n"u8.ToArray();
        File.WriteAllBytes(incoming, originalDisk);

        MainWindow? window = null;
        try
        {
            window = await OpenWindow(Path.Combine(folder, "profile"));
            var original = window.CurrentTab ?? throw new InvalidOperationException("缺少初始文档");
            original.Editor.Text = "ORIGINAL UNSAVED 😀";
            await Pump(window);
            check(original.File.IsDirty && original.File.Path is null, "模态安全：原未命名文档处于脏状态");

            var gate = new TaskCompletionSource<DocumentFile>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool openRequested = false;
            SetProperty(window, "OpenDocumentForTest",
                new Func<string, CancellationToken, Task<DocumentFile>>((_, _) =>
                {
                    openRequested = true;
                    return gate.Task;
                }));

            bool promptSeen = false;
            bool originalStayedActiveDuringPrompt = false;
            bool originalStayedActiveDuringSave = false;
            bool savePanelSeen = false;
            bool saveTargetedOriginal = false;
            bool promptTimedOut = false;
            if (chooseSave)
            {
                SetProperty(window, "SaveBeforeCloseForTest", new Func<EditorTab, bool>(tab =>
                {
                    saveTargetedOriginal = ReferenceEquals(tab, original) && original.File.Path is null;
                    return Invoke(window, "RunModalForTest", new Func<bool>(() =>
                    {
                        var panel = new Window
                        {
                            Owner = window,
                            Title = "测试保存面板",
                            Width = 360,
                            Height = 180,
                            WindowStartupLocation = WindowStartupLocation.CenterOwner,
                            ShowInTaskbar = false
                        };
                        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
                        timer.Tick += (_, _) =>
                        {
                            timer.Stop();
                            savePanelSeen = true;
                            originalStayedActiveDuringSave = ReferenceEquals(window.CurrentTab, original)
                                && original.File.Path is null;
                            panel.Close();
                        };
                        timer.Start();
                        panel.ShowDialog();
                        return false;
                    })) is true;
                }));
            }

            var openTask = window.OpenPathsAsync([incoming]);
            await WaitFor(() => openRequested);

            var promptTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            int attempts = 0;
            promptTimer.Tick += (_, _) =>
            {
                attempts++;
                var dialog = Application.Current.Windows.OfType<Window>()
                    .FirstOrDefault(candidate => candidate.IsVisible && candidate.Title == "处理未保存的修改");
                if (dialog is null)
                {
                    if (attempts > 200)
                    {
                        promptTimedOut = true;
                        promptTimer.Stop();
                    }
                    return;
                }

                promptTimer.Stop();
                promptSeen = true;
                originalStayedActiveDuringPrompt = ReferenceEquals(window.CurrentTab, original)
                    && original.File.Path is null
                    && original.Editor.Text == "ORIGINAL UNSAVED 😀";
                gate.TrySetResult(DocumentFile.Open(incoming));

                var delayed = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
                delayed.Tick += (_, _) =>
                {
                    delayed.Stop();
                    if (ReferenceEquals(window.CurrentTab, original)
                        && original.File.Path is null
                        && original.Editor.Text == "ORIGINAL UNSAVED 😀")
                        originalStayedActiveDuringPrompt = true;
                    var button = Descendants(dialog).OfType<Button>()
                        .FirstOrDefault(candidate => Equals(candidate.Content, chooseSave ? "保存" : "取消"));
                    if (button is not null) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else dialog.Close();
                };
                delayed.Start();
            };
            promptTimer.Start();

            bool closeResult = Invoke(window, "CloseTab", original, false, false) as bool? == true;
            await openTask.WaitAsync(TimeSpan.FromSeconds(8));
            await WaitFor(() => window.Tabs.Any(tab =>
                string.Equals(tab.File.Path, Path.GetFullPath(incoming), StringComparison.OrdinalIgnoreCase)));

            check(!promptTimedOut && promptSeen, "模态安全：关闭原脏文档时出现保存提示");
            check(originalStayedActiveDuringPrompt,
                "模态安全：待打开文件读取完成时不提前切换当前文档");
            check(!closeResult, "模态安全：取消关闭后原文档不关闭");
            if (chooseSave)
            {
                check(savePanelSeen, "模态安全：选择保存时显示保存面板");
                check(saveTargetedOriginal && originalStayedActiveDuringSave,
                    "模态安全：保存面板始终针对原文档，期间不切换到待打开文件");
            }

            check(window.Tabs.Contains(original) && original.File.IsDirty
                && original.File.Path is null && original.Editor.Text == "ORIGINAL UNSAVED 😀",
                "模态安全：取消关闭后原脏标签和正文仍完整保留");

            window.SelectTab(original);
            await Pump(window);
            check(window.CurrentTab == original && original.File.Path is null,
                "模态安全：原文档继续可独立访问");
            var incomingTab = window.Tabs.Single(tab =>
                string.Equals(tab.File.Path, Path.GetFullPath(incoming), StringComparison.OrdinalIgnoreCase));
            window.SelectTab(incomingTab);
            await Pump(window);
            check(window.CurrentTab == incomingTab && incomingTab.Editor.Text == "DISK INCOMING\n",
                "模态安全：待打开文件在原模态结束后独立打开");
            check(File.ReadAllBytes(incoming).SequenceEqual(originalDisk),
                "模态安全：整个取消/保存流程绝不改写 incoming 文件");
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

    private static void SetProperty(object target, string name, object value)
    {
        var property = target.GetType().GetProperty(name,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new MissingMemberException(target.GetType().Name, name);
        property.SetValue(target, value);
    }

    private static object? Invoke(object target, string name, params object?[] arguments)
    {
        var method = target.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .FirstOrDefault(candidate =>
                candidate.Name == name && candidate.GetParameters().Length == arguments.Length)
            ?? throw new MissingMethodException(target.GetType().FullName, name);
        return method.Invoke(target, arguments);
    }

    private static async Task Pump(Window window)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(6))
                throw new TimeoutException("等待模态安全状态超时。");
            await Task.Delay(20);
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
