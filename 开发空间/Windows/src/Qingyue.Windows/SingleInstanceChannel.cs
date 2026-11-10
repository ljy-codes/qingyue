using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace Qingyue.Windows;

/// <summary>
/// One length-prefixed UTF-8 JSON request and one completion reply per connection.
/// A connection is never retried after delivery: a lost reply must remain visible to the caller.
/// </summary>
internal sealed class SingleInstanceChannel : IDisposable, IAsyncDisposable
{
    internal const int MaxMessageBytes = 64 * 1024;
    internal const int MaxPaths = 200;
    private const int MaxConnections = 16;
    private readonly string pipeName;
    private readonly Func<string[], Task> open;
    private readonly Action<Exception> reportFailure;
    private readonly CancellationTokenSource stopping = new();
    private readonly SemaphoreSlim slots = new(MaxConnections);
    private readonly HashSet<NamedPipeServerStream> pipes = [];
    private readonly Channel<Pending> requests = Channel.CreateBounded<Pending>(
        new BoundedChannelOptions(MaxConnections) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task listener, worker;
    private bool disposed;

    internal SingleInstanceChannel(string pipeName, Func<string[], Task> open, Action<Exception> reportFailure)
    {
        this.pipeName = pipeName;
        this.open = open;
        this.reportFailure = reportFailure;
        // Create synchronously so an unavailable endpoint fails startup, not a detached background task.
        var first = CreatePipe();
        worker = Task.Run(ProcessRequestsAsync);
        listener = Task.Run(() => ListenAsync(first));
    }

    internal static string PipeName(string identity) =>
        $"Qingyue-{Process.GetCurrentProcess().SessionId}-{identity}-open-v1";

    internal static string[] PreparePaths(IEnumerable<string> paths)
    {
        var result = paths.Take(MaxPaths + 1).ToArray();
        if (result.Length > MaxPaths)
            throw new InvalidDataException($"一次最多打开 {MaxPaths} 个文件；本次请求未发送，请分批打开。");
        // Resolve relative paths in the launching process, never in the existing reader's working directory.
        for (int i = 0; i < result.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(result[i])) throw new InvalidDataException("文件路径不能为空。");
            result[i] = Path.GetFullPath(result[i]);
        }
        _ = Encode(new Request(1, result));
        return result;
    }

    /// <returns>False only when connection timed out before any message was sent.</returns>
    internal static async Task<bool> TryForwardAsync(string pipeName, string[] paths, int connectMilliseconds = 250,
        CancellationToken cancellationToken = default)
    {
        byte[] message = Encode(new Request(1, PreparePaths(paths)));
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(connectMilliseconds, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { return false; }

        // The launcher may grant foreground permission to the actual pipe owner, never to a claimed PID.
        if (GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint processId))
            AllowSetForegroundWindow(processId);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await WriteFrameAsync(pipe, message, deadline.Token).ConfigureAwait(false);
            var reply = JsonSerializer.Deserialize<Reply>(await ReadFrameAsync(pipe, deadline.Token).ConfigureAwait(false));
            if (reply is null || reply.Version != 1) throw new InvalidDataException("现有窗口返回了无效回执。");
            if (!reply.Success) throw new IOException(reply.Error ?? "现有窗口未能打开文件，请重试。");
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("等待现有窗口处理超时。文件可能已接收，请检查现有窗口后重试。");
        }
    }

    private NamedPipeServerStream CreatePipe()
    {
        var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, MaxConnections,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        lock (pipes)
        {
            if (disposed) { pipe.Dispose(); throw new OperationCanceledException(stopping.Token); }
            pipes.Add(pipe);
        }
        return pipe;
    }

    private async Task ListenAsync(NamedPipeServerStream first)
    {
        NamedPipeServerStream? waiting = first;
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                await slots.WaitAsync(stopping.Token).ConfigureAwait(false);
                waiting ??= CreatePipe();
                await waiting.WaitForConnectionAsync(stopping.Token).ConfigureAwait(false);
                _ = ReceiveAsync(waiting);
                waiting = null;
            }
        }
        catch (Exception ex)
        {
            if (!stopping.IsCancellationRequested) reportFailure(ex);
        }
        finally
        {
            if (waiting is not null) RemovePipe(waiting);
        }
    }

    private async Task ReceiveAsync(NamedPipeServerStream pipe)
    {
        try
        {
            Reply reply;
            try
            {
                using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                readDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                var request = JsonSerializer.Deserialize<Request>(
                    await ReadFrameAsync(pipe, readDeadline.Token).ConfigureAwait(false));
                if (request is null || request.Version != 1 || request.Paths is null)
                    throw new InvalidDataException("不支持的文件打开请求。");
                if (request.Paths.Any(path => path is null || !Path.IsPathFullyQualified(path)))
                    throw new InvalidDataException("转发路径必须为绝对路径。");
                var paths = PreparePaths(request.Paths);
                var pending = new Pending(paths);
                if (!requests.Writer.TryWrite(pending)) throw new IOException("现有窗口请求过多，请稍后重试。");
                // ACK means the UI handler completed, not merely that bytes entered a queue.
                reply = await pending.Completion.Task.WaitAsync(stopping.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!stopping.IsCancellationRequested)
            {
                reply = new Reply(1, false, ex.Message[..Math.Min(ex.Message.Length, 2048)]);
            }
            using var writeDeadline = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
            writeDeadline.CancelAfter(TimeSpan.FromSeconds(5));
            await WriteFrameAsync(pipe, Encode(reply), writeDeadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A caller can disconnect at any point; queued work still executes and the listener stays alive.
            Trace.WriteLine("Qingyue instance connection ended: " + ex.Message);
        }
        finally { RemovePipe(pipe); slots.Release(); }
    }

    private async Task ProcessRequestsAsync()
    {
        try
        {
            await foreach (var pending in requests.Reader.ReadAllAsync(stopping.Token).ConfigureAwait(false))
            {
                try
                {
                    await open(pending.Paths).WaitAsync(stopping.Token).ConfigureAwait(false);
                    pending.Completion.TrySetResult(new Reply(1, true, null));
                }
                catch (Exception ex)
                {
                    pending.Completion.TrySetResult(new Reply(1, false, ex.Message[..Math.Min(ex.Message.Length, 2048)]));
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    private void RemovePipe(NamedPipeServerStream pipe)
    {
        lock (pipes) pipes.Remove(pipe);
        pipe.Dispose();
    }

    private static byte[] Encode<T>(T value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length is < 1 or > MaxMessageBytes)
            throw new InvalidDataException($"文件打开请求超过 {MaxMessageBytes / 1024} KiB；本次请求未发送，请分批打开。");
        return bytes;
    }

    private static async Task<byte[]> ReadFrameAsync(Stream pipe, CancellationToken token)
    {
        byte[] header = new byte[4];
        await pipe.ReadExactlyAsync(header, token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > MaxMessageBytes) throw new InvalidDataException("文件打开消息长度无效。");
        byte[] body = new byte[length];
        await pipe.ReadExactlyAsync(body, token).ConfigureAwait(false);
        return body;
    }

    private static async Task WriteFrameAsync(Stream pipe, byte[] message, CancellationToken token)
    {
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, message.Length);
        await pipe.WriteAsync(header, token).ConfigureAwait(false);
        await pipe.WriteAsync(message, token).ConfigureAwait(false);
        await pipe.FlushAsync(token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        NamedPipeServerStream[] openPipes;
        lock (pipes)
        {
            if (disposed) return;
            disposed = true;
            openPipes = pipes.ToArray();
        }
        stopping.Cancel();
        requests.Writer.TryComplete();
        foreach (var pipe in openPipes) pipe.Dispose();
        // Do not block the WPF thread on handlers that may themselves be awaiting that thread.
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await Task.WhenAll(listener, worker).ConfigureAwait(false);
        // Receive tasks remove their streams and release their slots in finally.
        while (true)
        {
            lock (pipes) { if (pipes.Count == 0) break; }
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    private sealed record Request(int Version, string[] Paths);
    private sealed record Reply(int Version, bool Success, string? Error);
    private sealed record Pending(string[] Paths)
    {
        internal TaskCompletionSource<Reply> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
