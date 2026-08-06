using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Roost.App;

/// <summary>What a registered hotkey should do.</summary>
public enum HotkeyAction
{
    MoveWindow,
    SwitchDesktop,
    SendToLastCreated
}

/// <summary>
/// Global hotkeys via RegisterHotKey, delivered to a message-only window.
///
/// RegisterHotKey rather than a low-level keyboard hook on purpose: it does not see
/// every keystroke you type, so it cannot leak input, cannot slow the system down, and
/// cannot swallow a key it did not claim. The tradeoff is that it only handles keyboard
/// chords, which is why the mouse side button needs a different approach (map a spare
/// button to a chord in the mouse's own software).
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;

    /// <summary>Stops auto-repeat firing the palette dozens of times if a key sticks.</summary>
    private const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly HwndSource _source;
    private readonly Dictionary<int, HotkeyAction> _registered = [];
    private int _nextId = 1;

    /// <summary>Raised on the UI thread when a registered chord is pressed.</summary>
    public event Action<HotkeyAction>? Pressed;

    public HotkeyService()
    {
        // A message-only window: never shown, exists purely to receive WM_HOTKEY.
        _source = new HwndSource(new HwndSourceParameters("Roost.HotkeySink")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3) // HWND_MESSAGE
        });

        _source.AddHook(WndProc);
    }

    /// <summary>
    /// Registers a chord such as "Win+Ctrl+M". Returns an error string on failure, or
    /// null on success. Failure is normal and expected when another app already owns
    /// the chord, so callers should report it rather than throw.
    /// </summary>
    public string? Register(string chord, HotkeyAction action)
    {
        if (string.IsNullOrWhiteSpace(chord))
        {
            Log.Info($"{action}: no hotkey configured, skipping");
            return null;
        }

        if (!TryParse(chord, out var modifiers, out var vk, out var parseError))
            return $"\"{chord}\" is not a valid hotkey: {parseError}";

        var id = _nextId++;

        if (!RegisterHotKey(_source.Handle, id, modifiers | MOD_NOREPEAT, vk))
        {
            var err = Marshal.GetLastWin32Error();
            var reason = err == 1409
                ? "already registered by another application"
                : $"error {err}";

            Log.Error($"{action}: could not register {chord} ({reason})");
            return $"{chord} could not be registered: {reason}";
        }

        _registered[id] = action;
        Log.Info($"{action}: registered {chord}");
        return null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY || !_registered.TryGetValue(wParam.ToInt32(), out var action))
            return IntPtr.Zero;

        handled = true;

        try
        {
            Pressed?.Invoke(action);
        }
        catch (Exception ex)
        {
            // An exception escaping a window procedure would tear down the app.
            Log.Error($"unhandled error handling hotkey {action}", ex);
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Parses "Win+Ctrl+M" into RegisterHotKey modifiers and a virtual-key code.
    /// Accepts single letters, digits, F1-F24, and a few named keys.
    /// </summary>
    private static bool TryParse(string chord, out uint modifiers, out uint vk, out string error)
    {
        modifiers = 0;
        vk = 0;
        error = "";

        var parts = chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            error = "empty";
            return false;
        }

        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            var isLast = i == parts.Length - 1;

            switch (part.ToLowerInvariant())
            {
                case "win" or "windows": modifiers |= MOD_WIN; continue;
                case "ctrl" or "control": modifiers |= MOD_CONTROL; continue;
                case "alt": modifiers |= MOD_ALT; continue;
                case "shift": modifiers |= MOD_SHIFT; continue;
            }

            if (!isLast)
            {
                error = $"unknown modifier \"{part}\"";
                return false;
            }

            if (!TryParseKey(part, out vk))
            {
                error = $"unknown key \"{part}\"";
                return false;
            }
        }

        if (vk == 0)
        {
            error = "no key, only modifiers";
            return false;
        }

        if (modifiers == 0)
        {
            // A bare key as a global hotkey would hijack normal typing.
            error = "at least one modifier is required";
            return false;
        }

        return true;
    }

    private static bool TryParseKey(string key, out uint vk)
    {
        vk = 0;

        if (key.Length == 1)
        {
            var c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                vk = c;
                return true;
            }
        }

        if (key.Length > 1 && char.ToUpperInvariant(key[0]) == 'F'
            && int.TryParse(key[1..], out var fn) && fn is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + fn - 1); // VK_F1 = 0x70
            return true;
        }

        vk = key.ToLowerInvariant() switch
        {
            "space" => 0x20,
            "enter" or "return" => 0x0D,
            "tab" => 0x09,
            "left" => 0x25,
            "up" => 0x26,
            "right" => 0x27,
            "down" => 0x28,
            "insert" => 0x2D,
            "delete" => 0x2E,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" => 0x21,
            "pagedown" => 0x22,
            "oem3" or "backtick" or "tilde" => 0xC0,
            _ => 0
        };

        return vk != 0;
    }

    public void Dispose()
    {
        foreach (var id in _registered.Keys)
            UnregisterHotKey(_source.Handle, id);

        _registered.Clear();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
