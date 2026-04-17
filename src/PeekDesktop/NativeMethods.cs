using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace PeekDesktop;

/// <summary>
/// Win32 P/Invoke declarations used throughout PeekDesktop.
/// </summary>
internal static class NativeMethods
{
    // --- Hook constants ---
    public const int WH_MOUSE_LL = 14;

    // --- Mouse messages ---
    public const int WM_MOUSEMOVE = 0x0200;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_LBUTTONUP = 0x0202;

    // --- ShowWindow commands ---
    public const int SW_SHOWNORMAL = 1;
    public const int SW_MAXIMIZE = 3;
    public const int SW_SHOWNOACTIVATE = 4;
    public const int SW_MINIMIZE = 6;
    public const int SW_RESTORE = 9;

    // --- GetWindow relationship ---
    public const uint GW_OWNER = 4;

    // --- Window style indices ---
    public const int GWL_EXSTYLE = -20;

    // --- SetWindowPos flags ---
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_ASYNCWINDOWPOS = 0x4000;
    public const uint SWP_NOOWNERZORDER = 0x0200;
    public const uint SWP_NOSENDCHANGING = 0x0400;

    // --- Extended window styles ---
    public const long WS_EX_TOOLWINDOW = 0x00000080L;
    public const long WS_EX_NOACTIVATE = 0x08000000L;

    // --- WinEvent constants ---
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint SMTO_ABORTIFHUNG = 0x0002;

    // --- DWM constants ---
    public const int DWMWA_CLOAKED = 14;

    // --- Keyboard input ---
    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_D = 0x44;

    // --- MSAA accessible roles ---
    public const int ROLE_SYSTEM_LISTITEM = 0x22;

    // --- Shell notification state ---
    public enum UserNotificationState
    {
        NotPresent = 1,
        Busy = 2,
        RunningD3DFullScreen = 3,
        PresentationMode = 4,
        AcceptsNotifications = 5,
        QuietTime = 6,
        App = 7
    }

    // --- ListView hit testing ---
    private const int LVM_FIRST = 0x1000;
    private const int LVM_HITTEST = LVM_FIRST + 18;
    private const uint LVHT_ONITEMICON = 0x0002;
    private const uint LVHT_ONITEMLABEL = 0x0004;
    private const uint LVHT_ONITEMSTATEICON = 0x0008;
    private const uint LVHT_ONITEM = LVHT_ONITEMICON | LVHT_ONITEMLABEL | LVHT_ONITEMSTATEICON;

    // --- Remote memory helpers ---
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint PROCESS_VM_OPERATION = 0x0008;
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_VM_WRITE = 0x0020;
    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint MEM_RELEASE = 0x8000;
    private const uint PAGE_READWRITE = 0x04;

    #region Delegates

    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    public delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    #endregion

    #region Structs

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LVHITTESTINFO
    {
        public POINT pt;
        public uint flags;
        public int iItem;
        public int iSubItem;
        public int iGroup;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;

        [FieldOffset(0)]
        public KEYBDINPUT ki;

        [FieldOffset(0)]
        public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    #endregion

    #region Hook functions

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(
        int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(
        IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    #endregion

    #region Window query functions

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(
        IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern IntPtr RealChildWindowFromPoint(IntPtr hwndParent, POINT pt);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    public const int SM_CXDOUBLECLK = 36;
    public const int SM_CYDOUBLECLK = 37;
    public const int SM_CXDRAG = 68;
    public const int SM_CYDRAG = 69;

    // --- Monitor info ---
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    // --- MessageBox ---
    public const uint MB_OK = 0x00000000;
    public const uint MB_YESNO = 0x00000004;
    public const uint MB_ICONINFORMATION = 0x00000040;
    public const uint MB_ICONERROR = 0x00000010;
    public const int IDYES = 6;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);

    #endregion

    #region Window manipulation

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags);

    #endregion

    #region GetWindowLong (64-bit safe)

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    public static long GetWindowLongValue(IntPtr hWnd, int nIndex)
    {
        if (IntPtr.Size == 8)
            return GetWindowLongPtr64(hWnd, nIndex).ToInt64();
        return GetWindowLong32(hWnd, nIndex);
    }

    #endregion

    #region WinEvent

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    #endregion

    #region Module / DWM

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(
        uint idAttach,
        uint idAttachTo,
        [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, nuint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, nuint dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int nSize, out IntPtr lpNumberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, [Out] byte[] lpBuffer, int nSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        int length,
        out IntPtr hstring);

    [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out UserNotificationState pquns);

    #endregion

    #region Helpers

    public static string GetWindowClassName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return string.Empty;
        var sb = new StringBuilder(256);
        int len = GetClassName(hwnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString() : string.Empty;
    }

    public static string GetWindowTitle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return string.Empty;
        int length = GetWindowTextLength(hwnd);
        if (length == 0) return string.Empty;
        var sb = new StringBuilder(length + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string DescribeWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return "hwnd=0x0 class=<none> title=<none>";

        string className = GetWindowClassName(hwnd);
        string title = GetWindowTitle(hwnd);
        return $"hwnd=0x{hwnd.ToInt64():X} class={className} title=\"{title}\"";
    }

    public static string DescribePoint(POINT point)
    {
        return $"x={point.x}, y={point.y}";
    }

    public static bool TryGetCursorPoint(out POINT point)
    {
        return GetCursorPos(out point);
    }

    public static string DescribeWindowHierarchy(IntPtr hwnd, int maxDepth = 8)
    {
        if (hwnd == IntPtr.Zero)
            return "<none>";

        var parts = new List<string>();
        IntPtr current = hwnd;

        while (current != IntPtr.Zero && parts.Count < maxDepth)
        {
            parts.Add(DescribeWindow(current));
            current = GetParent(current);
        }

        return string.Join(" -> ", parts);
    }

    public static bool TryGetAccessibleDetailsAtPoint(POINT point, out int role, out string name)
    {
        role = 0;
        name = string.Empty;

        // The source-generated UI Automation path is being developed separately.
        // Keep the shipping baseline behavior stable until that integration is complete.
        AppDiagnostics.Log("Accessible hit-testing is disabled in the current baseline build.");
        return false;
    }

    public static bool TryIsDesktopListViewItemAtPoint(IntPtr hwnd, POINT screenPoint, out bool isOnItem)
    {
        isOnItem = false;

        IntPtr listView = FindAncestorByClassName(hwnd, "SysListView32");
        if (listView == IntPtr.Zero)
            return false;

        POINT clientPoint = screenPoint;
        if (!ScreenToClient(listView, ref clientPoint))
            return false;

        var hitTest = new LVHITTESTINFO
        {
            pt = clientPoint,
            iItem = -1,
            iSubItem = -1,
            iGroup = -1
        };

        return TryListViewHitTest(listView, ref hitTest, out isOnItem);
    }

    /// <summary>
    /// Returns true if the window is cloaked (hidden by DWM, e.g. on another virtual desktop).
    /// </summary>
    public static bool IsWindowCloaked(IntPtr hwnd)
    {
        int hr = DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int));
        return hr == 0 && cloaked != 0;
    }

    public static bool TryGetUserNotificationState(out UserNotificationState state)
    {
        int hr = SHQueryUserNotificationState(out state);
        if (hr == 0)
            return true;

        state = UserNotificationState.AcceptsNotifications;
        return false;
    }

    public static bool TryToggleDesktop()
    {
        if (TryToggleDesktopWithTaskbarButton())
        {
            AppDiagnostics.Log("Show desktop activated via taskbar button");
            return true;
        }

        if (TryToggleDesktopWithWinD())
        {
            AppDiagnostics.Log("Show desktop activated via Win+D input");
            return true;
        }

        AppDiagnostics.Log("Failed to toggle desktop via taskbar button and Win+D");
        return false;
    }

    private const uint BM_CLICK = 0x00F5;

    private static bool TryToggleDesktopWithTaskbarButton()
    {
        IntPtr hTray = FindWindow("Shell_TrayWnd", null);
        if (hTray == IntPtr.Zero)
        {
            AppDiagnostics.Log("Taskbar window (Shell_TrayWnd) not found");
            return false;
        }

        IntPtr hButton = FindWindowEx(hTray, IntPtr.Zero, "TrayShowDesktopButtonWClass", null);
        if (hButton == IntPtr.Zero)
        {
            AppDiagnostics.Log("Show Desktop button (TrayShowDesktopButtonWClass) not found");
            return false;
        }

        SendMessageW(hButton, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
        return true;
    }

    private static bool TryToggleDesktopWithWinD()
    {
        INPUT[] inputs =
        [
            CreateKeyInput(VK_LWIN, keyUp: false, extendedKey: true),
            CreateKeyInput(VK_D, keyUp: false),
            CreateKeyInput(VK_D, keyUp: true),
            CreateKeyInput(VK_LWIN, keyUp: true, extendedKey: true)
        ];

        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            int lastError = Marshal.GetLastWin32Error();
            AppDiagnostics.Log($"Win+D SendInput failed: sent={sent}/{inputs.Length} lastError={lastError}");
            return false;
        }

        return true;
    }

    private static IntPtr FindAncestorByClassName(IntPtr hwnd, string className)
    {
        IntPtr current = hwnd;
        while (current != IntPtr.Zero)
        {
            if (string.Equals(GetWindowClassName(current), className, StringComparison.OrdinalIgnoreCase))
                return current;

            current = GetParent(current);
        }

        return IntPtr.Zero;
    }

    private static bool TryListViewHitTest(IntPtr listView, ref LVHITTESTINFO hitTest, out bool isOnItem)
    {
        isOnItem = false;

        _ = GetWindowThreadProcessId(listView, out uint processId);
        if (processId == 0)
            return false;

        // Only perform cross-process memory operations against Explorer
        if (!TryGetProcessName(processId, out string procName)
            || !string.Equals(procName, "explorer", StringComparison.OrdinalIgnoreCase))
        {
            AppDiagnostics.Log($"ListView hit-test skipped: target process '{procName}' is not Explorer");
            return false;
        }

        IntPtr processHandle = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE, false, processId);
        if (processHandle == IntPtr.Zero)
            return false;

        int size = Marshal.SizeOf<LVHITTESTINFO>();
        IntPtr remoteBuffer = IntPtr.Zero;
        IntPtr localBuffer = IntPtr.Zero;

        try
        {
            remoteBuffer = VirtualAllocEx(processHandle, IntPtr.Zero, (nuint)size, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (remoteBuffer == IntPtr.Zero)
                return false;

            localBuffer = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(hitTest, localBuffer, false);

            var bytes = new byte[size];
            Marshal.Copy(localBuffer, bytes, 0, size);

            if (!WriteProcessMemory(processHandle, remoteBuffer, bytes, bytes.Length, out _))
                return false;

            if (SendMessageTimeout(listView, LVM_HITTEST, IntPtr.Zero, remoteBuffer, SMTO_ABORTIFHUNG, 100, out IntPtr messageResult) == IntPtr.Zero)
                return false;

            if (!ReadProcessMemory(processHandle, remoteBuffer, bytes, bytes.Length, out _))
                return false;

            Marshal.Copy(bytes, 0, localBuffer, size);
            unsafe { hitTest = *(LVHITTESTINFO*)localBuffer; }

            isOnItem = messageResult.ToInt64() >= 0 || (hitTest.flags & LVHT_ONITEM) != 0;
            return true;
        }
        finally
        {
            if (localBuffer != IntPtr.Zero)
                Marshal.FreeHGlobal(localBuffer);

            if (remoteBuffer != IntPtr.Zero)
                VirtualFreeEx(processHandle, remoteBuffer, 0, MEM_RELEASE);

            CloseHandle(processHandle);
        }
    }

    /// <summary>
    /// Gets the process name (without extension) for a given process ID using Win32 APIs.
    /// Replaces System.Diagnostics.Process.GetProcessById to avoid pulling in that framework.
    /// </summary>
    public static bool TryGetProcessName(uint processId, out string processName)
    {
        processName = string.Empty;
        if (processId == 0)
            return false;

        IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (hProcess == IntPtr.Zero)
            return false;

        try
        {
            uint size = 1024;
            var buffer = new StringBuilder((int)size);
            if (!QueryFullProcessImageNameW(hProcess, 0, buffer, ref size))
                return false;

            string fullPath = buffer.ToString();
            processName = System.IO.Path.GetFileNameWithoutExtension(fullPath);
            return !string.IsNullOrWhiteSpace(processName);
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    private static INPUT CreateKeyInput(ushort virtualKey, bool keyUp, bool extendedKey = false)
    {
        uint flags = keyUp ? KEYEVENTF_KEYUP : 0;
        if (extendedKey)
            flags |= KEYEVENTF_EXTENDEDKEY;

        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = virtualKey,
                    dwFlags = flags
                }
            }
        };
    }

    #endregion

    // --- Shell ---

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr ShellExecuteW(
        IntPtr hwnd,
        string? lpOperation,
        string lpFile,
        string? lpParameters,
        string? lpDirectory,
        int nShowCmd);

    // --- Version info ---

    private const uint FILE_VER_GET_NEUTRAL = 0x02;

    [DllImport("version.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileVersionInfoSizeExW(uint dwFlags, string lpwstrFilename, out uint lpdwHandle);

    [DllImport("version.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileVersionInfoExW(uint dwFlags, string lpwstrFilename, uint dwHandle, uint dwLen, byte[] lpData);

    [DllImport("version.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VerQueryValueW(byte[] pBlock, string lpSubBlock, out IntPtr lplpBuffer, out uint puLen);

    /// <summary>
    /// Reads version information from the current executable's PE version resource,
    /// replacing reflection-based version lookups.
    /// </summary>
    public static (string? ProductVersion, Version? FileVersion) GetExeVersionInfo()
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
            return (null, null);

        uint size = GetFileVersionInfoSizeExW(FILE_VER_GET_NEUTRAL, exePath, out uint handle);
        if (size == 0)
            return (null, null);

        byte[] data = new byte[size];
        if (!GetFileVersionInfoExW(FILE_VER_GET_NEUTRAL, exePath, handle, size, data))
            return (null, null);

        Version? fileVersion = null;
        if (VerQueryValueW(data, @"\", out IntPtr fixedPtr, out uint fixedLen) && fixedLen >= 52)
        {
            uint vms = (uint)Marshal.ReadInt32(fixedPtr, 8);
            uint vls = (uint)Marshal.ReadInt32(fixedPtr, 12);
            fileVersion = new Version(
                (int)(vms >> 16), (int)(vms & 0xFFFF),
                (int)(vls >> 16), (int)(vls & 0xFFFF));
        }

        string? productVersion = null;
        if (VerQueryValueW(data, @"\VarFileInfo\Translation", out IntPtr transPtr, out uint transLen) && transLen >= 4)
        {
            ushort lang = (ushort)Marshal.ReadInt16(transPtr, 0);
            ushort cp = (ushort)Marshal.ReadInt16(transPtr, 2);
            string subBlock = $@"\StringFileInfo\{lang:x4}{cp:x4}\ProductVersion";
            if (VerQueryValueW(data, subBlock, out IntPtr strPtr, out uint strLen) && strLen > 0)
            {
                productVersion = Marshal.PtrToStringUni(strPtr);
            }
        }

        return (productVersion, fileVersion);
    }
}
