using System.Diagnostics;

namespace ProdToy;

/// <summary>
/// Manages a system-wide global hotkey via a hidden message window.
/// </summary>
class GlobalHotkey : IDisposable
{
    private readonly HotkeyWindow _window;
    private bool _registered;

    public event Action? HotkeyPressed;

    public GlobalHotkey()
    {
        _window = new HotkeyWindow();
        _window.HotkeyPressed += () => HotkeyPressed?.Invoke();
    }

    public bool Register(string hotkeyString)
    {
        Unregister();

        if (string.IsNullOrWhiteSpace(hotkeyString))
            return false;

        if (!TryParse(hotkeyString, out uint modifiers, out uint vk))
            return false;

        _registered = NativeMethods.RegisterHotKey(
            _window.Handle,
            NativeMethods.HOTKEY_ID_DEFAULT,
            modifiers | NativeMethods.MOD_NOREPEAT,
            vk);

        if (!_registered)
            Debug.WriteLine($"Failed to register hotkey: {hotkeyString}");

        return _registered;
    }

    public void Unregister()
    {
        if (_registered)
        {
            NativeMethods.UnregisterHotKey(_window.Handle, NativeMethods.HOTKEY_ID_DEFAULT);
            _registered = false;
        }
    }

    public void Dispose()
    {
        Unregister();
        _window.DestroyHandle();
    }

    internal static bool TryParse(string hotkey, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;

        if (string.IsNullOrWhiteSpace(hotkey))
            return false;

        bool vkSet = false;
        foreach (var part in hotkey.Split('+'))
        {
            string p = part.Trim();
            switch (p.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= NativeMethods.MOD_CONTROL;
                    break;
                case "ALT":
                    modifiers |= NativeMethods.MOD_ALT;
                    break;
                case "SHIFT":
                    modifiers |= NativeMethods.MOD_SHIFT;
                    break;
                case "WIN":
                    modifiers |= NativeMethods.MOD_WIN;
                    break;
                default:
                    if (vkSet) return false; // two non-modifier tokens
                    if (!Enum.TryParse<Keys>(p, true, out var key)) return false;
                    vk = (uint)key;
                    vkSet = true;
                    break;
            }
        }

        // Windows allows modifier-less hotkeys (e.g. PrintScreen, F13+).
        return vkSet;
    }

    private class HotkeyWindow : NativeWindow
    {
        public event Action? HotkeyPressed;

        public HotkeyWindow()
        {
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_HOTKEY &&
                m.WParam.ToInt32() == NativeMethods.HOTKEY_ID_DEFAULT)
            {
                HotkeyPressed?.Invoke();
            }
            base.WndProc(ref m);
        }
    }
}
