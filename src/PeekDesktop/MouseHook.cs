using System;

namespace PeekDesktop;

internal readonly record struct PendingMouseClick(IntPtr Window, NativeMethods.POINT Point);

internal sealed class MouseClickTracker
{
    private bool _hasPreviousClick;
    private long _previousClickTick;
    private IntPtr _previousClickWindow;
    private NativeMethods.POINT _previousClickPoint;
    private bool _hasPendingClick;
    private PendingMouseClick _pendingClick;

    internal bool HasPendingClick => _hasPendingClick;

    internal bool TryBeginClick(
        IntPtr window,
        NativeMethods.POINT point,
        bool requireDoubleClick,
        long tick,
        uint doubleClickTime,
        int cxDoubleClick,
        int cyDoubleClick)
    {
        if (requireDoubleClick)
        {
            bool isDoubleClick = _hasPreviousClick
                && window == _previousClickWindow
                && (tick - _previousClickTick) <= doubleClickTime
                && Math.Abs(point.x - _previousClickPoint.x) <= cxDoubleClick
                && Math.Abs(point.y - _previousClickPoint.y) <= cyDoubleClick;

            if (!isDoubleClick)
            {
                _hasPreviousClick = true;
                _previousClickTick = tick;
                _previousClickWindow = window;
                _previousClickPoint = point;
                _hasPendingClick = false;
                return false;
            }

            _hasPreviousClick = false;
        }
        else
        {
            _hasPreviousClick = false;
        }

        _pendingClick = new PendingMouseClick(window, point);
        _hasPendingClick = true;
        return true;
    }

    internal bool CancelIfMoved(NativeMethods.POINT point, int cxDrag, int cyDrag)
    {
        if (!_hasPendingClick
            || (Math.Abs(point.x - _pendingClick.Point.x) <= cxDrag
                && Math.Abs(point.y - _pendingClick.Point.y) <= cyDrag))
        {
            return false;
        }

        _hasPendingClick = false;
        return true;
    }

    internal bool TryCompleteClick(
        NativeMethods.POINT point,
        int cxDrag,
        int cyDrag,
        out PendingMouseClick click)
    {
        click = _pendingClick;
        if (!_hasPendingClick)
            return false;

        _hasPendingClick = false;
        return Math.Abs(point.x - click.Point.x) <= cxDrag
            && Math.Abs(point.y - click.Point.y) <= cyDrag;
    }

    internal void Reset()
    {
        _hasPreviousClick = false;
        _hasPendingClick = false;
    }
}

/// <summary>
/// Installs a low-level mouse hook (WH_MOUSE_LL) and raises an event
/// when the user clicks on the desktop surface.
/// Must be installed on a thread with a message loop.
/// </summary>
public sealed class MouseHook : IDisposable
{
    private readonly Action<Action> _beginInvoke;
    private readonly MouseClickTracker _clickTracker = new();
    private IntPtr _hookId = IntPtr.Zero;

    // Must be stored as a field to prevent GC collection while the hook is active.
    private NativeMethods.LowLevelMouseProc? _hookProc;
    private bool _requireDoubleClick;
    private bool _monitorDesktopClicks;
    private bool _monitorTaskbarClicks;

    /// <summary>
    /// When true, only double-clicks trigger desktop peek (single clicks are ignored).
    /// </summary>
    public bool RequireDoubleClick
    {
        get => _requireDoubleClick;
        set
        {
            if (_requireDoubleClick == value)
                return;

            _requireDoubleClick = value;
            _clickTracker.Reset();
        }
    }

    internal bool MonitorDesktopClicks
    {
        get => _monitorDesktopClicks;
        set
        {
            if (_monitorDesktopClicks == value)
                return;

            _monitorDesktopClicks = value;
            _clickTracker.Reset();
        }
    }

    internal bool MonitorTaskbarClicks
    {
        get => _monitorTaskbarClicks;
        set
        {
            if (_monitorTaskbarClicks == value)
                return;

            _monitorTaskbarClicks = value;
            _clickTracker.Reset();
        }
    }

    /// <summary>
    /// Raised (on the UI thread) when a left-click on empty desktop wallpaper is detected.
    /// </summary>
    public event EventHandler? DesktopClicked;

    /// <summary>
    /// Raised (on the UI thread) when a left-click lands on a desktop icon.
    /// </summary>
    public event EventHandler? DesktopIconClicked;

    /// <summary>
    /// Raised (on the UI thread) when a left-click lands on empty taskbar space.
    /// </summary>
    public event EventHandler? TaskbarClicked;

    public MouseHook(Action<Action> beginInvoke)
    {
        ArgumentNullException.ThrowIfNull(beginInvoke);
        _beginInvoke = beginInvoke;
    }

    public void Install()
    {
        if (_hookId != IntPtr.Zero)
            return;

        _hookProc = HookCallback;
        _hookId = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_MOUSE_LL,
            _hookProc,
            NativeMethods.GetModuleHandle(null),
            0);
        AppDiagnostics.Log($"Mouse hook installed: 0x{_hookId.ToInt64():X}");
    }

    public void Uninstall()
    {
        if (_hookId != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hookId);
            AppDiagnostics.Log($"Mouse hook uninstalled: 0x{_hookId.ToInt64():X}");
            _hookId = IntPtr.Zero;
            _clickTracker.Reset();
        }
    }

    /// <summary>
    /// Hook callback - must return fast to avoid Windows unhooking us.
    /// It only tracks clicks on configured Explorer surfaces and posts
    /// heavier classification work to the application's message loop.
    /// </summary>
    private unsafe IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int message = wParam.ToInt32();

            if (message == NativeMethods.WM_LBUTTONDOWN)
            {
                var hookStruct = *(NativeMethods.MSLLHOOKSTRUCT*)lParam;
                NativeMethods.POINT clickPoint = hookStruct.pt;
                IntPtr windowUnderCursor = NativeMethods.WindowFromPoint(clickPoint);

                if (!DesktopDetector.IsPotentialPeekSurfaceWindow(
                    windowUnderCursor,
                    clickPoint,
                    MonitorDesktopClicks,
                    MonitorTaskbarClicks))
                {
                    _clickTracker.Reset();
                    return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
                }

                _clickTracker.TryBeginClick(
                    windowUnderCursor,
                    clickPoint,
                    RequireDoubleClick,
                    Environment.TickCount64,
                    NativeMethods.GetDoubleClickTime(),
                    NativeMethods.GetSystemMetrics(NativeMethods.SM_CXDOUBLECLK) / 2,
                    NativeMethods.GetSystemMetrics(NativeMethods.SM_CYDOUBLECLK) / 2);
            }
            else if (message == NativeMethods.WM_MOUSEMOVE && _clickTracker.HasPendingClick)
            {
                var hookStruct = *(NativeMethods.MSLLHOOKSTRUCT*)lParam;
                if (_clickTracker.CancelIfMoved(
                    hookStruct.pt,
                    NativeMethods.GetSystemMetrics(NativeMethods.SM_CXDRAG),
                    NativeMethods.GetSystemMetrics(NativeMethods.SM_CYDRAG)))
                {
                    AppDiagnostics.Log("Pending peek click cancelled (drag detected)");
                }
            }
            else if (message == NativeMethods.WM_LBUTTONUP && _clickTracker.HasPendingClick)
            {
                var hookStruct = *(NativeMethods.MSLLHOOKSTRUCT*)lParam;
                if (!_clickTracker.TryCompleteClick(
                    hookStruct.pt,
                    NativeMethods.GetSystemMetrics(NativeMethods.SM_CXDRAG),
                    NativeMethods.GetSystemMetrics(NativeMethods.SM_CYDRAG),
                    out PendingMouseClick click))
                {
                    AppDiagnostics.Log("Pending peek click cancelled on mouse-up (drag detected)");
                }
                else
                {
                    DispatchMouseClick(click.Window, click.Point);
                }
            }
        }

        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    internal void DispatchMouseClick(IntPtr windowUnderCursor, NativeMethods.POINT clickPoint)
    {
        _beginInvoke(() => HandleMouseClick(windowUnderCursor, clickPoint));
    }

    private void HandleMouseClick(IntPtr windowUnderCursor, NativeMethods.POINT clickPoint)
    {
        var monitorInfo = new NativeMethods.MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        IntPtr hMonitor = NativeMethods.MonitorFromPoint(clickPoint, NativeMethods.MONITOR_DEFAULTTONEAREST);
        NativeMethods.GetMonitorInfoW(hMonitor, ref monitorInfo);
        AppDiagnostics.Log($"Mouse click monitor: work={monitorInfo.rcWork.Left},{monitorInfo.rcWork.Top},{monitorInfo.rcWork.Right},{monitorInfo.rcWork.Bottom}");
        AppDiagnostics.Log($"Mouse click point: {NativeMethods.DescribePoint(clickPoint)}");
        AppDiagnostics.Log($"Mouse click target: {NativeMethods.DescribeWindow(windowUnderCursor)}");
        AppDiagnostics.Log($"Mouse click hierarchy: {NativeMethods.DescribeWindowHierarchy(windowUnderCursor)}");
        DesktopClickTarget clickTarget = DesktopDetector.GetClickTarget(
            windowUnderCursor,
            clickPoint,
            MonitorDesktopClicks,
            MonitorTaskbarClicks);
        AppDiagnostics.Log($"Mouse click classification: {clickTarget}");

        switch (clickTarget)
        {
            case DesktopClickTarget.DesktopBackground:
                DesktopClicked?.Invoke(this, EventArgs.Empty);
                break;

            case DesktopClickTarget.DesktopIcon:
                DesktopIconClicked?.Invoke(this, EventArgs.Empty);
                break;

            case DesktopClickTarget.TaskbarBackground:
                TaskbarClicked?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    public void Dispose()
    {
        Uninstall();
    }
}
