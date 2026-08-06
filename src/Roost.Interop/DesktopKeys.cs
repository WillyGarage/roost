namespace Roost.Interop;

/// <summary>
/// Desktop create / close / switch driven by the shell's own global hotkeys.
///
/// These operations have no documented API. Synthesising the keystroke Windows
/// itself binds is the version-proof way to do them: it works on every build,
/// needs no interface IDs, and cannot break when Microsoft reshuffles a vtable.
/// The cost is the shell's switch animation, and that creation always lands the
/// new desktop at the far right.
/// </summary>
public static class DesktopKeys
{
    /// <summary>
    /// Win+Ctrl+D. Creates a desktop at the far right AND switches to it.
    /// Returns the new desktop's GUID, discovered by diffing the registry list,
    /// or null if it did not appear within the timeout.
    /// </summary>
    public static Guid? CreateDesktopAndSwitch(int timeoutMs = 4000)
    {
        var before = DesktopRegistry.List().Select(d => d.Id).ToHashSet();

        Native.SendChord(Native.VK_D, Native.VK_LWIN, Native.VK_CONTROL);

        // Explorer persists the new desktop to the registry asynchronously, so poll
        // rather than assuming it is there on the next line.
        return WaitForNewDesktop(before, timeoutMs);
    }

    /// <summary>Win+Ctrl+F4. Closes the desktop currently being viewed.</summary>
    public static void CloseCurrentDesktop() =>
        Native.SendChord(Native.VK_F4, Native.VK_LWIN, Native.VK_CONTROL);

    /// <summary>Win+Ctrl+Left.</summary>
    public static void SwitchLeft() =>
        Native.SendChord(Native.VK_LEFT, Native.VK_LWIN, Native.VK_CONTROL);

    /// <summary>Win+Ctrl+Right.</summary>
    public static void SwitchRight() =>
        Native.SendChord(Native.VK_RIGHT, Native.VK_LWIN, Native.VK_CONTROL);

    /// <summary>
    /// Waits for a desktop GUID to appear that was not in <paramref name="before"/>.
    /// </summary>
    private static Guid? WaitForNewDesktop(HashSet<Guid> before, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            var added = DesktopRegistry.List().FirstOrDefault(d => !before.Contains(d.Id));
            if (added is not null)
                return added.Id;

            Thread.Sleep(100);
        }

        return null;
    }

    /// <summary>
    /// Walks left until <paramref name="anchor"/> is on the current desktop, i.e.
    /// until we are back where that window lives.
    ///
    /// Using a window we know the location of as the reference point avoids trusting
    /// the lazily-written CurrentVirtualDesktop registry value, and avoids counting
    /// indexes that may have shifted underneath us.
    /// </summary>
    public static bool ReturnToWindowsDesktop(
        VirtualDesktops vd, IntPtr anchor, int maxSteps = 40, int settleMs = 120)
    {
        for (var i = 0; i < maxSteps; i++)
        {
            if (vd.TryIsOnCurrentDesktop(anchor, out var onCurrent).Ok && onCurrent)
                return true;

            SwitchLeft();
            Thread.Sleep(settleMs);
        }

        return vd.TryIsOnCurrentDesktop(anchor, out var final).Ok && final;
    }
}
