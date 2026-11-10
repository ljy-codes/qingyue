using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace Qingyue.Windows;

public partial class App : Application
{
    private Mutex? instance;
    private bool ownsInstance;
    private SingleInstanceChannel? instanceChannel;
    private readonly TaskCompletionSource<MainWindow> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("操作未完成，窗口中的编辑仍保留。\n" + args.Exception.Message, "轻阅", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        try
        {
            string root = Environment.GetEnvironmentVariable("QINGYUE_SUPPORT_DIRECTORY")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Qingyue");
            root = Path.GetFullPath(root);
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..24];
            string pipeName = SingleInstanceChannel.PipeName(identity);
            string[] paths = SingleInstanceChannel.PreparePaths(e.Args);
            instance = new Mutex(false, @"Local\Qingyue-" + identity);
            var startup = Stopwatch.StartNew();
            while (!TryOwnInstance())
            {
                if (await SingleInstanceChannel.TryForwardAsync(pipeName, paths))
                {
                    Shutdown();
                    return;
                }
                // The owner may still be creating its endpoint, or may have exited/crashed meanwhile.
                if (startup.Elapsed >= TimeSpan.FromSeconds(10))
                    throw new TimeoutException("现有轻阅实例未能接收请求。请检查现有窗口后重新打开文件。");
            }
            instanceChannel = new SingleInstanceChannel(pipeName, OpenForwardedPathsAsync, ReportChannelFailure);
            var window = new MainWindow(root);
            MainWindow = window;
            window.Show();
            await window.InitializeAsync();
            if (paths.Length > 0) await window.OpenPathsAsync(paths);
            ready.TrySetResult(window);
        }
        catch (Exception ex)
        {
            ready.TrySetCanceled();
            MessageBox.Show("轻阅无法完成打开请求：\n" + ex.Message +
                (e.Args.Length > 0 ? $"\n\n本次 {e.Args.Length} 个文件尚未确认全部打开，请检查窗口后重试。" : ""),
                "轻阅", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private bool TryOwnInstance()
    {
        if (ownsInstance) return true;
        try { ownsInstance = instance!.WaitOne(0); }
        catch (AbandonedMutexException) { ownsInstance = true; }
        return ownsInstance;
    }

    private async Task OpenForwardedPathsAsync(string[] paths)
    {
        var window = await ready.Task;
        await Dispatcher.InvokeAsync(async () =>
        {
            if (exiting || !window.IsLoaded) throw new IOException("现有窗口正在退出，请重新打开文件。");
            var handle = new WindowInteropHelper(window).Handle;
            window.Show();
            if (IsIconic(handle)) ShowWindow(handle, 9); // SW_RESTORE retains the previous maximized state.
            window.Activate();
            if (!SetForegroundWindow(handle)) FlashWindow(handle, true);
            // Let save/close dialogs finish before entering MainWindow's file-opening path.
            while (ComponentDispatcher.IsThreadModal && !exiting) await Task.Delay(50);
            if (exiting || !window.IsLoaded) throw new IOException("现有窗口正在退出，请重新打开文件。");
            if (paths.Length == 0) return;
            await window.OpenPathsAsync(paths);
            // MainWindow can refuse additions during shutdown or at its tab limit. Do not ACK lost paths.
            if (exiting || paths.Any(path => !window.Tabs.Any(tab =>
                string.Equals(tab.File.Path, path, StringComparison.OrdinalIgnoreCase))))
                throw new IOException("部分文件未能在现有窗口中打开。请检查打开提示、标签数量及窗口退出状态后重试。");
        }).Task.Unwrap();
    }

    private void ReportChannelFailure(Exception exception)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (exiting) return;
            MessageBox.Show("轻阅无法继续接收文件打开请求，请重启轻阅。\n" + exception.Message,
                "轻阅", MessageBoxButton.OK, MessageBoxImage.Error);
            instanceChannel?.Dispose();
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        exiting = true;
        ready.TrySetCanceled();
        instanceChannel?.Dispose();
        if (ownsInstance) instance?.ReleaseMutex();
        instance?.Dispose();
        base.OnExit(e);
    }

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool FlashWindow(IntPtr window, bool invert);
}
