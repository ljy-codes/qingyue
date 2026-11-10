using System.Windows;
using System.Windows.Threading;
using Qingyue.Windows;

internal static partial class Program
{
    private static int passed, failed;

    [STAThread]
    private static int Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "qingyue-external-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/Qingyue.ExternalChangesTests;component/Theme.xaml", UriKind.Relative)
        });
        app.Startup += async (_, _) =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow(Path.Combine(root, "profile"), restoreSession: false);
                window.Show();
                await window.InitializeAsync();
                // Exercise the controller's actual Initialize/SelectTab/TabChanged
                // hooks; no test-only subscriptions may hide a missing integration.
                await ReproduceSwitchRefresh(window, root);
                if (!args.Contains("--repro")) await RunAdditionalTests(window, root);
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine("FAIL 外部更新流程\n" + ex);
            }
            finally
            {
                window?.CloseForShutdown();
                app.Shutdown();
                Directory.Delete(root, recursive: true);
            }
        };
        app.Run();
        Console.WriteLine($"RESULT {passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    private static async Task ReproduceSwitchRefresh(MainWindow window, string root)
    {
        string path = Path.Combine(root, "switch.txt");
        File.WriteAllText(path, "original");
        await window.OpenPathsAsync([path]);
        var tab = window.CurrentTab!;
        var other = window.NewDocument("other");
        File.WriteAllText(path, "external");
        window.SelectTab(tab);
        await WaitFor(() => tab.File.Text == "external", "切回原标签应异步显示真实磁盘更新");
        Check(ReferenceEquals(window.CurrentTab, tab) && window.Tabs.Contains(other),
            "外部更新留在同一标签且不影响其他标签");
    }

    private static partial Task RunAdditionalTests(MainWindow window, string root);

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        passed++;
        Console.WriteLine("PASS " + name);
    }

    private static async Task WaitFor(Func<bool> predicate, string name)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!predicate() && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        Check(predicate(), name);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
}
