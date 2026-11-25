using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace Qingyue.Windows;

/// <summary>
/// Starts the document's image work once, in source order, without depending on
/// viewport realization. This mirrors ReadingMedia.swift: the first 32 controls
/// share one five-second deadline and unfinished controls remain retryable.
/// </summary>
internal static class MarkdownImageScheduler
{
    internal const int MaximumImages = 32;
    internal static TimeSpan Deadline { get; set; } = TimeSpan.FromSeconds(5);

    private sealed class BatchState
    {
        internal readonly object Gate = new();
        internal readonly List<WeakReference<MarkdownImageView>> Views = [];
        internal readonly HashSet<MarkdownImageView> Attempted = [];
        internal bool Activated;
        internal bool Scheduled;
        internal bool Running;
    }

    private static readonly ConditionalWeakTable<MarkdownImageBudget, BatchState> Batches = new();
    private static readonly SemaphoreSlim RetryGate = new(1, 1);

    internal static void Register(MarkdownImageView view, MarkdownImageBudget budget)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(budget);
        var state = Batches.GetValue(budget, _ => new BatchState());
        lock (state.Gate)
        {
            state.Views.RemoveAll(reference => !reference.TryGetTarget(out _));
            state.Views.Add(new WeakReference<MarkdownImageView>(view));
        }
        if (state.Activated) Schedule(state, view.Dispatcher);
    }

    internal static void Activate(MarkdownImageView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (!Batches.TryGetValue(view.Budget, out var state)) return;
        state.Activated = true;
        Schedule(state, view.Dispatcher);
    }

    internal static void Unregister(MarkdownImageView view, MarkdownImageBudget budget)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(budget);
        if (!Batches.TryGetValue(budget, out var state)) return;
        lock (state.Gate)
        {
            state.Views.RemoveAll(reference => !reference.TryGetTarget(out var target) ||
                ReferenceEquals(target, view));
            state.Attempted.Remove(view);
        }
    }

    internal static void Retry(MarkdownImageView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        _ = RunRetryAsync(view);
    }

    private static void Schedule(BatchState state, Dispatcher dispatcher)
    {
        lock (state.Gate)
        {
            if (state.Scheduled || state.Running) return;
            state.Scheduled = true;
        }
        // Background is after layout/load but still cooperates with the WPF test
        // harness and with modal dispatcher frames used during document transfer.
        dispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(() => _ = RunBatchAsync(state)));
    }

    private static async Task RunBatchAsync(BatchState state)
    {
        MarkdownImageView[] views;
        lock (state.Gate)
        {
            state.Scheduled = false;
            if (state.Running) return;
            state.Running = true;
            state.Views.RemoveAll(reference => !reference.TryGetTarget(out _));
            views = state.Views
                .Select(reference => reference.TryGetTarget(out var view) ? view : null)
                .Where(view => view is not null)
                .Cast<MarkdownImageView>()
                .OrderBy(view => view.SourceStart)
                .ThenBy(view => view.Sequence)
                .Take(MaximumImages)
                .ToArray();
        }

        try
        {
            using var deadline = new CancellationTokenSource(Deadline);
            foreach (var view in views)
            {
                lock (state.Gate)
                {
                    if (view.IsReady || view.IsFailed || !state.Attempted.Add(view)) continue;
                }
                if (deadline.IsCancellationRequested)
                {
                    view.MarkTimedOut();
                    continue;
                }
                await view.StartLoadingAsync(deadline.Token);
            }
        }
        finally
        {
            bool scheduleAgain;
            lock (state.Gate)
            {
                state.Running = false;
                scheduleAgain = state.Activated && state.Views.Any(reference =>
                    reference.TryGetTarget(out var view) && !state.Attempted.Contains(view) &&
                    !view.IsReady && !view.IsFailed);
            }
            if (scheduleAgain) Schedule(state, views.FirstOrDefault()?.Dispatcher ??
                Dispatcher.CurrentDispatcher);
        }
    }

    private static async Task RunRetryAsync(MarkdownImageView view)
    {
        await RetryGate.WaitAsync();
        try
        {
            using var deadline = new CancellationTokenSource(Deadline);
            await view.StartLoadingAsync(deadline.Token);
        }
        finally
        {
            RetryGate.Release();
        }
    }
}
