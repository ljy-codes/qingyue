using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;

namespace Qingyue.Windows;

public partial class App : Application
{
    private Mutex? instance;
    private bool ownsInstance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string root = Environment.GetEnvironmentVariable("QINGYUE_SUPPORT_DIRECTORY")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Qingyue");
        root = Path.GetFullPath(root);
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..24];
        instance = new Mutex(true, @"Local\Qingyue-" + identity, out ownsInstance);
        if (!ownsInstance)
        {
            MessageBox.Show("轻阅已在运行。请在现有窗口中打开文件或拖入文件。", "轻阅");
            Shutdown(); return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("操作未完成，窗口中的编辑仍保留。\n" + args.Exception.Message, "轻阅", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        try
        {
            var window = new MainWindow(root);
            MainWindow = window;
            window.Show();
            await window.InitializeAsync();
            if (e.Args.Length > 0) await window.OpenPathsAsync(e.Args);
        }
        catch (Exception ex)
        {
            MessageBox.Show("轻阅无法启动：\n" + ex.Message, "轻阅", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (ownsInstance) instance?.ReleaseMutex();
        instance?.Dispose();
        base.OnExit(e);
    }
}
