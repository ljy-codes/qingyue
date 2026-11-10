using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Qingyue.Core;
using Qingyue.Windows;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (Environment.GetEnvironmentVariable("QINGYUE_INSTANCE_TEST_HOST") == "1")
        {
            var app = new App();
            app.InitializeComponent();
            app.DispatcherUnhandledException += (_, failure) => Console.Error.WriteLine(failure.Exception);
            var seen = new HashSet<Window>();
            var diagnostics = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            diagnostics.Tick += (_, _) =>
            {
                foreach (Window window in app.Windows)
                    if (window != app.MainWindow && seen.Add(window))
                        Console.Error.WriteLine("DIALOG " + window.Title + " " + DialogText(window));
            };
            diagnostics.Start();
            if (Environment.GetEnvironmentVariable("QINGYUE_INSTANCE_TEST_PAUSE") is { } gate)
            {
                app.Startup += (_, _) => app.Dispatcher.BeginInvoke(() =>
                {
                    Console.WriteLine(app.MainWindow is MainWindow { Tabs.Count: 0 } ? "INITIALIZING" : "TOO_LATE");
                    var clock = Stopwatch.StartNew();
                    while (!System.IO.File.Exists(gate) && clock.Elapsed < TimeSpan.FromSeconds(10))
                        Thread.Sleep(20);
                }, DispatcherPriority.Send);
            }
            return app.Run();
        }
        return RunAsync(args).GetAwaiter().GetResult();
    }

    private static string DialogText(DependencyObject node)
    {
        var result = new List<string>();
        foreach (var child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is string text) result.Add(text);
            else if (child is System.Windows.Controls.TextBlock block) result.Add(block.Text);
            else if (child is DependencyObject element) result.Add(DialogText(element));
        }
        return string.Join(" ", result);
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("第二进程转发多个相对路径到原窗口", ForwardMultiplePathsAsync),
            ("初始化期间请求排队且首批文件先打开", QueueDuringInitializationAsync),
            ("空参数恢复最小化及最大化窗口", ActivateAsync),
            ("并发冷启动不丢失任何进程的文件", ConcurrentStartupAsync),
            ("启动竞态中接管放弃的互斥量", AbandonedOwnerAsync),
            ("资料目录隔离及同目录大小写兼容", ProfileIsolationAsync),
            ("正常退出后释放互斥量及管道可重启", RestartAsync),
        };
        tests = tests.Concat(PipeTests.Cases).ToArray();
        if (args.Contains("--ipc-only")) tests = PipeTests.Cases;
        if (args is ["--filter", var filter]) tests = tests.Where(t => t.Name.Contains(filter)).ToArray();
        int failures = 0;
        foreach (var (name, run) in tests)
        {
            try { await run(); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {name}\n{ex}"); }
        }
        Console.WriteLine($"RESULT {tests.Length - failures}/{tests.Length} passed");
        return failures == 0 ? 0 : 1;
    }

    private static async Task ForwardMultiplePathsAsync()
    {
        using var fixture = new AppFixture();
        string initial = fixture.File("最初.txt");
        string incoming = Path.Combine(fixture.Root, "另一个工作目录");
        Directory.CreateDirectory(incoming);
        System.IO.File.WriteAllText(Path.Combine(incoming, "第二个 空格.txt"), "第二个");
        System.IO.File.WriteAllText(Path.Combine(incoming, "最后一个 😀.txt"), "最后一个");
        var primary = fixture.Start(initial);
        await WaitForAsync(() => WindowTitle(primary).Contains("最初.txt"), "首进程没有显示初始文档");
        var secondary = fixture.StartAt(fixture.Profile, incoming, ["第二个 空格.txt", "最后一个 😀.txt"]);
        try { await WaitForAsync(() => secondary.HasExited, "第二进程没有转发后退出（旧行为会弹出“已在运行”）", 6000); }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException($"{ex.Message}\n原窗口：{WindowTitle(primary)}\n{fixture.Diagnostics}", ex);
        }
        Check(secondary.ExitCode == 0, "第二进程转发失败");
        await WaitForAsync(() => WindowTitle(primary).Contains("最后一个 😀.txt"), "原窗口未打开转发的最后一个路径");
        var store = new WorkspaceStore(fixture.Profile);
        await WaitForAsync(async () =>
        {
            var result = await store.LoadAsync();
            return new[] { "最初.txt", "第二个 空格.txt", "最后一个 😀.txt" }
                .All(name => result.Documents.Any(d => Path.GetFileName(d.File.Path) == name));
        }, "多路径转发丢失文档");
    }

    private static async Task QueueDuringInitializationAsync()
    {
        using var fixture = new AppFixture();
        string gate = Path.Combine(fixture.Root, "release-initialization");
        var primary = fixture.StartAt(fixture.Profile, fixture.Root, [fixture.File("首批.txt")], gate);
        string? marker = await primary.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Check(marker == "INITIALIZING", "测试未能进入真实 InitializeAsync 等待阶段：" + marker);
        string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fixture.Profile.ToUpperInvariant())))[..24];
        var forwarded = SingleInstanceChannel.TryForwardAsync(SingleInstanceChannel.PipeName(identity),
            [fixture.File("排队文件.txt")], 2000);
        await Task.Delay(200);
        Check(!forwarded.IsCompleted, "初始化完成前错误地确认了文件请求");
        System.IO.File.WriteAllText(gate, "");
        Check(await forwarded.WaitAsync(TimeSpan.FromSeconds(10)), "初始化期间转发失败");
        await WaitForAsync(() => WindowTitle(primary).Contains("排队文件.txt"), "排队文件未在初始化完成后打开");
        await WaitForAsync(async () =>
        {
            var result = await new WorkspaceStore(fixture.Profile).LoadAsync();
            return result.Documents.Any(d => Path.GetFileName(d.File.Path) == "首批.txt") &&
                result.Documents.Any(d => Path.GetFileName(d.File.Path) == "排队文件.txt");
        }, "初始化与转发交错丢失首批文件");
    }

    private static async Task ActivateAsync()
    {
        using var fixture = new AppFixture();
        var primary = fixture.Start(fixture.File("激活.txt"));
        await WaitForAsync(() => WindowTitle(primary).Contains("激活.txt"), "主窗口没有就绪");
        primary.Refresh();
        IntPtr window = primary.MainWindowHandle;
        ShowWindow(window, 3); // SW_MAXIMIZE
        await WaitForAsync(() => IsZoomed(window), "无法最大化测试窗口");
        ShowWindow(window, 6); // SW_MINIMIZE
        await WaitForAsync(() => IsIconic(window), "无法最小化测试窗口");
        var secondary = fixture.Start();
        await WaitForAsync(() => secondary.HasExited && !IsIconic(window), "空参数启动未恢复原窗口");
        Check(secondary.ExitCode == 0 && !primary.HasExited, "激活错误地关闭了主进程");
        Check(IsZoomed(window), "恢复时丢失之前的最大化状态");
        Check(WindowTitle(primary).Contains("激活.txt"), "激活改变了当前文档");
    }

    private static async Task ConcurrentStartupAsync()
    {
        using var fixture = new AppFixture();
        string[] paths = Enumerable.Range(0, 6).Select(i => fixture.File($"冷启动 {i}.txt")).ToArray();
        var processes = paths.Select(path => fixture.Start(path)).ToArray();
        await WaitForAsync(() => processes.Count(p => !p.HasExited) == 1, "并发启动没有收敛到单一进程", 15000);
        Check(processes.Where(p => p.HasExited).All(p => p.ExitCode == 0), "并发启动存在转发错误");
        await WaitForAsync(async () =>
        {
            var result = await new WorkspaceStore(fixture.Profile).LoadAsync();
            return paths.All(path => result.Documents.Any(d => d.File.Path == path));
        }, "并发冷启动丢失文件", 15000);
    }

    private static async Task ProfileIsolationAsync()
    {
        using var fixture = new AppFixture();
        string otherProfile = Path.Combine(fixture.Root, "other-profile");
        var first = fixture.Start(fixture.File("甲资料.txt"));
        var second = fixture.StartAt(otherProfile, fixture.Root, [fixture.File("乙资料.txt")]);
        await WaitForAsync(() => WindowTitle(first).Contains("甲资料.txt") && WindowTitle(second).Contains("乙资料.txt"),
            "不同资料目录没有各自创建窗口");
        var forwarded = fixture.StartAt(fixture.Profile.ToUpperInvariant(), fixture.Root, [fixture.File("仅甲.txt")]);
        await WaitForAsync(() => forwarded.HasExited && WindowTitle(first).Contains("仅甲.txt"), "同目录大小写身份发生变化");
        Check(WindowTitle(second).Contains("乙资料.txt"), "请求被转发到了其他资料目录");
        Check(forwarded.ExitCode == 0, "同目录转发失败");
    }

    private static async Task AbandonedOwnerAsync()
    {
        using var fixture = new AppFixture();
        using var held = new ManualResetEventSlim();
        using var abandon = new ManualResetEventSlim();
        string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fixture.Profile.ToUpperInvariant())))[..24];
        var owner = new Thread(() =>
        {
            using var mutex = new Mutex(true, @"Local\Qingyue-" + identity);
            held.Set();
            abandon.Wait();
            // Simulate the original owner dying after Mutex creation but before pipe creation.
        });
        owner.Start();
        try
        {
            Check(held.Wait(2000), "测试互斥量未就绪");
            var primary = fixture.Start(fixture.File("接管.txt"));
            await Task.Delay(400);
            Check(!primary.HasExited && WindowTitle(primary) == "", "尚有其他所有者时不应创建第二个窗口");
            abandon.Set();
            await WaitForAsync(() => WindowTitle(primary).Contains("接管.txt"), "未接管已放弃的启动身份");
        }
        finally { abandon.Set(); owner.Join(2000); }
    }

    private static async Task RestartAsync()
    {
        using var fixture = new AppFixture();
        var primary = fixture.Start(fixture.File("退出前.txt"));
        await WaitForAsync(() => WindowTitle(primary).Contains("退出前.txt"), "主窗口没有就绪");
        Check(primary.CloseMainWindow(), "无法请求测试窗口正常退出");
        await WaitForAsync(() => primary.HasExited, "窗口没有正常退出");
        Check(primary.ExitCode == 0, "正常退出返回错误");
        var restarted = fixture.Start(fixture.File("重新启动.txt"));
        await WaitForAsync(() => WindowTitle(restarted).Contains("重新启动.txt"), "退出后无法重新获得资料目录身份");
    }

    internal static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }

    internal static Task WaitForAsync(Func<bool> condition, string message, int milliseconds = 10000) =>
        WaitForAsync(() => Task.FromResult(condition()), message, milliseconds);

    internal static async Task WaitForAsync(Func<Task<bool>> condition, string message, int milliseconds = 10000)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds)
        {
            if (await condition()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException(message);
    }

    internal static string WindowTitle(Process process)
    {
        if (process.HasExited) return "";
        process.Refresh();
        var text = new StringBuilder(1024);
        GetWindowText(process.MainWindowHandle, text, text.Capacity);
        return text.ToString();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr window);
}

internal sealed class AppFixture : IDisposable
{
    private readonly List<Process> processes = [];
    private readonly ConcurrentQueue<string> diagnostics = new();
    internal string Diagnostics => string.Join("\n", diagnostics);
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "qingyue-instance-" + Guid.NewGuid().ToString("N"));
    internal string Profile => Path.Combine(Root, "profile");

    internal AppFixture() => Directory.CreateDirectory(Root);

    internal string File(string name)
    {
        string path = Path.Combine(Root, name);
        System.IO.File.WriteAllText(path, name);
        return path;
    }

    internal Process Start(params string[] paths) => StartAt(Profile, Root, paths);

    internal Process StartAt(string profile, string directory, string[] paths, string? initializationGate = null)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, WorkingDirectory = directory,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        // The suite is launched both through the apphost and `dotnet test.dll`.
        // A child of dotnet must receive this assembly before its document paths.
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet",
            StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.Environment["QINGYUE_INSTANCE_TEST_HOST"] = "1";
        start.Environment["QINGYUE_SUPPORT_DIRECTORY"] = profile;
        if (initializationGate is not null) start.Environment["QINGYUE_INSTANCE_TEST_PAUSE"] = initializationGate;
        foreach (string path in paths) start.ArgumentList.Add(path);
        var process = Process.Start(start)!;
        process.ErrorDataReceived += (_, line) => { if (line.Data is not null) diagnostics.Enqueue(line.Data); };
        process.BeginErrorReadLine();
        processes.Add(process);
        return process;
    }

    public void Dispose()
    {
        foreach (var process in processes)
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); }
            process.Dispose();
        }
        // Each fixture owns only this GUID directory; never touch a user's reader profile.
        Directory.Delete(Root, recursive: true);
    }
}
