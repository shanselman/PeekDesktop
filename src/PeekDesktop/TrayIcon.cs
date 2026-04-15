using System;

namespace PeekDesktop;

/// <summary>
/// Manages the system tray (notification area) icon and its context menu
/// using raw Win32 APIs (no WinForms dependency).
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    // Menu item IDs
    private const uint ID_ENABLED = 1;
    private const uint ID_STARTUP = 2;
    private const uint ID_DOUBLECLICK = 3;
    private const uint ID_MODE_MINIMIZE = 10;
    private const uint ID_MODE_FLYAWAY = 11;
    private const uint ID_MODE_NATIVE = 12;
    private const uint ID_ABOUT = 20;
    private const uint ID_UPDATES = 21;
    private const uint ID_EXIT = 30;

    private readonly Win32TrayIcon _trayIcon;
    private readonly Win32MessageLoop _messageLoop;
    private readonly DesktopPeek _desktopPeek;
    private readonly AppUpdater _appUpdater;
    private readonly Settings _settings;
    private readonly Action _exitAction;

    public TrayIcon(Win32MessageLoop messageLoop, DesktopPeek desktopPeek, AppUpdater appUpdater, Settings settings, Action exitAction)
    {
        _messageLoop = messageLoop;
        _desktopPeek = desktopPeek;
        _appUpdater = appUpdater;
        _settings = settings;
        _exitAction = exitAction;

        _trayIcon = new Win32TrayIcon(messageLoop.Handle);
        IntPtr hIcon = Win32Icon.CreateTrayIcon();
        _trayIcon.Add(hIcon, "PeekDesktop \u2014 click desktop to peek");

        _messageLoop.MessageReceived += OnMessage;
        _messageLoop.TaskbarCreated += OnTaskbarCreated;

        _appUpdater.UpdateAvailable += (_, e) =>
        {
            _trayIcon.ShowBalloon(
                "PeekDesktop Update Available",
                $"Version {e.Version} is available. Click here to open the download page.");
        };
    }

    private void OnTaskbarCreated()
    {
        AppDiagnostics.Log("Re-adding tray icon after Explorer restart");
        IntPtr hIcon = Win32Icon.CreateTrayIcon();
        _trayIcon.Add(hIcon, "PeekDesktop \u2014 click desktop to peek");
    }

    private (bool handled, IntPtr result) OnMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Win32TrayIcon.WM_TRAYICON)
        {
            if (Win32TrayIcon.IsRightClick(lParam))
            {
                ShowContextMenu();
                return (true, IntPtr.Zero);
            }

            if (Win32TrayIcon.IsBalloonClick(lParam))
            {
                _appUpdater.OpenLatestReleasePage();
                return (true, IntPtr.Zero);
            }
        }

        return (false, IntPtr.Zero);
    }

    private void ShowContextMenu()
    {
        using var menu = new Win32Menu();

        menu.AddItem(ID_ENABLED, "Enabled", ToggleEnabled, _settings.Enabled);
        menu.AddItem(ID_STARTUP, "Start with Windows", ToggleStartup, _settings.StartWithWindows);
        menu.AddItem(ID_DOUBLECLICK, "Require Double-Click", ToggleDoubleClick, _settings.RequireDoubleClick);
        menu.AddSeparator();
        menu.AddItem(ID_MODE_MINIMIZE, "Classic Minimize", () => SetPeekMode(PeekMode.Minimize), _settings.PeekMode == PeekMode.Minimize);
        menu.AddItem(ID_MODE_FLYAWAY, "Fly Away (Experimental)", () => SetPeekMode(PeekMode.FlyAway), _settings.PeekMode == PeekMode.FlyAway);
        menu.AddItem(ID_MODE_NATIVE, "Native Show Desktop (Explorer)", () => SetPeekMode(PeekMode.NativeShowDesktop), _settings.PeekMode == PeekMode.NativeShowDesktop);
        menu.AddSeparator();
        menu.AddItem(ID_ABOUT, "About PeekDesktop", ShowAbout);
        menu.AddItem(ID_UPDATES, "Check for Updates", CheckForUpdates);
        menu.AddSeparator();
        menu.AddItem(ID_EXIT, "Exit", DoExit);

        menu.Show(_messageLoop.Handle);
    }

    private void ToggleEnabled()
    {
        _settings.Enabled = !_settings.Enabled;
        _desktopPeek.IsEnabled = _settings.Enabled;

        if (_settings.Enabled)
            _desktopPeek.Start();
        else
            _desktopPeek.Stop();

        _settings.Save();
    }

    private void ToggleStartup()
    {
        _settings.StartWithWindows = !_settings.StartWithWindows;
        _settings.Save();
        Settings.SetAutoStart(_settings.StartWithWindows);
    }

    private void ToggleDoubleClick()
    {
        _settings.RequireDoubleClick = !_settings.RequireDoubleClick;
        _desktopPeek.SetRequireDoubleClick(_settings.RequireDoubleClick);
        _settings.Save();
    }

    private void SetPeekMode(PeekMode peekMode)
    {
        _settings.PeekMode = peekMode;
        _desktopPeek.SetPeekMode(peekMode);
        _trayIcon.UpdateTooltip($"PeekDesktop - {GetPeekModeDisplayName(peekMode)}");
        _settings.Save();
    }

    private void ShowAbout()
    {
        string version = GetDisplayVersion();
        NativeMethods.MessageBoxW(
            IntPtr.Zero,
            $"PeekDesktop v{version}\n\n" +
            "Click your desktop wallpaper to peek at your desktop,\n" +
            "just like macOS Sonoma.\n\n" +
            "Click any window or the taskbar to restore.\n" +
            "Peek Style lets you switch between classic minimize,\n" +
            "fly-away, and Explorer show desktop.\n\n" +
            "Updates come from GitHub Releases.\n\n" +
            "github.com/shanselman/PeekDesktop",
            "About PeekDesktop",
            NativeMethods.MB_OK | NativeMethods.MB_ICONINFORMATION);
    }

    private async void CheckForUpdates()
    {
        await _appUpdater.CheckForUpdatesAsync(interactive: true);
    }

    private void DoExit()
    {
        _trayIcon.Remove();
        _exitAction();
    }

    internal static string GetDisplayVersion()
    {
        var (productVersion, fileVersion) = NativeMethods.GetExeVersionInfo();
        string? version = productVersion ?? fileVersion?.ToString();

        if (string.IsNullOrWhiteSpace(version))
            return "unknown";

        int plusIndex = version.IndexOf('+');
        return plusIndex >= 0 ? version[..plusIndex] : version;
    }

    private static string GetPeekModeDisplayName(PeekMode peekMode)
    {
        return peekMode switch
        {
            PeekMode.Minimize => "Classic Minimize",
            PeekMode.FlyAway => "Fly Away",
            PeekMode.NativeShowDesktop => "Native Show Desktop",
            _ => "Peek"
        };
    }

    public void Dispose()
    {
        _trayIcon.Dispose();
    }
}
