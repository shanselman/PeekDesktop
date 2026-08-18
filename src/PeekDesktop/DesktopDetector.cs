using System;

namespace PeekDesktop;

internal enum DesktopClickTarget
{
    NonDesktop,
    DesktopBackground,
    DesktopIcon,
    TaskbarBackground
}

/// <summary>
/// Identifies whether a window handle belongs to the Windows desktop surface
/// (Progman, WorkerW with SHELLDLL_DefView, or transient desktop UI like menus).
/// </summary>
public static class DesktopDetector
{
    /// <summary>
    /// Classifies a click as hitting the desktop wallpaper, a desktop icon,
    /// or something unrelated to the desktop.
    /// </summary>
    internal static DesktopClickTarget GetClickTarget(
        IntPtr hwnd,
        NativeMethods.POINT point,
        bool includeDesktop,
        bool includeTaskbar)
    {
        if (!IsPotentialPeekSurfaceWindow(hwnd, point, includeDesktop, includeTaskbar))
            return DesktopClickTarget.NonDesktop;

        if (includeTaskbar && IsTaskbarBlankAreaWindow(hwnd, point))
        {
            AppDiagnostics.Log("Taskbar blank-area click detected");
            return DesktopClickTarget.TaskbarBackground;
        }

        if (includeTaskbar && IsMonitorTaskbarAreaPoint(point))
        {
            AppDiagnostics.Log($"Taskbar-area click detected without taskbar hwnd at {NativeMethods.DescribePoint(point)}; deferring to UIA");
            if (ClassifyTaskbarOverlayPoint(point))
            {
                AppDiagnostics.Log("Taskbar blank-area click detected");
                return DesktopClickTarget.TaskbarBackground;
            }
        }

        if (!includeDesktop || !IsDesktopRelatedWindow(hwnd))
        {
            AppDiagnostics.Log($"Desktop relationship check failed at {NativeMethods.DescribePoint(point)}");
            AppDiagnostics.Log($"Desktop relationship reason: {GetDesktopRelationshipReason(hwnd)}");
            return DesktopClickTarget.NonDesktop;
        }

        if (IsDesktopIconWindow(hwnd))
        {
            if (NativeMethods.TryIsDesktopListViewItemAtPoint(hwnd, point, out bool isOnDesktopItem))
            {
                AppDiagnostics.Log($"Desktop list-view hit-test: {(isOnDesktopItem ? "icon" : "background")}");
                return isOnDesktopItem ? DesktopClickTarget.DesktopIcon : DesktopClickTarget.DesktopBackground;
            }

            if (NativeMethods.TryGetAccessibleDetailsAtPoint(point, out int role, out string name))
            {
                AppDiagnostics.Log($"Desktop accessibility hit-test: role=0x{role:X} name=\"{name}\"");
                if (role == NativeMethods.ROLE_SYSTEM_LISTITEM)
                    return DesktopClickTarget.DesktopIcon;
            }
        }

        return DesktopClickTarget.DesktopBackground;
    }

    internal static bool IsPotentialPeekSurfaceWindow(
        IntPtr hwnd,
        NativeMethods.POINT point,
        bool includeDesktop,
        bool includeTaskbar)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        bool isConfiguredSurface = (includeDesktop && IsDesktopRelatedWindow(hwnd))
            || (includeTaskbar
                && (IsTaskbarRelatedWindow(hwnd) || IsMonitorTaskbarAreaPoint(point)));

        return isConfiguredSurface && IsExplorerOwnedWindow(hwnd);
    }

    private static bool IsExplorerOwnedWindow(IntPtr hwnd)
    {
        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint processId);
        if (processId == 0)
            return false;

        return WindowBelongsToProcess(NativeMethods.FindWindow("Progman", null), processId)
            || WindowBelongsToProcess(NativeMethods.FindWindow("Shell_TrayWnd", null), processId);
    }

    private static bool WindowBelongsToProcess(IntPtr hwnd, uint processId)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint windowProcessId);
        return windowProcessId == processId;
    }

    internal static string GetDesktopRelationshipReason(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return "window handle was zero";

        IntPtr current = hwnd;
        while (current != IntPtr.Zero)
        {
            string className = NativeMethods.GetWindowClassName(current);

            if (string.Equals(className, "Progman", StringComparison.OrdinalIgnoreCase))
                return $"matched Progman ancestor: {NativeMethods.DescribeWindow(current)}";

            if (string.Equals(className, "WorkerW", StringComparison.OrdinalIgnoreCase))
            {
                IntPtr shellView = NativeMethods.FindWindowEx(current, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (shellView != IntPtr.Zero)
                    return $"matched desktop WorkerW ancestor: {NativeMethods.DescribeWindow(current)}";
            }

            current = NativeMethods.GetParent(current);
        }

        return $"no desktop ancestor found. hierarchy={NativeMethods.DescribeWindowHierarchy(hwnd)}";
    }

    /// <summary>
    /// Checks whether the given window is part of the desktop surface
    /// by walking up its parent chain looking for Progman or the desktop WorkerW.
    /// </summary>
    public static bool IsDesktopRelatedWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        IntPtr current = hwnd;
        while (current != IntPtr.Zero)
        {
            string className = NativeMethods.GetWindowClassName(current);

            if (string.Equals(className, "Progman", StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.Equals(className, "WorkerW", StringComparison.OrdinalIgnoreCase))
            {
                // Only the WorkerW that hosts SHELLDLL_DefView is the actual desktop.
                // Other WorkerW windows are unrelated shell helpers.
                IntPtr shellView = NativeMethods.FindWindowEx(
                    current, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (shellView != IntPtr.Zero)
                    return true;
            }

            current = NativeMethods.GetParent(current);
        }

        return false;
    }

    /// <summary>
    /// Returns true if the given foreground window should be treated as
    /// "the user is still on the desktop" — preventing a restore trigger.
    /// This includes the desktop itself plus transient UI (menus, tooltips).
    /// </summary>
    public static bool IsDesktopWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return true; // No foreground window — stay peeking

        string className = NativeMethods.GetWindowClassName(hwnd);

        // Context menus and tooltips are transient — treat as "still on desktop"
        if (string.Equals(className, "#32768", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(className, "tooltips_class32", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(className, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(className, "Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(className, "TopLevelWindowForOverflowXamlIsland", StringComparison.OrdinalIgnoreCase))
            return true;

        return IsDesktopRelatedWindow(hwnd);
    }

    private static bool IsDesktopIconWindow(IntPtr hwnd)
    {
        IntPtr current = hwnd;
        while (current != IntPtr.Zero)
        {
            string className = NativeMethods.GetWindowClassName(current);
            if (string.Equals(className, "SysListView32", StringComparison.OrdinalIgnoreCase))
                return true;

            current = NativeMethods.GetParent(current);
        }

        return false;
    }

    private static bool IsTaskbarRelatedWindow(IntPtr hwnd)
    {
        IntPtr current = hwnd;
        while (current != IntPtr.Zero)
        {
            string className = NativeMethods.GetWindowClassName(current);
            if (string.Equals(className, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(className, "Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            current = NativeMethods.GetParent(current);
        }

        return false;
    }

    private static bool IsTaskbarBlankAreaWindow(IntPtr hwnd, NativeMethods.POINT screenPoint)
    {
        string className = NativeMethods.GetWindowClassName(hwnd);
        if (!string.Equals(className, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(className, "Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase))
            return false;

        // WindowFromPoint returned Shell_TrayWnd itself. On Win11 this can happen
        // both for truly blank space AND for areas covered by transparent composition
        // overlays. Use ChildWindowFromPoint to find the real child, then skip
        // transparent overlay classes that cover the whole taskbar.
        NativeMethods.POINT clientPt = screenPoint;
        NativeMethods.ScreenToClient(hwnd, ref clientPt);
        IntPtr child = NativeMethods.ChildWindowFromPointEx(
            hwnd,
            clientPt,
            NativeMethods.CWP_SKIPINVISIBLE | NativeMethods.CWP_SKIPDISABLED | NativeMethods.CWP_SKIPTRANSPARENT);

        if (child == IntPtr.Zero || child == hwnd)
            child = NativeMethods.RealChildWindowFromPoint(hwnd, clientPt);

        if (child == IntPtr.Zero || child == hwnd)
            return ClassifyTaskbarOverlayPoint(screenPoint);

        // Skip transparent composition overlays that span the whole taskbar
        string childClass = NativeMethods.GetWindowClassName(child);
        if (childClass.StartsWith("Windows.UI.Composition", StringComparison.OrdinalIgnoreCase)
            || childClass.StartsWith("Windows.UI.Input", StringComparison.OrdinalIgnoreCase))
        {
            AppDiagnostics.Log($"Taskbar blank check: overlay child class={childClass}; deferring to UIA");
            return ClassifyTaskbarOverlayPoint(screenPoint);
        }

        AppDiagnostics.Log($"Taskbar blank check: child=0x{child.ToInt64():X} class={childClass} — not blank");
        return false;
    }

    private static bool ClassifyTaskbarOverlayPoint(NativeMethods.POINT screenPoint)
    {
        if (!UiAutomationCom.TryIsTaskbarElementInteractiveAtPoint(screenPoint, out bool isInteractive, out string description))
        {
            AppDiagnostics.Log($"Taskbar blank check: UIA unavailable at {NativeMethods.DescribePoint(screenPoint)}; treating as non-blank");
            return false;
        }

        AppDiagnostics.Log($"Taskbar UIA classification at {NativeMethods.DescribePoint(screenPoint)}: {description}");

        if (!isInteractive && IsTaskbarFrameClassification(description))
        {
            if (TryFindNearbyInteractiveTaskbarElement(screenPoint, out NativeMethods.POINT interactivePoint, out string nearbyDescription))
            {
                AppDiagnostics.Log(
                    $"Taskbar UIA nearby probe found interactive element near {NativeMethods.DescribePoint(screenPoint)} " +
                    $"at {NativeMethods.DescribePoint(interactivePoint)}: {nearbyDescription}");
                return false;
            }

            AppDiagnostics.Log(
                $"Taskbar UIA frame-only result at {NativeMethods.DescribePoint(screenPoint)} with no nearby interactive element");
        }

        return !isInteractive;
    }

    private static bool IsMonitorTaskbarAreaPoint(NativeMethods.POINT screenPoint)
    {
        IntPtr monitor = NativeMethods.MonitorFromPoint(screenPoint, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
            return false;

        var info = new NativeMethods.MONITORINFO
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>()
        };

        if (!NativeMethods.GetMonitorInfoW(monitor, ref info))
            return false;

        bool inMonitor =
            screenPoint.x >= info.rcMonitor.Left
            && screenPoint.x < info.rcMonitor.Right
            && screenPoint.y >= info.rcMonitor.Top
            && screenPoint.y < info.rcMonitor.Bottom;

        bool inWorkArea =
            screenPoint.x >= info.rcWork.Left
            && screenPoint.x < info.rcWork.Right
            && screenPoint.y >= info.rcWork.Top
            && screenPoint.y < info.rcWork.Bottom;

        return inMonitor && !inWorkArea;
    }

    private static bool IsTaskbarFrameClassification(string description)
    {
        return description.Contains("class=\"Taskbar.TaskbarFrameAutomationPeer\"", StringComparison.Ordinal)
            || description.Contains("aid=\"TaskbarFrame\"", StringComparison.Ordinal);
    }

    private static bool TryFindNearbyInteractiveTaskbarElement(
        NativeMethods.POINT origin,
        out NativeMethods.POINT interactivePoint,
        out string interactiveDescription)
    {
        static NativeMethods.POINT Offset(NativeMethods.POINT p, int dx, int dy) => new() { x = p.x + dx, y = p.y + dy };

        // Probe a small neighborhood so minor hit-testing drift still detects taskbar buttons.
        ReadOnlySpan<(int dx, int dy)> offsets =
        [
            (-48, 0), (-32, 0), (-20, 0), (-12, 0), (12, 0), (20, 0), (32, 0), (48, 0),
            (-24, -8), (-12, -8), (12, -8), (24, -8),
            (-24, 8), (-12, 8), (12, 8), (24, 8)
        ];

        foreach ((int dx, int dy) in offsets)
        {
            NativeMethods.POINT probe = Offset(origin, dx, dy);
            if (!UiAutomationCom.TryIsTaskbarElementInteractiveAtPoint(probe, out bool isInteractive, out string description))
                continue;

            if (!isInteractive)
                continue;

            interactivePoint = probe;
            interactiveDescription = description;
            return true;
        }

        interactivePoint = origin;
        interactiveDescription = string.Empty;
        return false;
    }
}
