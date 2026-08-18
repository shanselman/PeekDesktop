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
    private const int OffscreenMargin = 64;

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
    public bool FlyAwayAll()
    {
        var stopwatch = Stopwatch.StartNew();
        var recoverableWindows = new List<WindowInfo>(_savedWindows.Count);
        var recoverySnapshot = new List<FlyAwayRecoveryWindow>(_savedWindows.Count);

        foreach (var window in _savedWindows)
        {
            if (!NativeMethods.IsWindow(window.Handle))
                continue;

            _ = NativeMethods.GetWindowThreadProcessId(window.Handle, out uint processId);
            if (processId == 0)
                continue;

            recoverableWindows.Add(window);
            recoverySnapshot.Add(new FlyAwayRecoveryWindow(
                window.Handle.ToInt64(),
                processId,
                window.Placement,
                window.Bounds));
        }

        if (recoverySnapshot.Count == 0)
        {
            AppDiagnostics.Log("Fly Away skipped because no recoverable windows remained");
            return false;
        }

        if (!FlyAwayRecovery.TryWriteSnapshot(recoverySnapshot))
        {
            AppDiagnostics.Log("Fly Away aborted because its recovery snapshot could not be persisted");
            return false;
        }

        var animationWindows = new List<AnimatedWindow>(recoverableWindows.Count);
        foreach (var window in recoverableWindows)
        {
            var workingPlacement = window.Placement;
            workingPlacement.length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>();

            if (workingPlacement.showCmd == NativeMethods.SW_MAXIMIZE)
            {
                workingPlacement.showCmd = NativeMethods.SW_SHOWNORMAL;
                NativeMethods.SetWindowPlacement(window.Handle, ref workingPlacement);
            }

            NativeMethods.ShowWindow(window.Handle, NativeMethods.SW_SHOWNOACTIVATE);

            NativeMethods.RECT startBounds = GetCurrentBounds(window);
            NativeMethods.RECT targetBounds = ComputeFlyAwayTarget(startBounds);
            animationWindows.Add(new AnimatedWindow(window.Handle, startBounds, targetBounds));
        }

        AnimateWindows(animationWindows);
        AppDiagnostics.Metric($"FlyAwayAll: {animationWindows.Count} window(s) in {stopwatch.ElapsedMilliseconds}ms");
        return animationWindows.Count > 0;
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
        if (peekMode == PeekMode.FlyAway)
            FlyAwayRecovery.ClearSnapshot();

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

    private static NativeMethods.RECT ComputeFlyAwayTarget(NativeMethods.RECT startBounds)
    {
        var monitorInfo = new NativeMethods.MONITORINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        IntPtr hMonitor = NativeMethods.MonitorFromRect(ref startBounds, NativeMethods.MONITOR_DEFAULTTONEAREST);
        NativeMethods.GetMonitorInfoW(hMonitor, ref monitorInfo);
        NativeMethods.RECT screenBounds = monitorInfo.rcWork;

        int width = Math.Max(1, startBounds.Right - startBounds.Left);
        int height = Math.Max(1, startBounds.Bottom - startBounds.Top);
        int centerX = startBounds.Left + (width / 2);
        int centerY = startBounds.Top + (height / 2);

        int screenWidth = screenBounds.Right - screenBounds.Left;
        int screenHeight = screenBounds.Bottom - screenBounds.Top;

        bool moveLeft = centerX < screenBounds.Left + (screenWidth / 2);
        bool moveUp = centerY < screenBounds.Top + (screenHeight / 2);

        int targetLeft = moveLeft
            ? screenBounds.Left - width - OffscreenMargin
            : screenBounds.Right + OffscreenMargin;

        int targetTop = moveUp
            ? screenBounds.Top - height - OffscreenMargin
            : screenBounds.Bottom + OffscreenMargin;

        return new NativeMethods.RECT
        {
            Left = targetLeft,
            Top = targetTop,
            Right = targetLeft + width,
            Bottom = targetTop + height
        };
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
        return (int)Math.Round(from + ((to - from) * t));
    }

    private record WindowInfo(IntPtr Handle, NativeMethods.WINDOWPLACEMENT Placement, NativeMethods.RECT Bounds);
    private record AnimatedWindow(IntPtr Handle, NativeMethods.RECT StartBounds, NativeMethods.RECT EndBounds);
}
