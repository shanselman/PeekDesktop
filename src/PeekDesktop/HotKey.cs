using System;

namespace PeekDesktop;

/// <summary>
/// Wraps the Win32 <c>RegisterHotKey</c> API to provide a global toggle
/// hotkey. Uses <c>MOD_NOREPEAT</c> so holding the combo fires once per press.
/// </summary>
/// <remarks>
/// We intentionally use the registered-hotkey API rather than a low-level
/// keyboard hook. Peek toggling is a single-shot action, so we don't need
/// to observe, swallow, or synthesize keystrokes — which means we avoid
/// every low-level-hook pitfall (injection filtering, stuck modifiers,
/// focus-loss cleanup, SendInput failures, etc.).
/// </remarks>
internal sealed class HotKey : IDisposable
{
    private const int HotKeyId = 0xBEEF;

    private readonly Win32MessageLoop _messageLoop;
    private bool _registered;
    private uint _modifiers;
    private uint _vk;

    public event Action? Pressed;

    public bool IsRegistered => _registered;

    public HotKey(Win32MessageLoop messageLoop)
    {
        _messageLoop = messageLoop;
        _messageLoop.MessageReceived += OnMessage;
    }

    /// <summary>
    /// Registers the hotkey. Returns true on success, false if the combo
    /// is already claimed by another process (or registration otherwise fails).
    /// </summary>
    public bool Register(uint modifiers, uint virtualKey)
    {
        Unregister();

        _modifiers = modifiers;
        _vk = virtualKey;

        if (modifiers == 0 || virtualKey == 0)
        {
            AppDiagnostics.Log("Hotkey registration skipped: modifiers or vk is zero");
            return false;
        }

        bool ok = NativeMethods.RegisterHotKey(
            _messageLoop.Handle,
            HotKeyId,
            modifiers | NativeMethods.MOD_NOREPEAT,
            virtualKey);

        if (!ok)
        {
            int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            AppDiagnostics.Log($"RegisterHotKey failed ({Describe(modifiers, virtualKey)}): Win32 error {err}");
            _registered = false;
            return false;
        }

        _registered = true;
        AppDiagnostics.Log($"Hotkey registered: {Describe(modifiers, virtualKey)}");
        return true;
    }

    public void Unregister()
    {
        if (!_registered)
            return;

        if (!NativeMethods.UnregisterHotKey(_messageLoop.Handle, HotKeyId))
        {
            int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            AppDiagnostics.Log($"UnregisterHotKey failed: Win32 error {err}");
        }
        else
        {
            AppDiagnostics.Log($"Hotkey unregistered: {Describe(_modifiers, _vk)}");
        }

        _registered = false;
    }

    private (bool handled, IntPtr result) OnMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotKeyId)
        {
            Pressed?.Invoke();
            return (true, IntPtr.Zero);
        }

        return (false, IntPtr.Zero);
    }

    public void Dispose()
    {
        _messageLoop.MessageReceived -= OnMessage;
        Unregister();
    }

    public static string Describe(uint modifiers, uint vk)
    {
        var parts = new System.Text.StringBuilder();
        if ((modifiers & NativeMethods.MOD_CONTROL) != 0) parts.Append("Ctrl+");
        if ((modifiers & NativeMethods.MOD_ALT) != 0) parts.Append("Alt+");
        if ((modifiers & NativeMethods.MOD_SHIFT) != 0) parts.Append("Shift+");
        if ((modifiers & NativeMethods.MOD_WIN) != 0) parts.Append("Win+");
        parts.Append(VkToDisplayName(vk));
        return parts.ToString();
    }

    private static string VkToDisplayName(uint vk)
    {
        // Printable ASCII range for letters and digits — RegisterHotKey uses
        // the same values as VK_A..VK_Z (0x41..0x5A) and VK_0..VK_9 (0x30..0x39).
        if ((vk >= 0x30 && vk <= 0x39) || (vk >= 0x41 && vk <= 0x5A))
            return ((char)vk).ToString();

        return vk switch
        {
            0x20 => "Space",
            0x70 => "F1",
            0x71 => "F2",
            0x72 => "F3",
            0x73 => "F4",
            0x74 => "F5",
            0x75 => "F6",
            0x76 => "F7",
            0x77 => "F8",
            0x78 => "F9",
            0x79 => "F10",
            0x7A => "F11",
            0x7B => "F12",
            _ => $"VK_0x{vk:X2}"
        };
    }
}
