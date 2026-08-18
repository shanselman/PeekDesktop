using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PeekDesktop;

/// <summary>
/// Captures the state of all visible top-level windows, minimizes them,
/// and restores them to their exact previous positions (including maximized state).
/// </summary>
public sealed class WindowTracker
{
    private const int AnimationSteps = 12;
    private const int AnimationDurationMs = 160;
    internal const int OffscreenMargin = 64;

    private readonly List<WindowInfo> _savedWindows = new();

    public bool HasWindows => _savedWindows.Count > 0;
    public int SavedWindowCount => _savedWindows.Count;

    /// <summary>
    /// Snapshot all visible, non-system top-level windows and their placements.
    /// </summary>
    public void CaptureWindows()
    {
        var stopwatch = Stopwatch.StartNew();
        _savedWindows.Clear();

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (ShouldTrackWindow(hwnd))
            {
                var placement = new NativeMethods.WINDOWPLACEMENT();
                placement.length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>();
                if (NativeMethods.GetWindowPlacement(hwnd, ref placement))
                {
                    if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT bounds))
                        bounds = placement.rcNormalPosition;

                    _savedWindows.Add(new WindowInfo(hwnd, placement, bounds));
                    AppDiagnostics.LogWindow("Captured window", hwnd);
                }
            }
            return true;
        }, IntPtr.Zero);

        AppDiagnostics.Log($"Capture complete: {_savedWindows.Count} window(s) saved");
        AppDiagnostics.Metric($"CaptureWindows: {_savedWindows.Count} window(s) in {stopwatch.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// Minimize every captured window.
    /// </summary>
    public void MinimizeAll()
    {
        var stopwatch = Stopwatch.StartNew();
        foreach (var window in _savedWindows)
        {
            AppDiagnostics.LogWindow("Minimizing window", window.Handle);
            NativeMethods.ShowWindow(window.Handle, NativeMethods.SW_MINIMIZE);
        }

        AppDiagnostics.Metric($"MinimizeAll: {_savedWindows.Count} window(s) in {stopwatch.ElapsedMilliseconds}ms");
    }

    public void ClearSavedWindows()
    {
        _savedWindows.Clear();
    }

    public IntPtr[] GetSavedWindowHandlesSnapshot()
    {
        var handles = new IntPtr[_savedWindows.Count];
        for (int i = 0; i < _savedWindows.Count; i++)
            handles[i] = _savedWindows[i].Handle;

        return handles;
    }

    public IntPtr[] RemoveSavedWindows(Predicate<IntPtr> shouldRemove)
    {
        var removedHandles = new List<IntPtr>();
        _savedWindows.RemoveAll(window =>
        {
            if (!shouldRemove(window.Handle))
                return false;

            removedHandles.Add(window.Handle);
            return true;
        });

        return removedHandles.ToArray();
    }

    /// <summary>
    /// Move captured windows toward the corner of the screen they are already closest to.
    /// This is an experiment to mimic macOS-style "show desktop" animation.
    /// </summary>
    public void FlyAwayAll()
    {
        var stopwatch = Stopwatch.StartNew();
        var animationWindows = new List<AnimatedWindow>(_savedWindows.Count);
        IReadOnlyList<NativeMethods.RECT> monitorBounds = GetMonitorBounds();
        if (monitorBounds.Count == 0)
        {
            AppDiagnostics.Log("Fly Away skipped because monitor geometry was unavailable");
            return;
        }

        foreach (var window in _savedWindows)
        {
            if (!NativeMethods.IsWindow(window.Handle))
                continue;

            var workingPlacement = window.Placement;
            workingPlacement.length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>();

            if (workingPlacement.showCmd == NativeMethods.SW_MAXIMIZE)
            {
                workingPlacement.showCmd = NativeMethods.SW_SHOWNORMAL;
                NativeMethods.SetWindowPlacement(window.Handle, ref workingPlacement);
            }

            NativeMethods.ShowWindow(window.Handle, NativeMethods.SW_SHOWNOACTIVATE);

            NativeMethods.RECT startBounds = GetCurrentBounds(window);
            NativeMethods.RECT targetBounds = ComputeFlyAwayTarget(startBounds, monitorBounds);
            animationWindows.Add(new AnimatedWindow(window.Handle, startBounds, targetBounds));
        }

        AnimateWindows(animationWindows);
        AppDiagnostics.Metric($"FlyAwayAll: {animationWindows.Count} window(s) in {stopwatch.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// Restore every captured window to its saved placement.
    /// Restores bottom-to-top to preserve Z-order, and does NOT steal focus.
    /// </summary>
    public void RestoreAll(PeekMode peekMode = PeekMode.Minimize)
    {
        var stopwatch = Stopwatch.StartNew();
        int restoredCount = 0;

        if (peekMode == PeekMode.FlyAway)
        {
            var animationWindows = new List<AnimatedWindow>(_savedWindows.Count);

            foreach (var info in _savedWindows)
            {
                if (!NativeMethods.IsWindow(info.Handle))
                    continue;

                NativeMethods.ShowWindow(info.Handle, NativeMethods.SW_SHOWNOACTIVATE);

                NativeMethods.RECT startBounds = GetCurrentBounds(info);
                NativeMethods.RECT endBounds = GetRestoreBounds(info);
                animationWindows.Add(new AnimatedWindow(info.Handle, startBounds, endBounds));
            }

            AnimateWindows(animationWindows);
        }

        // Restore in reverse order (bottom windows first) to preserve Z-order
        for (int i = _savedWindows.Count - 1; i >= 0; i--)
        {
            var info = _savedWindows[i];

            // Skip windows that were destroyed while we were peeking
            if (!NativeMethods.IsWindow(info.Handle))
            {
                AppDiagnostics.LogWindow("Skipping destroyed window", info.Handle);
                continue;
            }

            var placement = info.Placement;
            AppDiagnostics.LogWindow("Restoring window", info.Handle);
            NativeMethods.SetWindowPlacement(info.Handle, ref placement);
            restoredCount++;
        }

        _savedWindows.Clear();
        AppDiagnostics.Log("Restore list cleared");
        AppDiagnostics.Metric($"RestoreAll: {restoredCount} window(s) in {stopwatch.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// Determines whether a window should be captured for peek/restore.
    /// Filters out system chrome, invisible windows, tool windows, etc.
    /// </summary>
    private static bool ShouldTrackWindow(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindowVisible(hwnd))
            return false;

        if (NativeMethods.IsIconic(hwnd))
            return false;

        // Skip owned windows — they follow their owner
        if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero)
            return false;

        // Skip cloaked windows (other virtual desktops, hidden UWP apps)
        if (NativeMethods.IsWindowCloaked(hwnd))
            return false;

        string className = NativeMethods.GetWindowClassName(hwnd);
        if (string.IsNullOrEmpty(className))
            return false;

        // Skip shell and system windows
        if (IsExcludedClass(className))
            return false;

        // Skip tool windows (floating palettes, etc.)
        long exStyle = NativeMethods.GetWindowLongValue(hwnd, NativeMethods.GWL_EXSTYLE);
        if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0)
            return false;
        if ((exStyle & NativeMethods.WS_EX_NOACTIVATE) != 0)
            return false;

        return true;
    }

    private static bool IsExcludedClass(string className)
    {
        return className switch
        {
            "Progman" => true,
            "WorkerW" => true,
            "Shell_TrayWnd" => true,
            "Shell_SecondaryTrayWnd" => true,
            "NotifyIconOverflowWindow" => true,
            "DV2ControlHost" => true,            // Start menu (Win10)
            "Windows.UI.Core.CoreWindow" => true, // Start, Action Center
            _ => false
        };
    }

    private static NativeMethods.RECT GetCurrentBounds(WindowInfo window)
    {
        if (NativeMethods.GetWindowRect(window.Handle, out NativeMethods.RECT bounds))
            return bounds;

        return window.Bounds;
    }

    private static NativeMethods.RECT GetRestoreBounds(WindowInfo window)
    {
        if (window.Placement.showCmd == NativeMethods.SW_MAXIMIZE)
            return window.Placement.rcNormalPosition;

        return window.Bounds;
    }

    internal static NativeMethods.RECT ComputeFlyAwayTarget(
        NativeMethods.RECT startBounds,
        IReadOnlyList<NativeMethods.RECT> monitorBounds)
    {
        if (monitorBounds.Count == 0)
            return startBounds;

        int width = startBounds.Right - startBounds.Left;
        int height = startBounds.Bottom - startBounds.Top;
        if (width <= 0 || height <= 0 || IsOutsideAllMonitors(startBounds, monitorBounds))
            return startBounds;

        for (int pass = 0; pass < 2; pass++)
        {
            int clearance = pass == 0 ? OffscreenMargin : 0;
            NativeMethods.RECT bestTarget = startBounds;
            long bestDistance = long.MaxValue;

            foreach (NativeMethods.RECT monitor in monitorBounds)
            {
                if (RangesOverlap(
                        (long)startBounds.Top - clearance,
                        (long)startBounds.Bottom + clearance,
                        monitor.Top,
                        monitor.Bottom))
                {
                    int leftRight = ClampCoordinate((long)monitor.Left - clearance);
                    ConsiderTarget(
                        new NativeMethods.RECT
                        {
                            Left = ClampCoordinate((long)leftRight - width),
                            Top = startBounds.Top,
                            Right = leftRight,
                            Bottom = startBounds.Bottom
                        },
                        startBounds,
                        monitorBounds,
                        clearance,
                        ref bestTarget,
                        ref bestDistance);

                    int rightLeft = ClampCoordinate((long)monitor.Right + clearance);
                    ConsiderTarget(
                        new NativeMethods.RECT
                        {
                            Left = rightLeft,
                            Top = startBounds.Top,
                            Right = ClampCoordinate((long)rightLeft + width),
                            Bottom = startBounds.Bottom
                        },
                        startBounds,
                        monitorBounds,
                        clearance,
                        ref bestTarget,
                        ref bestDistance);
                }

                if (RangesOverlap(
                        (long)startBounds.Left - clearance,
                        (long)startBounds.Right + clearance,
                        monitor.Left,
                        monitor.Right))
                {
                    int topBottom = ClampCoordinate((long)monitor.Top - clearance);
                    ConsiderTarget(
                        new NativeMethods.RECT
                        {
                            Left = startBounds.Left,
                            Top = ClampCoordinate((long)topBottom - height),
                            Right = startBounds.Right,
                            Bottom = topBottom
                        },
                        startBounds,
                        monitorBounds,
                        clearance,
                        ref bestTarget,
                        ref bestDistance);

                    int bottomTop = ClampCoordinate((long)monitor.Bottom + clearance);
                    ConsiderTarget(
                        new NativeMethods.RECT
                        {
                            Left = startBounds.Left,
                            Top = bottomTop,
                            Right = startBounds.Right,
                            Bottom = ClampCoordinate((long)bottomTop + height)
                        },
                        startBounds,
                        monitorBounds,
                        clearance,
                        ref bestTarget,
                        ref bestDistance);
                }
            }

            if (bestDistance != long.MaxValue)
                return bestTarget;
        }

        return startBounds;
    }

    private static IReadOnlyList<NativeMethods.RECT> GetMonitorBounds()
    {
        var monitorBounds = new List<NativeMethods.RECT>();
        bool enumerated = NativeMethods.EnumDisplayMonitors(
            IntPtr.Zero,
            IntPtr.Zero,
            (IntPtr _, IntPtr _, ref NativeMethods.RECT monitorRect, IntPtr _) =>
            {
                if (monitorRect.Right > monitorRect.Left && monitorRect.Bottom > monitorRect.Top)
                    monitorBounds.Add(monitorRect);
                return true;
            },
            IntPtr.Zero);

        if (!enumerated)
        {
            AppDiagnostics.Log("EnumDisplayMonitors failed while computing Fly Away targets");
            monitorBounds.Clear();
        }

        return monitorBounds;
    }

    private static void ConsiderTarget(
        NativeMethods.RECT candidate,
        NativeMethods.RECT startBounds,
        IReadOnlyList<NativeMethods.RECT> monitorBounds,
        int clearance,
        ref NativeMethods.RECT bestTarget,
        ref long bestDistance)
    {
        long sourceWidth = (long)startBounds.Right - startBounds.Left;
        long sourceHeight = (long)startBounds.Bottom - startBounds.Top;
        if ((long)candidate.Right - candidate.Left != sourceWidth
            || (long)candidate.Bottom - candidate.Top != sourceHeight)
        {
            return;
        }

        if (!IsClearOfAllMonitors(candidate, monitorBounds, clearance))
            return;

        long distance = Math.Abs((long)candidate.Left - startBounds.Left)
            + Math.Abs((long)candidate.Top - startBounds.Top);
        if (distance < bestDistance)
        {
            bestTarget = candidate;
            bestDistance = distance;
        }
    }

    internal static bool IsOutsideAllMonitors(
        NativeMethods.RECT bounds,
        IReadOnlyList<NativeMethods.RECT> monitorBounds)
    {
        return IsClearOfAllMonitors(bounds, monitorBounds, 0);
    }

    internal static bool IsClearOfAllMonitors(
        NativeMethods.RECT bounds,
        IReadOnlyList<NativeMethods.RECT> monitorBounds,
        int clearance)
    {
        foreach (NativeMethods.RECT monitor in monitorBounds)
        {
            if (RangesOverlap(
                    (long)bounds.Left - clearance,
                    (long)bounds.Right + clearance,
                    monitor.Left,
                    monitor.Right)
                && RangesOverlap(
                    (long)bounds.Top - clearance,
                    (long)bounds.Bottom + clearance,
                    monitor.Top,
                    monitor.Bottom))
            {
                return false;
            }
        }

        return true;
    }

    private static bool RangesOverlap(long firstStart, long firstEnd, long secondStart, long secondEnd)
    {
        return firstStart < secondEnd && firstEnd > secondStart;
    }

    private static int ClampCoordinate(long value)
    {
        return (int)Math.Clamp(value, int.MinValue, int.MaxValue);
    }

    private static void AnimateWindows(IReadOnlyList<AnimatedWindow> windows)
    {
        if (windows.Count == 0)
            return;

        int sleepMs = Math.Max(1, AnimationDurationMs / AnimationSteps);
        const uint flags = NativeMethods.SWP_NOACTIVATE
                         | NativeMethods.SWP_NOZORDER
                         | NativeMethods.SWP_NOOWNERZORDER
                         | NativeMethods.SWP_NOSENDCHANGING
                         | NativeMethods.SWP_ASYNCWINDOWPOS;

        for (int step = 1; step <= AnimationSteps; step++)
        {
            double progress = EaseOutCubic(step / (double)AnimationSteps);

            foreach (var window in windows)
            {
                if (!NativeMethods.IsWindow(window.Handle))
                    continue;

                NativeMethods.RECT frame = LerpRect(window.StartBounds, window.EndBounds, progress);
                int width = Math.Max(1, frame.Right - frame.Left);
                int height = Math.Max(1, frame.Bottom - frame.Top);

                NativeMethods.SetWindowPos(
                    window.Handle,
                    IntPtr.Zero,
                    frame.Left,
                    frame.Top,
                    width,
                    height,
                    flags);
            }

            Thread.Sleep(sleepMs);
        }
    }

    private static double EaseOutCubic(double t)
    {
        double inverse = 1d - t;
        return 1d - (inverse * inverse * inverse);
    }

    private static NativeMethods.RECT LerpRect(NativeMethods.RECT from, NativeMethods.RECT to, double t)
    {
        return new NativeMethods.RECT
        {
            Left = Lerp(from.Left, to.Left, t),
            Top = Lerp(from.Top, to.Top, t),
            Right = Lerp(from.Right, to.Right, t),
            Bottom = Lerp(from.Bottom, to.Bottom, t)
        };
    }

    private static int Lerp(int from, int to, double t)
    {
        return (int)Math.Round(from + (((long)to - from) * t));
    }

    private record WindowInfo(IntPtr Handle, NativeMethods.WINDOWPLACEMENT Placement, NativeMethods.RECT Bounds);
    private record AnimatedWindow(IntPtr Handle, NativeMethods.RECT StartBounds, NativeMethods.RECT EndBounds);
}
