using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Qingyue.Windows;

internal static class MarkdownImageScheduler
{
    private sealed class HostState
    {
        internal readonly RichTextBox Host;
        internal readonly List<WeakReference<MarkdownImageView>> Views = [];
        internal readonly ScrollChangedEventHandler ScrollChanged;
        internal readonly SizeChangedEventHandler SizeChanged;

        internal HostState(RichTextBox host)
        {
            Host = host;
            ScrollChanged = (_, _) => Refresh(this);
            SizeChanged = (_, _) => Refresh(this);
            host.AddHandler(ScrollViewer.ScrollChangedEvent, ScrollChanged);
            host.SizeChanged += SizeChanged;
        }

        internal void Dispose()
        {
            Host.RemoveHandler(ScrollViewer.ScrollChangedEvent, ScrollChanged);
            Host.SizeChanged -= SizeChanged;
        }
    }

    private static readonly ConditionalWeakTable<RichTextBox, HostState> Hosts = new();
    private static readonly object Gate = new();

    internal static void Register(MarkdownImageView view, RichTextBox? host)
    {
        if (host is null)
        {
            // A detached test/document host has no viewport to rank against. Preserve
            // the old eager behavior for that case; real windows always provide a host.
            view.StartLoading();
            return;
        }

        HostState state;
        lock (Gate)
        {
            if (!Hosts.TryGetValue(host, out state!))
            {
                state = new HostState(host);
                Hosts.Add(host, state);
            }
            state.Views.Add(new WeakReference<MarkdownImageView>(view));
        }
        Refresh(state);
    }

    internal static void Unregister(MarkdownImageView view, RichTextBox? host)
    {
        if (host is null) return;
        lock (Gate)
        {
            if (!Hosts.TryGetValue(host, out var state)) return;
            state.Views.RemoveAll(item => !item.TryGetTarget(out var candidate) ||
                ReferenceEquals(candidate, view));
            if (state.Views.Count == 0)
            {
                state.Dispose();
                Hosts.Remove(host);
            }
        }
    }

    private static void Refresh(HostState state)
    {
        if (!state.Host.Dispatcher.CheckAccess())
        {
            state.Host.Dispatcher.BeginInvoke(() => Refresh(state));
            return;
        }

        List<(MarkdownImageView View, double Distance)> candidates = [];
        lock (Gate)
        {
            state.Views.RemoveAll(item => !item.TryGetTarget(out _));
            foreach (var reference in state.Views)
            {
                if (!reference.TryGetTarget(out var view) || view.LoadingStarted) continue;
                if (TryDistance(view, state.Host, out double distance))
                    candidates.Add((view, distance));
                else if (candidates.Count < 8)
                    candidates.Add((view, double.MaxValue));
            }
        }

        double viewport = state.Host.ActualHeight > 0 ? state.Host.ActualHeight : 900;
        foreach (var candidate in candidates
            .OrderBy(item => item.Distance)
            .Take(candidates.Any(item => item.Distance < double.MaxValue) ? 16 : 8))
        {
            if (candidate.Distance == double.MaxValue ||
                candidate.Distance <= viewport * 1.5)
                candidate.View.StartLoading();
        }
    }

    private static bool TryDistance(MarkdownImageView view, RichTextBox host, out double distance)
    {
        try
        {
            if (!view.IsLoaded || !host.IsLoaded)
            {
                distance = 0;
                return false;
            }
            Rect bounds = view.TransformToAncestor(host).TransformBounds(
                new Rect(new Size(Math.Max(1, view.ActualWidth), Math.Max(1, view.ActualHeight))));
            double viewport = host.ActualHeight > 0 ? host.ActualHeight : 900;
            distance = bounds.Bottom < 0 ? -bounds.Bottom
                : bounds.Top > viewport ? bounds.Top - viewport
                : 0;
            return true;
        }
        catch (InvalidOperationException)
        {
            distance = 0;
            return false;
        }
    }
}
