using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using Qingyue.Core;

namespace Qingyue.Windows;

public partial class MainWindow
{
    private const int ExternalRefreshDebounceMs = 160;
    private const int ExternalRefreshIntervalMs = 800;
    private readonly DispatcherTimer externalRefreshTimer = new(DispatcherPriority.Background);
    private readonly ConditionalWeakTable<EditorTab, ExternalRefreshState> externalRefreshStates = new();
    private bool externalChangesInitialized, externalChangesClosed, externalRefreshRunning;
    private long externalRefreshGeneration;
    private EditorTab? externalPendingTab, externalRunningTab;
    private ExternalFileRefresh? externalPendingRead, externalRunningRead;
    private CancellationTokenSource? externalRefreshCancellation;

    /// <summary>
    /// Host: call once after InitializeComponent. SelectTab/TabChanged should call
    /// QueueExternalRefresh(CurrentTab); RefreshCurrent must not queue checks.
    /// </summary>
    public void InitializeExternalChanges()
    {
        Dispatcher.VerifyAccess();
        if (externalChangesInitialized || externalChangesClosed) return;
        externalChangesInitialized = true;
        externalRefreshTimer.Tick += ExternalRefreshTick;
        Activated += ExternalChangesActivated;
        Closed += ExternalChangesClosed;
    }

    private void ExternalChangesActivated(object? sender, EventArgs e) => QueueExternalRefresh(CurrentTab);

    private void ExternalChangesClosed(object? sender, EventArgs e)
    {
        externalChangesClosed = true;
        externalRefreshGeneration++;
        externalRefreshTimer.Stop();
        externalRefreshTimer.Tick -= ExternalRefreshTick;
        externalRefreshCancellation?.Cancel();
        externalPendingTab = null;
        externalPendingRead = null;
        Activated -= ExternalChangesActivated;
        Closed -= ExternalChangesClosed;
    }

    /// <summary>UI thread only. Coalesces notifications; performs no disk I/O.</summary>
    public void QueueExternalRefresh(EditorTab? tab)
    {
        Dispatcher.VerifyAccess();
        if (!externalChangesInitialized || externalChangesClosed || !initialized || approvedShutdown || exitInProgress) return;
        tab ??= CurrentTab;
        if (tab is null || tab != CurrentTab || !tabs.Contains(tab) || tab.File.Path is null)
        {
            // Switching to a draft or removing the selected tab invalidates old work.
            externalRefreshGeneration++;
            externalPendingTab = null;
            externalPendingRead = null;
            externalRefreshTimer.Stop();
            externalRefreshCancellation?.Cancel();
            return;
        }
        var state = externalRefreshStates.GetOrCreateValue(tab);
        if (state.LastRead?.IsCurrent == true && state.Notice is not null)
            StatusText.Text = state.Notice;
        if (externalPendingTab == tab && externalPendingRead?.IsCurrent == true) return;

        bool sameRunning = externalRefreshRunning && externalRunningTab == tab && externalRunningRead?.IsCurrent == true;
        if (!sameRunning)
        {
            externalRefreshGeneration++;
            externalRefreshCancellation?.Cancel();
        }
        // Keep one trailing check even for an activation arriving while a read is
        // in flight. Otherwise a second external write in that interval can be lost.
        externalPendingTab = tab;
        externalPendingRead = new ExternalFileRefresh(tab.File);
        ArmExternalRefresh(state);
    }

    private void ArmExternalRefresh(ExternalRefreshState state)
    {
        externalRefreshTimer.Stop();
        externalRefreshTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(ExternalRefreshDebounceMs,
            state.NextCheckAt - Environment.TickCount64));
        externalRefreshTimer.Start();
    }

    private async void ExternalRefreshTick(object? sender, EventArgs e)
    {
        externalRefreshTimer.Stop();
        if (externalRefreshRunning || externalChangesClosed || approvedShutdown || exitInProgress) return;
        var tab = externalPendingTab;
        if (tab is null || CurrentTab != tab || !tabs.Contains(tab)) return;
        if (modalDepth > 0)
        {
            externalRefreshTimer.Interval = TimeSpan.FromMilliseconds(250);
            externalRefreshTimer.Start();
            return;
        }
        // Capture again after the debounce: typing/saving may have changed the
        // document since the notification was queued.
        var read = new ExternalFileRefresh(tab.File);
        externalPendingTab = null;
        externalPendingRead = null;
        var state = externalRefreshStates.GetOrCreateValue(tab);
        state.NextCheckAt = Environment.TickCount64 + ExternalRefreshIntervalMs;
        long generation = externalRefreshGeneration;
        using var cancellation = new CancellationTokenSource();
        externalRefreshCancellation = cancellation;
        externalRunningTab = tab;
        externalRunningRead = read;
        externalRefreshRunning = true;
        try
        {
            var result = await read.ReadAsync(cancellation.Token);
            if (externalChangesClosed || approvedShutdown || exitInProgress || cancellation.IsCancellationRequested
                || generation != externalRefreshGeneration || CurrentTab != tab || !tabs.Contains(tab)) return;
            if (!read.IsCurrent || modalDepth > 0)
            {
                QueueExternalRefresh(tab);
                return;
            }
            state.LastRead = read;
            state.Notice = result.Status switch
            {
                ExternalRefreshStatus.Conflict => "文件已被外部修改或删除，已保留当前编辑；保存时将检查冲突，可另存为。",
                ExternalRefreshStatus.Unavailable => "外部文件暂时无法读取，已保留当前正文；下次切换或激活将重试。"
                    + (result.Error is null ? "" : " " + result.Error),
                _ => null
            };
            if (result.Status == ExternalRefreshStatus.Changed)
            {
                ApplyExternalRefresh(tab, result);
                // Applying advances the document version; keep the notice associated
                // with the new baseline instead of retaining an obsolete request.
                state.LastRead = new ExternalFileRefresh(tab.File);
            }
            else if (state.Notice is not null) StatusText.Text = state.Notice;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            if (!externalChangesClosed && CurrentTab == tab && generation == externalRefreshGeneration)
                StatusText.Text = "外部更新未完成，已保留当前正文；下次切换或激活将重试。 " + ex.Message;
        }
        finally
        {
            externalRefreshRunning = false;
            externalRunningTab = null;
            externalRunningRead = null;
            if (ReferenceEquals(externalRefreshCancellation, cancellation)) externalRefreshCancellation = null;
            if (!externalChangesClosed && externalPendingTab is { } pending && CurrentTab == pending)
                ArmExternalRefresh(externalRefreshStates.GetOrCreateValue(pending));
        }
    }

    private void ApplyExternalRefresh(EditorTab tab, ExternalRefreshResult result)
    {
        bool reading = tab.IsReading;
        int anchor = CurrentSourceAnchor(tab);
        int selectionStart = SourceOffset(tab, tab.Editor.SelectionStart);
        int selectionEnd = SourceOffset(tab, tab.Editor.SelectionStart + tab.Editor.SelectionLength);
        int readingStart = anchor, readingEnd = anchor;
        int readingDisplayStart = 0, readingDisplayEnd = 0;
        if (reading && tab.Rendered is not null && tab.Reading is not null)
        {
            readingDisplayStart = tab.Rendered.OffsetAt(tab.Preview.Selection.Start);
            readingDisplayEnd = tab.Rendered.OffsetAt(tab.Preview.Selection.End);
            readingStart = tab.Reading.DisplayToSource(readingDisplayStart);
            readingEnd = tab.Reading.DisplayToSource(readingDisplayEnd);
        }
        double vertical = tab.Editor.VerticalOffset, horizontal = tab.Editor.HorizontalOffset;
        double readingVertical = tab.Preview.VerticalOffset, readingHorizontal = tab.Preview.HorizontalOffset;
        var markdown = markdownTab == tab && !reading ? markdownPreview : null;
        double markdownVertical = markdown?.VerticalOffset ?? 0;
        int markdownStart = markdown?.Document.ContentStart.GetOffsetToPosition(markdown.Selection.Start) ?? 0;
        int markdownEnd = markdown?.Document.ContentStart.GetOffsetToPosition(markdown.Selection.End) ?? 0;
        if (!result.TryApply()) return;

        // The existing TextChanged path invalidates models and refreshes automatic
        // kind. Suppress only its reading rebuild, then rebuild exactly once below.
        tab.IsReading = false;
        tab.Reading = null;
        tab.Rendered = null;
        bool undoEnabled = tab.Editor.IsUndoEnabled;
        tab.Editor.IsUndoEnabled = false;
        try { tab.Editor.Text = tab.File.Text; }
        finally { tab.Editor.IsUndoEnabled = undoEnabled; }
        tab.TrackSavedBaseline();
        tab.NotifyChanged();
        tab.SourceAnchor = Math.Clamp(anchor, 0, tab.File.Text.Length);
        int start = EditorOffset(tab, selectionStart), end = EditorOffset(tab, selectionEnd);
        tab.Editor.Select(start, Math.Max(0, end - start));
        if (reading)
        {
            EnterReading(tab, tab.SourceAnchor, focus: false);
            if (tab.Rendered is not null && tab.Reading is not null)
                tab.Preview.Selection.Select(tab.Rendered.PointerAt(ExternalReadingDisplayOffset(tab.Reading,
                        Math.Clamp(readingStart, 0, tab.File.Text.Length), readingDisplayStart)),
                    tab.Rendered.PointerAt(ExternalReadingDisplayOffset(tab.Reading,
                        Math.Clamp(readingEnd, 0, tab.File.Text.Length), readingDisplayEnd)));
        }
        else tab.Editor.IsReadOnly = false;
        RefreshCurrent();
        if (markdown is not null && ReferenceEquals(markdown, markdownPreview) && markdownTab == tab)
        {
            markdownTimer.Stop();
            UpdateMarkdownPreview(tab);
            RestoreExternalMarkdownSelection(markdown, markdownStart, markdownEnd);
        }

        var accepted = new ExternalFileRefresh(tab.File);
        var rendered = tab.Rendered;
        int restoredReadingStart = rendered?.OffsetAt(tab.Preview.Selection.Start) ?? 0;
        int restoredReadingEnd = rendered?.OffsetAt(tab.Preview.Selection.End) ?? 0;
        // Run after EnterReading's Loaded/BringIntoView callback and text layout.
        // A later edit/save, mode change, tab switch or selection change wins.
        Dispatcher.BeginInvoke(() =>
        {
            if (externalChangesClosed || approvedShutdown || exitInProgress || CurrentTab != tab
                || !tabs.Contains(tab) || !accepted.IsCurrent || tab.IsReading != reading
                || !ReferenceEquals(tab.Rendered, rendered)
                || tab.Editor.SelectionStart != start || tab.Editor.SelectionLength != Math.Max(0, end - start)) return;
            if (reading && tab.Rendered is not null
                && (tab.Rendered.OffsetAt(tab.Preview.Selection.Start) != restoredReadingStart
                    || tab.Rendered.OffsetAt(tab.Preview.Selection.End) != restoredReadingEnd)) return;
            tab.Editor.ScrollToHorizontalOffset(horizontal);
            tab.Editor.ScrollToVerticalOffset(vertical);
            if (reading && tab.Rendered is not null)
            {
                tab.Preview.ScrollToHorizontalOffset(readingHorizontal);
                tab.Preview.ScrollToVerticalOffset(readingVertical);
            }
            if (markdown is not null && ReferenceEquals(markdown, markdownPreview) && markdownTab == tab)
                markdown.ScrollToVerticalOffset(markdownVertical);
            tab.UpdateLineNumbers();
        }, DispatcherPriority.ContextIdle);
    }

    private static void RestoreExternalMarkdownSelection(MarkdownPreview preview, int start, int end)
    {
        TextPointer beginning = preview.Document.ContentStart;
        int length = beginning.GetOffsetToPosition(preview.Document.ContentEnd);
        var first = beginning.GetPositionAtOffset(Math.Clamp(start, 0, length)) ?? beginning;
        var last = beginning.GetPositionAtOffset(Math.Clamp(end, 0, length)) ?? first;
        preview.Selection.Select(first, last);
    }

    private static int ExternalReadingDisplayOffset(ReadingModel model, int sourceOffset, int previousDisplayOffset)
    {
        // Block mappings interpolate with integer division in both directions.
        // Avoid the one-character drift caused by composing those rounded maps.
        int previous = Math.Clamp(previousDisplayOffset, 0, model.VisibleText.Length);
        if (model.DisplayToSource(previous) == sourceOffset) return previous;
        int low = 0, high = model.VisibleText.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (model.DisplayToSource(middle) < sourceOffset) low = middle + 1;
            else high = middle;
        }
        if (low > 0 && sourceOffset - model.DisplayToSource(low - 1) < model.DisplayToSource(low) - sourceOffset)
            low--;
        return low;
    }

    private sealed class ExternalRefreshState
    {
        public long NextCheckAt;
        public ExternalFileRefresh? LastRead;
        public string? Notice;
    }
}
