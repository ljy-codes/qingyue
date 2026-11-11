using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Qingyue.Core;

namespace Qingyue.Windows;

public partial class MainWindow
{
    private readonly SemaphoreSlim openGate = new(1);
    private LoadingOperation? loading;
    private readonly Queue<string> operationDiagnostics = new();

    public void CancelLoading()
    {
        loading?.Cancellation.Cancel();
        CancelReadingSharing();
    }

    private LoadingOperation BeginLoading(string stage, EditorTab? tab = null)
    {
        CancelLoading();
        var operation = new LoadingOperation(this, stage, tab);
        loading = operation;
        OperationPanel.Visibility = Visibility.Visible;
        OperationProgress.IsIndeterminate = true;
        OperationLabel.Text = stage;
        return operation;
    }

    private void CancelInactiveLoading()
    {
        if (loading?.Tab is { } tab && CurrentTab != tab) CancelLoading();
    }

    private void ShowOperationDiagnostics()
    {
        Notify("以下仅记录本次运行的阶段和耗时，不包含文件名、路径、正文或图片。\n\n" +
            (operationDiagnostics.Count == 0 ? "暂无打开记录。" : string.Join("\n\n", operationDiagnostics)),
            "打开与阅读诊断");
    }

    private async Task ShareReadingWithProgressAsync(bool export)
    {
        using var operation = BeginLoading(export ? "正在准备 HTML 导出" : "正在准备全文复制", CurrentTab);
        if (export) await ExportReadingHtmlAsync(operation.Token);
        else await CopyReadingDocumentAsync(operation.Token);
    }

    private async Task<bool> EnterReadingAsync(EditorTab tab, int? sourceAnchor = null, bool focus = true,
        LoadingOperation? existingOperation = null)
    {
        if (CanReuseReading(tab))
        {
            EnterReading(tab, sourceAnchor, focus);
            return true;
        }
        using var ownedOperation = existingOperation is null ? BeginLoading("正在解析正文", tab) : null;
        var operation = existingOperation ?? ownedOperation!;
        operation.Tab = tab;
        string text = tab.File.Text;
        string? path = tab.File.Path;
        TextKind kind = tab.Kind;
        var expected = new ExternalFileRefresh(tab.File);
        int anchor = Math.Clamp(sourceAnchor ?? CurrentSourceAnchor(tab), 0, text.Length);
        try
        {
            operation.Stage("正在解析正文");
            var model = await Task.Run(() => ReadingModel.Build(text, kind), operation.Token).WaitAsync(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            anchor = SnapToReadingBlockStart(model, anchor);
            operation.Stage("正在排版阅读内容");
            // WPF objects are created on their owning dispatcher, in bounded batches.
            await Dispatcher.Yield(DispatcherPriority.Background);
            RenderedReading? rendered = null;
            if (PreviewLimits.CanRender(text, kind))
                rendered = kind == TextKind.Markdown
                    ? await RenderedReading.MarkdownAsync(model, text, path, settings.FontSize + 1,
                        settings.ReadingWidth, operation.Token)
                    : new RenderedReading(model, sourcePath: path);
            operation.Token.ThrowIfCancellationRequested();
            if (!tabs.Contains(tab) || CurrentTab != tab || approvedShutdown || exitInProgress
                || !expected.IsCurrent || tab.Kind != kind || tab.File.Path != path) return false;
            PublishReading(tab, model, rendered, text, path, kind, anchor, focus);
            operation.Stage("阅读已就绪");
            return true;
        }
        catch (OperationCanceledException)
        {
            if (CurrentTab == tab && !exitInProgress) StatusText.Text = "已取消预览，原文与编辑仍保留。";
            return false;
        }
    }

    private async Task ToggleReadingAsync()
    {
        if (CurrentTab is not { } tab) return;
        if (loading?.Tab == tab)
        {
            CancelLoading();
            return;
        }
        if (tab.IsReading) ToggleReading();
        else
        {
            int anchor = EditingViewportAnchor(tab);
            tab.ViewportAnchor = anchor;
            tab.ViewportDelta = 0;
            await EnterReadingAsync(tab, anchor);
            RefreshCurrent();
            if (tab.IsReading) RestoreReadingViewport(tab, anchor, tab.ViewportDelta);
        }
    }

    private sealed class LoadingOperation : IDisposable
    {
        private readonly MainWindow owner;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        private readonly List<string> phases = [];
        private string stage;
        private long stageStart;
        public CancellationTokenSource Cancellation { get; } = new();
        public CancellationToken Token => Cancellation.Token;
        public EditorTab? Tab { get; set; }

        public LoadingOperation(MainWindow owner, string stage, EditorTab? tab)
        {
            this.owner = owner;
            this.stage = stage;
            Tab = tab;
            timer.Tick += Tick;
            timer.Start();
        }

        public void Stage(string next)
        {
            Token.ThrowIfCancellationRequested();
            phases.Add($"{stage} {clock.ElapsedMilliseconds - stageStart:N0} ms");
            stageStart = clock.ElapsedMilliseconds;
            stage = next;
            Tick(null, EventArgs.Empty);
        }

        private void Tick(object? sender, EventArgs e)
        {
            if (ReferenceEquals(owner.loading, this))
                owner.OperationLabel.Text = $"{stage} · 已用 {clock.Elapsed.TotalSeconds:F1} 秒";
        }

        public void Dispose()
        {
            timer.Stop();
            timer.Tick -= Tick;
            phases.Add($"{stage} {clock.ElapsedMilliseconds - stageStart:N0} ms");
            owner.operationDiagnostics.Enqueue(
                $"{(Cancellation.IsCancellationRequested ? "已取消" : "已结束")} · 总耗时 {clock.ElapsedMilliseconds:N0} ms\n" +
                string.Join(" → ", phases));
            while (owner.operationDiagnostics.Count > 12) owner.operationDiagnostics.Dequeue();
            if (ReferenceEquals(owner.loading, this))
            {
                owner.loading = null;
                owner.OperationPanel.Visibility = Visibility.Collapsed;
            }
            Cancellation.Dispose();
        }
    }
}
