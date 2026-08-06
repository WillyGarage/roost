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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const uint GW_OWNER = 4;

    /// <summary>DWMWA_CLOAKED. Non-zero means the window is hidden by the shell.</summary>
    private const int DWMWA_CLOAKED = 14;

    /// <summary>
    /// Structural test for "a window the user would recognise as theirs": visible, titled,
    /// top-level, not a tool window, not owned by another window, not the shell.
    ///
    /// Deliberately does NOT consider DWM cloaking. Cloaking cannot be judged without
    /// knowing which desktop the window is on, because Windows cloaks every window that
    /// sits on a virtual desktop other than the current one. Filtering on cloaked alone
    /// makes every other desktop look empty. See <see cref="IsCloaked"/>.
    /// </summary>
    public static bool IsUserWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || hWnd == GetShellWindow())
            return false;

        if (!IsWindowVisible(hWnd))
            return false;

        if (GetWindowTitle(hWnd).Length == 0)
            return false;

        // Tool windows are palettes and helpers; they never appear in Alt-Tab.
        var exStyle = GetWindowLongPtrW(hWnd, GWL_EXSTYLE).ToInt64();
        if ((exStyle & WS_EX_TOOLWINDOW) != 0)
            return false;

        // Owned windows are dialogs and popups belonging to another window. Counting both
        // would double-count one application window the user sees as one thing.
        if (GetWindow(hWnd, GW_OWNER) != IntPtr.Zero)
            return false;

        return true;
    }

    /// <summary>
    /// Whether DWM is hiding this window, and why (0 when it is not).
    ///
    /// The value is a bit field: 1 the app cloaked itself, 2 the shell cloaked it,
    /// 4 inherited from an owner. The shell bit is doing double duty, which is the trap:
    /// it means both "this window is on another virtual desktop" (a perfectly real window)
    /// and "this is a suspended UWP app" (a ghost that should not be counted).
    ///
    /// The usable rule is therefore positional, not absolute: a window that is cloaked
    /// while sitting on the CURRENT desktop is a ghost, because a real window there would
    /// be on screen. A cloaked window on any other desktop is just off-screen.
    ///
    /// A failed DWM call reports 0, so an unexpected error cannot silently empty the list.
    /// </summary>
    public static int IsCloaked(IntPtr hWnd) =>
        DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 ? cloaked : 0;

    /// <summary>
    /// Top-level windows the user would recognise as theirs, front to back.
    /// </summary>
    public static List<IntPtr> GetUserWindows()
    {
        var result = new List<IntPtr>();

        EnumWindows((hWnd, _) =>
        {
            if (IsUserWindow(hWnd))
                result.Add(hWnd);
            return true;
        }, IntPtr.Zero);

        return result;
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
