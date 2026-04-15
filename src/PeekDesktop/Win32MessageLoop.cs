using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PeekDesktop;

/// <summary>
/// Provides a Win32 message loop and hidden top-level window for receiving
/// messages, replacing WinForms Application.Run / ApplicationContext.
/// Uses a real top-level window (not HWND_MESSAGE) so it can receive
/// broadcast messages like TaskbarCreated and participate in activation.
/// </summary>
internal sealed class Win32MessageLoop : IDisposable
{
    private const string ClassName = "PeekDesktop_MessageWindow";
    private const uint WM_TIMER = 0x0113;
    private const uint WM_APP_CALLBACK = 0x8001; // WM_APP + 1, for cross-thread callbacks

    private static Win32MessageLoop? s_instance;
    private readonly System.Collections.Generic.Dictionary<nuint, Action> _deferredActions = new();
    private readonly ConcurrentQueue<Action> _callbackQueue = new();
    private IntPtr _hwnd;
    private nuint _nextTimerId;
    private bool _disposed;

    /// <summary>
    /// The registered message ID for Explorer's TaskbarCreated notification.
    /// </summary>
    public uint TaskbarCreatedMessage { get; }

    public IntPtr Handle => _hwnd;

    /// <summary>
    /// Fired when a message is received on the hidden window.
    /// Return true to indicate the message was handled.
    /// </summary>
    public event Func<IntPtr, uint, IntPtr, IntPtr, (bool handled, IntPtr result)>? MessageReceived;

    /// <summary>
    /// Fired when Explorer restarts and re-creates the taskbar.
    /// Listeners should re-add their tray icons.
    /// </summary>
    public event Action? TaskbarCreated;

    public unsafe Win32MessageLoop()
    {
        s_instance = this;
        TaskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&StaticWndProc,
            hInstance = NativeMethods.GetModuleHandle(null),
            lpszClassName = ClassName
        };

        ushort atom = RegisterClassExW(ref wc);
        if (atom == 0)
            throw new InvalidOperationException($"RegisterClassExW failed: {Marshal.GetLastWin32Error()}");

        // Hidden top-level window (not HWND_MESSAGE) so we receive
        // broadcast messages and can participate in foreground activation.
        _hwnd = CreateWindowExW(
            0, ClassName, "", 0,
            0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero,
            NativeMethods.GetModuleHandle(null), IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowExW failed: {Marshal.GetLastWin32Error()}");
    }

    /// <summary>
    /// Posts a one-shot timer callback that fires after the message loop starts.
    /// Replaces the WinForms Timer used for deferred initialization.
    /// </summary>
    public void PostDeferredAction(uint delayMs, Action action)
    {
        nuint timerId = ++_nextTimerId;
        _deferredActions[timerId] = action;
        SetTimer(_hwnd, timerId, delayMs, IntPtr.Zero);
    }

    /// <summary>
    /// Marshals an action onto the message loop thread.
    /// Safe to call from any thread. Replaces SynchronizationContext.Post.
    /// </summary>
    public void BeginInvoke(Action action)
    {
        _callbackQueue.Enqueue(action);
        PostMessageW(_hwnd, WM_APP_CALLBACK, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// Runs the message loop. Blocks until PostQuitMessage is called.
    /// </summary>
    public void Run()
    {
        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    /// <summary>
    /// Posts WM_QUIT to exit the message loop.
    /// </summary>
    public void Quit()
    {
        PostQuitMessage(0);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (s_instance is { } self)
            return self.HandleMessage(hwnd, msg, wParam, lParam);
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private IntPtr HandleMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_TIMER)
        {
            nuint timerId = (nuint)wParam;
            if (_deferredActions.Remove(timerId, out Action? action))
            {
                KillTimer(hwnd, timerId);
                action();
            }
            return IntPtr.Zero;
        }

        if (msg == WM_APP_CALLBACK)
        {
            while (_callbackQueue.TryDequeue(out Action? action))
                action();
            return IntPtr.Zero;
        }

        if (msg == TaskbarCreatedMessage && TaskbarCreatedMessage != 0)
        {
            AppDiagnostics.Log("TaskbarCreated received; Explorer restarted");
            TaskbarCreated?.Invoke();
            return IntPtr.Zero;
        }

        if (MessageReceived is not null)
        {
            var (handled, result) = MessageReceived(hwnd, msg, wParam, lParam);
            if (handled)
                return result;
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        s_instance = null;

        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        UnregisterClassW(ClassName, NativeMethods.GetModuleHandle(null));
    }

    // --- P/Invoke ---

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public NativeMethods.POINT pt;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nuint SetTimer(IntPtr hWnd, nuint nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool KillTimer(IntPtr hWnd, nuint uIDEvent);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
