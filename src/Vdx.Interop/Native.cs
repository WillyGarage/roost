using System.Runtime.InteropServices;
using System.Text;

namespace Vdx.Interop;

/// <summary>
/// Raw Win32 P/Invoke. Nothing virtual-desktop specific lives here.
/// </summary>
public static class Native
{
    // ---- windows -----------------------------------------------------------

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetShellWindow();

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder text, int maxCount);

    public static string GetWindowTitle(IntPtr hWnd)
    {
        var sb = new StringBuilder(512);
        var n = GetWindowTextW(hWnd, sb, sb.Capacity);
        return n > 0 ? sb.ToString() : string.Empty;
    }

    public static string GetWindowClass(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        var n = GetClassNameW(hWnd, sb, sb.Capacity);
        return n > 0 ? sb.ToString() : string.Empty;
    }

    /// <summary>
    /// Visible top-level windows with a title, excluding the shell window itself.
    /// Deliberately not filtering further: callers decide what counts.
    /// </summary>
    public static List<IntPtr> GetTopLevelWindows()
    {
        var shell = GetShellWindow();
        var result = new List<IntPtr>();

        EnumWindows((hWnd, _) =>
        {
            if (hWnd != shell && IsWindowVisible(hWnd) && GetWindowTitle(hWnd).Length > 0)
                result.Add(hWnd);
            return true;
        }, IntPtr.Zero);

        return result;
    }

    // ---- synthetic keystrokes ---------------------------------------------
    //
    // Used for the operations that have no stable API: creating and closing
    // desktops, and switching as a fallback when COM SwitchDesktop is unavailable.

    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_LWIN = 0x5B;
    public const ushort VK_LEFT = 0x25;
    public const ushort VK_RIGHT = 0x27;
    public const ushort VK_D = 0x44;
    public const ushort VK_F4 = 0x73;

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL, wParamH;
    }

    // Explicit union so the struct size matches native INPUT (40 bytes on x64).
    // MOUSEINPUT is the largest member and therefore sets the size.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 } }
    };

    /// <summary>
    /// Presses modifiers + key, then releases in reverse order, in a single
    /// SendInput batch so nothing can interleave.
    /// </summary>
    public static void SendChord(ushort key, params ushort[] modifiers)
    {
        var inputs = new List<INPUT>();

        foreach (var m in modifiers)
            inputs.Add(Key(m, up: false));

        inputs.Add(Key(key, up: false));
        inputs.Add(Key(key, up: true));

        for (var i = modifiers.Length - 1; i >= 0; i--)
            inputs.Add(Key(modifiers[i], up: true));

        var arr = inputs.ToArray();
        var sent = SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());

        if (sent != arr.Length)
            throw new InvalidOperationException(
                $"SendInput sent {sent}/{arr.Length} events, last error {Marshal.GetLastWin32Error()}");
    }
}
