using System;
using System.Collections.Generic;

namespace PeekDesktop;

/// <summary>
/// Core orchestrator for the peek-desktop feature.
/// State machine with two states: Idle and Peeking.
///
///   Idle → Peeking:  user clicks empty desktop wallpaper
///   Peeking → Idle:  a non-desktop window gains foreground focus
/// </summary>
public sealed class DesktopPeek : IDisposable
{
    private const int PostPeekFocusGracePeriodMs = 200;
    private const int PostPeekRestoreClickGracePeriodMs = 300;
    private static readonly HashSet<string> KnownGamingProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam",
        "steamwebhelper",
        "steamgameoverlayui",
        "xboxpcapp",
        "gamebar",
        "gamebarftserver",
        "gamebarpresencewriter"
    };

    private readonly MouseHook _mouseHook = new();
    private readonly FocusWatcher _focusWatcher = new();
    private readonly WindowTracker _windowTracker = new();

    private bool _isPeeking;
    private bool _isTransitioning; // suppresses events during minimize/restore
    private bool _nativeShellToggled;
    private bool _pauseWhileFullscreenAppActive;
    private bool _isSuppressedForGaming;
    private long _ignoreFocusUntil;
    private long _ignoreRestoreClickUntil;
    private string _gameSuppressionReason = string.Empty;
    private PeekMode _activePeekMode = PeekMode.Minimize;

    public bool IsEnabled { get; set; } = true;
    public bool IsPeeking => _isPeeking;
    public PeekMode PeekMode { get; set; }

    public DesktopPeek(Settings settings)
    {
        PeekMode = NormalizePeekMode(settings.PeekMode);
        _mouseHook.RequireDoubleClick = settings.RequireDoubleClick;
        _pauseWhileFullscreenAppActive = settings.PauseWhileFullscreenAppActive;
        AppDiagnostics.Log("DesktopPeek created");
        _mouseHook.DesktopClicked += OnDesktopClicked;
        _mouseHook.DesktopIconClicked += OnDesktopIconClicked;
        _mouseHook.NonDesktopClicked += OnNonDesktopClicked;
        _focusWatcher.FocusChanged += OnFocusChanged;
    }

    public void SetRequireDoubleClick(bool requireDoubleClick)
    {
        _mouseHook.RequireDoubleClick = requireDoubleClick;
        AppDiagnostics.Log($"RequireDoubleClick set to {requireDoubleClick}");
    }

    public void SetPauseWhileFullscreenAppActive(bool enabled)
    {
        _pauseWhileFullscreenAppActive = enabled;
        AppDiagnostics.Log($"PauseWhileFullscreenAppActive set to {enabled}");

        if (!enabled)
        {
            if (_isSuppressedForGaming)
            {
                _isSuppressedForGaming = false;
                _gameSuppressionReason = string.Empty;
                AppDiagnostics.Log("Gaming suppression cleared");
            }

            return;
        }

        UpdateGamingSuppressionState(NativeMethods.GetForegroundWindow());
    }

    public void SetPeekMode(PeekMode peekMode)
    {
        peekMode = NormalizePeekMode(peekMode);
        bool modeChanged = PeekMode != peekMode;
        PeekMode = peekMode;

        if (modeChanged)
        {
            AppDiagnostics.Log($"Peek mode changed to {peekMode}. IsPeeking={_isPeeking} Transitioning={_isTransitioning}");
        }
        else
        {
            AppDiagnostics.Log($"Peek mode reaffirmed as {peekMode}. IsPeeking={_isPeeking} ActiveMode={_activePeekMode}");
        }

        if (!_isPeeking || _isTransitioning || !IsEnabled)
            return;

        if (!modeChanged && _activePeekMode == peekMode)
            return;

        AppDiagnostics.Log("Applying newly selected peek mode immediately");
        RestoreWindows();
        PeekDesktopNow();
    }

    private static PeekMode NormalizePeekMode(PeekMode peekMode)
    {
        return Enum.IsDefined(typeof(PeekMode), peekMode)
            ? peekMode
            : PeekMode.NativeShowDesktop;
    }

    public void Start()
    {
        AppDiagnostics.Log($"Start requested. Enabled={IsEnabled}");
        _mouseHook.Install();
        _focusWatcher.Start();
        UpdateGamingSuppressionState(NativeMethods.GetForegroundWindow());
    }

    public void Stop()
    {
        AppDiagnostics.Log($"Stop requested. IsPeeking={_isPeeking}");
        _mouseHook.Uninstall();
        _focusWatcher.Stop();

        if (_isPeeking)
            RestoreWindows();
    }

    private void OnDesktopClicked(object? sender, EventArgs e)
    {
        if (!IsEnabled || _isTransitioning)
        {
            AppDiagnostics.Log($"Desktop click ignored. Enabled={IsEnabled} IsPeeking={_isPeeking} Transitioning={_isTransitioning}");
            return;
        }

        if (_isSuppressedForGaming)
        {
            AppDiagnostics.Log($"Desktop click ignored because gaming protection is active ({_gameSuppressionReason})");
            return;
        }

        if (_isPeeking)
        {
            if (Environment.TickCount64 < _ignoreRestoreClickUntil)
            {
                AppDiagnostics.Log("Desktop click ignored because it immediately followed activation");
                return;
            }

            AppDiagnostics.Log("Desktop clicked again while peeking; restoring windows");
            RestoreWindows();
            return;
        }

        AppDiagnostics.Log("Desktop click accepted; entering peek mode");
        PeekDesktopNow();
    }

    private void OnDesktopIconClicked(object? sender, EventArgs e)
    {
        if (_isTransitioning)
        {
            AppDiagnostics.Log("Desktop icon click ignored during transition");
            return;
        }

        if (_isPeeking)
        {
            AppDiagnostics.Log("Desktop icon clicked while peeking; staying in peek mode");
            return;
        }

        AppDiagnostics.Log("Desktop icon clicked; not entering peek mode");
    }

    private void OnNonDesktopClicked(object? sender, EventArgs e)
    {
        if (!_isPeeking || _isTransitioning)
        {
            AppDiagnostics.Log($"Non-desktop click ignored. IsPeeking={_isPeeking} Transitioning={_isTransitioning}");
            return;
        }

        if (Environment.TickCount64 < _ignoreRestoreClickUntil)
        {
            AppDiagnostics.Log("Non-desktop click ignored because it immediately followed activation");
            return;
        }

        if (_nativeShellToggled)
        {
            AppDiagnostics.Log("Non-desktop click while native show desktop is active; deferring restore to shell");
            return;
        }

        AppDiagnostics.Log("Non-desktop click detected while peeking; restoring windows");
        RestoreWindows();
    }

    private void OnFocusChanged(object? sender, FocusChangedEventArgs e)
    {
        UpdateGamingSuppressionState(e.ForegroundWindow);

        if (!_isPeeking || _isTransitioning)
            return;

        AppDiagnostics.LogWindow("Focus changed while peeking", e.ForegroundWindow);

        // If the new foreground is still the desktop or transient desktop UI, stay peeking
        if (DesktopDetector.IsDesktopWindow(e.ForegroundWindow))
        {
            AppDiagnostics.Log("Foreground is still desktop-related; staying in peek mode");
            return;
        }

        // Ignore our own tray/message windows so shell-backed modes don't
        // immediately unwind due to internal focus churn.
        if (IsOwnedByCurrentProcess(e.ForegroundWindow))
        {
            AppDiagnostics.Log("Foreground belongs to PeekDesktop; ignoring");
            return;
        }

        // Focus can churn briefly while windows are being minimized. Ignore those
        // immediate foreground changes so the initial desktop click stays in peek mode.
        if (Environment.TickCount64 < _ignoreFocusUntil)
        {
            AppDiagnostics.Log("Foreground change fell inside grace period; ignoring");
            return;
        }

        if (_nativeShellToggled)
        {
            AppDiagnostics.Log("Foreground moved away from desktop while native show desktop is active; clearing shell-backed peek state");
            _nativeShellToggled = false;
            _isPeeking = false;
            _ignoreFocusUntil = 0;
            _ignoreRestoreClickUntil = 0;
            _activePeekMode = PeekMode;
            return;
        }

        AppDiagnostics.Log("Foreground moved away from desktop; restoring windows");
        RestoreWindows();
    }

    private void PeekDesktopNow()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        _isTransitioning = true;
        AppDiagnostics.Log("Beginning peek transition");
        try
        {
            _activePeekMode = PeekMode;
            _nativeShellToggled = false;

            if (_activePeekMode == PeekMode.NativeShowDesktop)
            {
                AppDiagnostics.Log($"Native toggle context: thread={Environment.CurrentManagedThreadId} apartment={System.Threading.Thread.CurrentThread.GetApartmentState()}");
                if (NativeMethods.TryToggleDesktop())
                {
                    _windowTracker.ClearSavedWindows();
                    _nativeShellToggled = true;
                    _isPeeking = true;
                    _ignoreFocusUntil = Environment.TickCount64 + PostPeekFocusGracePeriodMs;
                    _ignoreRestoreClickUntil = Environment.TickCount64 + PostPeekRestoreClickGracePeriodMs;
                    AppDiagnostics.Log($"Peek mode active; ignoring focus churn for {PostPeekFocusGracePeriodMs}ms");
                    AppDiagnostics.Log($"Peek mode active; ignoring restore clicks for {PostPeekRestoreClickGracePeriodMs}ms");
                    AppDiagnostics.Log("Native show desktop activated");
                    return;
                }

                AppDiagnostics.Log("Native show desktop failed; falling back to classic minimize");
                _activePeekMode = PeekMode.Minimize;
            }

            _windowTracker.CaptureWindows();

            if (_windowTracker.HasWindows)
            {
                AppDiagnostics.Log($"Captured {_windowTracker.SavedWindowCount} window(s); applying {_activePeekMode} effect");

                if (_activePeekMode == PeekMode.FlyAway)
                    _windowTracker.FlyAwayAll();
                else
                    _windowTracker.MinimizeAll();

                _isPeeking = true;
                _ignoreFocusUntil = Environment.TickCount64 + PostPeekFocusGracePeriodMs;
                _ignoreRestoreClickUntil = Environment.TickCount64 + PostPeekRestoreClickGracePeriodMs;
                AppDiagnostics.Log($"Peek mode active; ignoring focus churn for {PostPeekFocusGracePeriodMs}ms");
                AppDiagnostics.Log($"Peek mode active; ignoring restore clicks for {PostPeekRestoreClickGracePeriodMs}ms");
            }
            else
            {
                AppDiagnostics.Log("No restoreable windows were captured");
            }
        }
        finally
        {
            _isTransitioning = false;
            AppDiagnostics.Log("Peek transition complete");
            AppDiagnostics.Metric($"PeekDesktopNow total: {stopwatch.ElapsedMilliseconds}ms");
        }
    }

    private void RestoreWindows()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        _isTransitioning = true;
        AppDiagnostics.Log($"Beginning restore transition for {_windowTracker.SavedWindowCount} window(s)");
        try
        {
            _ignoreFocusUntil = 0;
            _ignoreRestoreClickUntil = 0;

            if (_nativeShellToggled)
            {
                NativeMethods.TryToggleDesktop();
                _windowTracker.ClearSavedWindows();
                _nativeShellToggled = false;
            }
            else
            {
                _windowTracker.RestoreAll(_activePeekMode);
            }

            _isPeeking = false;
            _activePeekMode = PeekMode;
            AppDiagnostics.Log("Restore complete; returned to idle");
        }
        finally
        {
            _isTransitioning = false;
            AppDiagnostics.Metric($"RestoreWindows total: {stopwatch.ElapsedMilliseconds}ms");
        }
    }

    public void Dispose()
    {
        AppDiagnostics.Log("DesktopPeek disposing");
        Stop();
        _mouseHook.Dispose();
        _focusWatcher.Dispose();
    }

    private static bool IsOwnedByCurrentProcess(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint processId);
        return processId == (uint)Environment.ProcessId;
    }

    private void UpdateGamingSuppressionState(IntPtr foregroundWindow)
    {
        bool shouldSuppress = ShouldSuppressForGaming(foregroundWindow, out string reason);
        if (_isSuppressedForGaming == shouldSuppress && string.Equals(_gameSuppressionReason, reason, StringComparison.Ordinal))
            return;

        if (shouldSuppress)
            AppDiagnostics.Log($"Gaming protection active ({reason}); desktop peek is paused");
        else if (_isSuppressedForGaming)
            AppDiagnostics.Log("Gaming protection inactive; desktop peek resumed");

        _isSuppressedForGaming = shouldSuppress;
        _gameSuppressionReason = reason;
    }

    private bool ShouldSuppressForGaming(IntPtr foregroundWindow, out string reason)
    {
        reason = string.Empty;

        if (!_pauseWhileFullscreenAppActive)
            return false;

        if (foregroundWindow == IntPtr.Zero || !NativeMethods.IsWindow(foregroundWindow))
            return false;

        if (IsOwnedByCurrentProcess(foregroundWindow) || DesktopDetector.IsDesktopWindow(foregroundWindow))
            return false;

        if (NativeMethods.TryGetUserNotificationState(out NativeMethods.UserNotificationState notificationState)
            && notificationState == NativeMethods.UserNotificationState.RunningD3DFullScreen)
        {
            reason = "running-d3d-full-screen";
            return true;
        }

        if (!IsWindowFullscreen(foregroundWindow))
            return false;

        if (TryGetForegroundProcessName(foregroundWindow, out string processName) && KnownGamingProcesses.Contains(processName))
        {
            reason = $"gaming-process:{processName}";
            return true;
        }

        return false;
    }

    private static bool IsWindowFullscreen(IntPtr hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT windowRect))
            return false;

        IntPtr monitor = NativeMethods.MonitorFromRect(ref windowRect, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
            return false;

        var monitorInfo = new NativeMethods.MONITORINFO
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>()
        };

        if (!NativeMethods.GetMonitorInfoW(monitor, ref monitorInfo))
            return false;

        const int tolerance = 2;
        return Math.Abs(windowRect.Left - monitorInfo.rcMonitor.Left) <= tolerance
            && Math.Abs(windowRect.Top - monitorInfo.rcMonitor.Top) <= tolerance
            && Math.Abs(windowRect.Right - monitorInfo.rcMonitor.Right) <= tolerance
            && Math.Abs(windowRect.Bottom - monitorInfo.rcMonitor.Bottom) <= tolerance;
    }

    private static bool TryGetForegroundProcessName(IntPtr foregroundWindow, out string processName)
    {
        processName = string.Empty;
        _ = NativeMethods.GetWindowThreadProcessId(foregroundWindow, out uint processId);
        if (processId == 0)
            return false;

        return NativeMethods.TryGetProcessName(processId, out processName);
    }
}
