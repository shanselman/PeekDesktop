using System;
using System.Runtime.InteropServices;

namespace PeekDesktop;

/// <summary>
/// Win32 Shell_NotifyIcon wrapper, replacing WinForms NotifyIcon.
/// </summary>
internal sealed class Win32TrayIcon : IDisposable
{
    public const uint WM_TRAYICON = 0x0400 + 1; // WM_USER + 1

    private readonly IntPtr _hwnd;
    private IntPtr _hIcon;
    private bool _added;
    private bool _disposed;

    public Win32TrayIcon(IntPtr hwnd)
    {
        _hwnd = hwnd;
    }

    /// <summary>
    /// Adds the tray icon with the given tooltip and icon handle.
    /// </summary>
    public bool Add(IntPtr hIcon, string tooltip)
    {
        if (_added)
            Remove();

        if (_hIcon != IntPtr.Zero && _hIcon != hIcon)
            DestroyIcon(_hIcon);

        _hIcon = hIcon;
        var nid = MakeNid();
        nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        nid.uCallbackMessage = WM_TRAYICON;
        nid.hIcon = hIcon;
        nid.szTip = tooltip;

        if (!Shell_NotifyIconW(NIM_ADD, ref nid))
        {
            AppDiagnostics.Log($"Shell_NotifyIconW(NIM_ADD) failed: {Marshal.GetLastWin32Error()}");
            _added = false;
            return false;
        }

        // Request version 4 behavior in a separate call
        var versionNid = MakeNid();
        versionNid.uVersion = NOTIFYICON_VERSION_4;
        if (!Shell_NotifyIconW(NIM_SETVERSION, ref versionNid))
        {
            AppDiagnostics.Log($"Shell_NotifyIconW(NIM_SETVERSION) failed: {Marshal.GetLastWin32Error()}");
        }

        _added = true;
        AppDiagnostics.Log("Tray icon added");
        return true;
    }

    public void UpdateTooltip(string tooltip)
    {
        if (!_added) return;
        var nid = MakeNid();
        nid.uFlags = NIF_TIP | NIF_SHOWTIP;
        nid.szTip = tooltip;
        Shell_NotifyIconW(NIM_MODIFY, ref nid);
    }

    public void ShowBalloon(string title, string text)
    {
        if (!_added) return;
        var nid = MakeNid();
        nid.uFlags = NIF_INFO;
        nid.dwInfoFlags = NIIF_INFO;
        nid.szInfoTitle = title;
        nid.szInfo = text;
        Shell_NotifyIconW(NIM_MODIFY, ref nid);
    }

    public void Remove()
    {
        if (!_added) return;
        var nid = MakeNid();
        Shell_NotifyIconW(NIM_DELETE, ref nid);
        _added = false;
        AppDiagnostics.Log("Tray icon removed");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Remove();
        if (_hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Returns true if lParam represents a right-click on the tray icon.
    /// </summary>
    public static bool IsRightClick(IntPtr lParam) =>
        LOWORD(lParam) == WM_RBUTTONUP || LOWORD(lParam) == WM_CONTEXTMENU;

    /// <summary>
    /// Returns true if lParam represents a left-click (mouse select or keyboard
    /// activation) on the tray icon. Requires NOTIFYICON_VERSION_4 behavior.
    /// </summary>
    public static bool IsLeftClick(IntPtr lParam)
    {
        ushort code = LOWORD(lParam);
        return code == NIN_SELECT || code == NIN_KEYSELECT;
    }

    /// <summary>
    /// Returns true if lParam represents a left double-click on the tray icon.
    /// </summary>
    public static bool IsLeftDoubleClick(IntPtr lParam) =>
        LOWORD(lParam) == WM_LBUTTONDBLCLK;

    /// <summary>
    /// Returns true if the NIN_BALLOONUSERCLICK notification was sent.
    /// </summary>
    public static bool IsBalloonClick(IntPtr lParam) =>
        LOWORD(lParam) == NIN_BALLOONUSERCLICK;

    private NOTIFYICONDATAW MakeNid()
    {
        var nid = new NOTIFYICONDATAW();
        nid.cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>();
        nid.hWnd = _hwnd;
        nid.uID = 1;
        return nid;
    }

    private static ushort LOWORD(IntPtr value) => (ushort)((long)value & 0xFFFF);

    // --- Constants ---
    private const uint NIM_ADD = 0;
    private const uint NIM_MODIFY = 1;
    private const uint NIM_DELETE = 2;
    private const uint NIM_SETVERSION = 4;
    private const uint NIF_MESSAGE = 0x01;
    private const uint NIF_ICON = 0x02;
    private const uint NIF_TIP = 0x04;
    private const uint NIF_INFO = 0x10;
    private const uint NIF_SHOWTIP = 0x80;
    private const uint NIIF_INFO = 0x01;
    private const uint NOTIFYICON_VERSION_4 = 4;
    private const ushort WM_RBUTTONUP = 0x0205;
    private const ushort WM_LBUTTONDBLCLK = 0x0203;
    private const ushort WM_CONTEXTMENU = 0x007B;
    private const ushort NIN_SELECT = 0x0400;
    private const ushort NIN_KEYSELECT = 0x0401;
    private const ushort NIN_BALLOONUSERCLICK = 0x0405;

    // --- Struct ---
    // Using ByValTStr for string fields ensures proper marshaling to Shell_NotifyIconW.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string? szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string? szInfo;
        public uint uVersion; // union with uTimeout
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string? szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
