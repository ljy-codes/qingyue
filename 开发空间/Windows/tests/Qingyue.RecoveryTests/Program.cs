using System.Reflection;
#if !RECOVERY_CORE_ONLY
using System.Windows;
#endif

internal static partial class Program
{
    private static int passed, failed;
    private static string runRoot = "";

    [STAThread]
    private static int Main(string[] args)
    {
        // The caller supplies an evidence directory, never a real application profile.
        if (args.Length != 2 || args[0] != "--root") throw new ArgumentException("Use --root <isolated evidence directory>");
        runRoot = Path.Combine(Path.GetFullPath(args[1]), "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runRoot);
#if RECOVERY_CORE_ONLY
        RunCoreScenarios().GetAwaiter().GetResult();
#else
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/Qingyue.RecoveryTests;component/Theme.xaml", UriKind.Relative)
        });
        app.Startup += async (_, _) =>
        {
            try { await RunCoreScenarios(); await RunWpfScenarios(); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL runner: " + ex); }
            finally
            {
                foreach (Window window in app.Windows.Cast<Window>().ToArray())
                    if (window is Qingyue.Windows.MainWindow main) main.CloseForShutdown();
                app.Shutdown();
            }
        };
        app.Run();
#endif
        Console.WriteLine($"RESULT {passed} passed, {failed} failed; isolated fixtures: {runRoot}");
        return failed == 0 ? 0 : 1;
    }

    private static async Task Scenario(string name, Func<string, Task> test)
    {
        string root = Path.Combine(runRoot, name);
        Directory.CreateDirectory(root);
        try { await test(root); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // Baseline compatibility lets RED exercise the old behavior instead of failing compilation.
    // Every GREEN run also asserts that the integration methods actually exist.
    private static Task Prepare(object target)
    {
        var method = target.GetType().GetMethod("PrepareRecoveryAsync", BindingFlags.Public | BindingFlags.Instance);
        return method is null ? Task.CompletedTask : (Task)method.Invoke(target, null)!;
    }
}
