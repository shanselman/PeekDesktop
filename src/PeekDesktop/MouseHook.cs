using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PeekDesktop;

/// <summary>
/// Installs a low-level mouse hook (WH_MOUSE_LL) and raises an event
/// when the user clicks on the desktop surface.
/// Must be installed on a thread with a message loop.
/// </summary>
public sealed class MouseHook : IDisposable
{
    private IntPtr _hookId = IntPtr.Zero;

    // Must be stored as a field to prevent GC collection while the hook is active.
    private NativeMethods.LowLevelMouseProc? _hookProc;
    private SynchronizationContext? _syncContext;

    // Double-click detection state (low-level hooks never see WM_LBUTTONDBLCLK)
    private long _lastClickTick;
    private NativeMethods.POINT _lastClickPoint;

    // Pending-click state: we defer firing the classification event until WM_LBUTTONUP
    // so we can suppress the peek when the user is actually drag-selecting icons
    // on the desktop (e.g. marquee multi-select). See issue #35.
    private bool _hasPendingClick;
    private NativeMethods.POINT _pendingDownPoint;

    /// <summary>
    /// When true, only double-clicks trigger desktop peek (single clicks are ignored).
    /// </summary>
    public bool RequireDoubleClick { get; set; }

    /// <summary>
    /// Raised (on the UI thread) when a left-click on empty desktop wallpaper is detected.
    /// </summary>
    public event EventHandler? DesktopClicked;

    /// <summary>
    /// Raised (on the UI thread) when a left-click lands on a desktop icon.
    /// </summary>
    public event EventHandler? DesktopIconClicked;

    /// <summary>
    /// Raised (on the UI thread) when a left-click lands on something other than the desktop.
    /// </summary>
    public event EventHandler? NonDesktopClicked;

    public void Install()
    {
        if (_hookId != IntPtr.Zero)
            return;

        _syncContext = SynchronizationContext.Current;
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
        }
    }

    /// <summary>
    /// Hook callback — must return FAST to avoid Windows unhooking us.
    /// It captures the click point and posts the heavier classification
    /// work to the UI thread.
    /// </summary>
    private unsafe IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int message = wParam.ToInt32();

            if (message == NativeMethods.WM_LBUTTONDOWN)
            {
                var hookStruct = *(NativeMethods.MSLLHOOKSTRUCT*)lParam;
                var clickPoint = hookStruct.pt;

                if (RequireDoubleClick)
                {
                    long now = Environment.TickCount64;
                    uint doubleClickTime = NativeMethods.GetDoubleClickTime();
                    int cxThreshold = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXDOUBLECLK) / 2;
                    int cyThreshold = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYDOUBLECLK) / 2;

                    bool withinTime = (now - _lastClickTick) <= doubleClickTime;
                    bool withinDistance = Math.Abs(clickPoint.x - _lastClickPoint.x) <= cxThreshold
                                      && Math.Abs(clickPoint.y - _lastClickPoint.y) <= cyThreshold;

                    _lastClickTick = now;
                    _lastClickPoint = clickPoint;

                    if (!(withinTime && withinDistance))
                    {
                        // First click of a potential double-click — don't arm the pending trigger yet
                        _hasPendingClick = false;
                        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
                    }

                    // Reset so a third click doesn't also fire
                    _lastClickTick = 0;
                }

                // Arm a pending click; the actual classification + event will fire
                // on WM_LBUTTONUP, unless the user drags beyond the drag threshold
                // (which indicates a marquee / drag-select gesture).
                _hasPendingClick = true;
                _pendingDownPoint = clickPoint;
            }
            else if (message == NativeMethods.WM_MOUSEMOVE)
            {
                if (_hasPendingClick)
                {
                    var hookStruct = *(NativeMethods.MSLLHOOKSTRUCT*)lParam;
                    if (HasExceededDragThreshold(_pendingDownPoint, hookStruct.pt))
                    {
                        _hasPendingClick = false;
                        AppDiagnostics.Log("Pending peek click cancelled (drag detected)");
                    }
                }
            }
            else if (message == NativeMethods.WM_LBUTTONUP)
            {
                if (_hasPendingClick)
                {
                    _hasPendingClick = false;
                    var hookStruct = *(NativeMethods.MSLLHOOKSTRUCT*)lParam;
                    var upPoint = hookStruct.pt;

                    if (HasExceededDragThreshold(_pendingDownPoint, upPoint))
                    {
                        AppDiagnostics.Log("Pending peek click cancelled on mouse-up (drag detected)");
                    }
                    else
                    {
                        // Classify based on the original press location so a tiny
                        // cursor jitter during the click doesn't change the target.
                        var classifyPoint = _pendingDownPoint;
                        IntPtr windowUnderCursor = NativeMethods.WindowFromPoint(classifyPoint);

                        if (_syncContext is not null)
                            _syncContext.Post(_ => HandleMouseClick(windowUnderCursor, classifyPoint), null);
                        else
                            HandleMouseClick(windowUnderCursor, classifyPoint);
                    }
                }
            }
        }

        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private static bool HasExceededDragThreshold(NativeMethods.POINT from, NativeMethods.POINT to)
    {
        int cxDrag = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXDRAG);
        int cyDrag = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYDRAG);
        return Math.Abs(to.x - from.x) > cxDrag
            || Math.Abs(to.y - from.y) > cyDrag;
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
        DesktopClickTarget clickTarget = DesktopDetector.GetClickTarget(windowUnderCursor, clickPoint);
        AppDiagnostics.Log($"Mouse click classification: {clickTarget}");

        switch (clickTarget)
        {
            case DesktopClickTarget.DesktopBackground:
                DesktopClicked?.Invoke(this, EventArgs.Empty);
                break;

            case DesktopClickTarget.DesktopIcon:
                DesktopIconClicked?.Invoke(this, EventArgs.Empty);
                break;

            default:
                NonDesktopClicked?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    public void Dispose()
    {
        Uninstall();
    }
}
