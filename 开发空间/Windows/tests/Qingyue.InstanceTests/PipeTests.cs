using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Qingyue.Windows;
using static Program;

internal static class PipeTests
{
    internal static readonly (string Name, Func<Task> Run)[] Cases =
    [
        ("IPC 串行执行并在处理完成后才确认", SerializedCompletionAsync),
        ("IPC 连接超时可重试且支持延迟监听", DelayedListenerAsync),
        ("IPC 严格拒绝非法长度及损坏请求后仍可用", InvalidFramesAsync),
        ("IPC 200 路径完整传递且超限整批拒绝", PathLimitsAsync),
        ("IPC 慢客户端和断连不阻塞其他请求", SlowAndDisconnectedAsync),
        ("IPC UI 处理失败明确回传且继续服务", HandlerFailureAsync),
        ("IPC 发送方取消后已接收请求仍执行一次", CallerCancellationAsync),
        ("IPC 退出取消在途请求并可重建相同端点", DisposeAsync),
    ];

    private static string Name() => "qingyue-ipc-test-" + Guid.NewGuid().ToString("N");
    private static SingleInstanceChannel Server(string name, Func<string[], Task> handler) =>
        new(name, handler, ex => throw new InvalidOperationException("监听失败", ex));
    private static string[] PathRequest(string name = "中文 空格😀.txt") => [Path.Combine(Path.GetTempPath(), name)];

    private static async Task SerializedCompletionAsync()
    {
        string name = Name();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new ConcurrentQueue<string[]>();
        int active = 0, peak = 0;
        await using var server = Server(name, async paths =>
        {
            int count = Interlocked.Increment(ref active);
            peak = Math.Max(peak, count);
            received.Enqueue(paths);
            started.TrySetResult();
            await release.Task;
            Interlocked.Decrement(ref active);
        });
        var first = SingleInstanceChannel.TryForwardAsync(name, PathRequest(), 1000);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = SingleInstanceChannel.TryForwardAsync(name, [], 1000);
        await Task.Delay(150);
        Check(!first.IsCompleted && !second.IsCompleted, "处理尚未完成就返回了成功回执");
        Check(received.Count == 1, "第二个请求绕过了串行处理");
        release.SetResult();
        Check((await Task.WhenAll(first, second)).All(ok => ok), "未收到处理完成回执");
        Check(peak == 1 && received.Count == 2, "请求处理发生并发或丢失");
        Check(received.First().SequenceEqual(PathRequest()) && received.Last().Length == 0, "Unicode 路径或激活请求损坏");
    }

    private static async Task DelayedListenerAsync()
    {
        string name = Name();
        Check(!await SingleInstanceChannel.TryForwardAsync(name, PathRequest(), 50), "缺失端点不应返回已投递");
        int received = 0;
        await using var server = Server(name, _ => { received++; return Task.CompletedTask; });
        Check(await SingleInstanceChannel.TryForwardAsync(name, PathRequest(), 1000), "延迟监听后重试失败");
        Check(received == 1, "连接建立前的重试产生重复投递");
    }

    private static async Task InvalidFramesAsync()
    {
        string name = Name();
        int received = 0;
        await using var server = Server(name, _ => { received++; return Task.CompletedTask; });
        foreach (int length in new[] { -1, 0, 65537, int.MaxValue })
        {
            using var pipe = await ConnectAsync(name);
            byte[] header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, length);
            await pipe.WriteAsync(header);
            Check(!await ReadSuccessAsync(pipe), "非法帧长度未被拒绝：" + length);
        }
        foreach (string json in new[]
        {
            "{", "null", """{"Version":2,"Paths":[]}""", """{"Version":1,"Paths":null}""",
            """{"Version":1,"Paths":["relative.txt"]}""", """{"Version":1,"Paths":[null]}""",
        })
        {
            using var pipe = await ConnectAsync(name);
            await WriteAsync(pipe, Encoding.UTF8.GetBytes(json));
            Check(!await ReadSuccessAsync(pipe), "损坏请求未被拒绝：" + json);
        }
        Check(received == 0, "非法请求进入了文件打开回调");
        Check(await SingleInstanceChannel.TryForwardAsync(name, [], 1000), "非法请求使监听器停止");
    }

    private static async Task PathLimitsAsync()
    {
        string name = Name();
        string[] paths = Enumerable.Range(0, 200).Select(i => Path.Combine(Path.GetTempPath(), $"{i}.txt")).ToArray();
        string[]? received = null;
        await using var server = Server(name, values => { received = values; return Task.CompletedTask; });
        Check(await SingleInstanceChannel.TryForwardAsync(name, paths, 1000), "200 路径请求失败");
        Check(received is not null && received.SequenceEqual(paths), "合法批次被截断或重排");
        string[] tooMany = [.. paths, Path.Combine(Path.GetTempPath(), "201.txt")];
        await ThrowsAsync<InvalidDataException>(() => SingleInstanceChannel.TryForwardAsync(name, tooMany, 1000));
        await ThrowsAsync<InvalidDataException>(() => SingleInstanceChannel.TryForwardAsync(name,
            Enumerable.Range(0, 200).Select(i => Path.Combine(Path.GetTempPath(), $"{i}-{new string('x', 400)}.txt")).ToArray(), 1000));
        using var pipe = await ConnectAsync(name);
        await WriteAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Paths = tooMany }));
        Check(!await ReadSuccessAsync(pipe), "接收端静默截断第 201 个路径");
        Check(received!.SequenceEqual(paths), "超限请求被部分投递");
    }

    private static async Task SlowAndDisconnectedAsync()
    {
        string name = Name();
        int received = 0;
        await using var server = Server(name, _ => { received++; return Task.CompletedTask; });
        using var slow = await ConnectAsync(name);
        await slow.WriteAsync(new byte[] { 10 });
        Check(await SingleInstanceChannel.TryForwardAsync(name, PathRequest(), 1000), "慢客户端阻塞了后续请求");
        Check(!await ReadSuccessAsync(slow), "不完整消息超时后未明确拒绝");
        using (var broken = await ConnectAsync(name))
            await broken.WriteAsync(new byte[] { 10, 0, 0, 0, (byte)'{' });
        Check(await SingleInstanceChannel.TryForwardAsync(name, [], 1000), "断连后无法继续处理请求");
        Check(received == 2, "畸形/断连请求被执行或正常请求丢失");
    }

    private static async Task HandlerFailureAsync()
    {
        string name = Name();
        int received = 0;
        await using var server = Server(name, _ =>
        {
            if (received++ == 0) throw new IOException("窗口正在退出，未打开文件");
            return Task.CompletedTask;
        });
        var error = await ThrowsAsync<IOException>(() => SingleInstanceChannel.TryForwardAsync(name, PathRequest(), 1000));
        Check(error.Message.Contains("未打开文件"), "接收端失败没有回传到发送方");
        Check(await SingleInstanceChannel.TryForwardAsync(name, [], 1000), "一次处理失败使后续服务停止");
    }

    private static async Task DisposeAsync()
    {
        string name = Name();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Server(name, _ => { started.TrySetResult(); return release.Task; });
        var pending = SingleInstanceChannel.TryForwardAsync(name, PathRequest(), 1000);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            await ThrowsAsync<IOException>(() => pending);
            Check(!await SingleInstanceChannel.TryForwardAsync(name, [], 50), "退出后仍有遗留监听");
            await using var restarted = Server(name, _ => Task.CompletedTask);
            Check(await SingleInstanceChannel.TryForwardAsync(name, [], 1000), "清理后不能重建相同端点");
        }
        finally { release.TrySetResult(); await server.DisposeAsync(); }
    }

    private static async Task CallerCancellationAsync()
    {
        string name = Name();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int count = 0;
        await using var server = Server(name, async _ =>
        {
            Interlocked.Increment(ref count);
            started.TrySetResult();
            await release.Task;
            completed.TrySetResult();
        });
        using var cancel = new CancellationTokenSource();
        var forwarded = SingleInstanceChannel.TryForwardAsync(name, PathRequest(), 1000, cancel.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancel.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => forwarded);
            release.SetResult();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(count == 1, "取消回复等待导致已接收文件丢失或重复执行");
            Check(await SingleInstanceChannel.TryForwardAsync(name, [], 1000), "发送方取消后监听停止");
        }
        finally { release.TrySetResult(); }
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(string name)
    {
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(2000);
        return pipe;
    }

    private static async Task WriteAsync(Stream pipe, byte[] bytes)
    {
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await pipe.WriteAsync(header);
        await pipe.WriteAsync(bytes);
        await pipe.FlushAsync();
    }

    private static async Task<bool> ReadSuccessAsync(Stream pipe)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        byte[] header = new byte[4];
        await pipe.ReadExactlyAsync(header, timeout.Token);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        Check(length is > 0 and <= 65536, "回执消息长度无效");
        byte[] bytes = new byte[length];
        await pipe.ReadExactlyAsync(bytes, timeout.Token);
        using var json = JsonDocument.Parse(bytes);
        Check(json.RootElement.GetProperty("Version").GetInt32() == 1, "回执协议版本无效");
        return json.RootElement.GetProperty("Success").GetBoolean();
    }

    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (T ex) { return ex; }
        throw new InvalidOperationException("没有抛出预期异常：" + typeof(T).Name);
    }
}
