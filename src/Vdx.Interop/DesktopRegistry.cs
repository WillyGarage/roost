using Microsoft.Win32;

namespace Vdx.Interop;

/// <summary>
/// Enumerates and names virtual desktops by reading the registry directly.
///
/// This is the "stable tier" of the design (see docs/NOTES.md): it uses no COM at
/// all, so it does not break when Windows updates change the undocumented
/// virtual-desktop interfaces. Listing desktops, their order, and their names is
/// the bulk of what the palette needs, and all of it lives here.
/// </summary>
public static class DesktopRegistry
{
    private const string RootPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";

    private const string DesktopsPath = RootPath + @"\Desktops";

    /// <summary>REG_BINARY: packed array of 16-byte GUIDs, in display order.</summary>
    private const string IdsValue = "VirtualDesktopIDs";

    /// <summary>REG_BINARY: 16-byte GUID. Written lazily, so treat as a hint only.</summary>
    private const string CurrentValue = "CurrentVirtualDesktop";

    private const string NameValue = "Name";

    private const int GuidSize = 16;

    /// <summary>
    /// All virtual desktops in the order Windows displays them, left to right.
    /// Returns an empty list if the registry layout is missing or malformed rather
    /// than throwing, so the caller can fall back and surface a clear error.
    /// </summary>
    public static IReadOnlyList<VirtualDesktopInfo> List()
    {
        using var root = Registry.CurrentUser.OpenSubKey(RootPath);
        if (root?.GetValue(IdsValue) is not byte[] blob)
            return [];

        // A trailing partial GUID would mean the blob is corrupt; ignore the remainder
        // instead of throwing, since a partial list is still usable.
        var count = blob.Length / GuidSize;
        if (count == 0)
            return [];

        var names = ReadNames();
        var result = new List<VirtualDesktopInfo>(count);

        for (var i = 0; i < count; i++)
        {
            // The blob stores GUIDs in native Windows struct layout, which is exactly
            // what the Guid(ReadOnlySpan<byte>) constructor expects. No byte swapping.
            var id = new Guid(blob.AsSpan(i * GuidSize, GuidSize));
            names.TryGetValue(id, out var name);
            result.Add(new VirtualDesktopInfo(i, id, name));
        }

        return result;
    }

    /// <summary>
    /// GUID → user-assigned name, for every desktop that has one. Desktops appear
    /// under Desktops\{GUID} only once they have been named, so this map is usually
    /// smaller than the desktop list.
    /// </summary>
    private static Dictionary<Guid, string> ReadNames()
    {
        var map = new Dictionary<Guid, string>();

        using var desktops = Registry.CurrentUser.OpenSubKey(DesktopsPath);
        if (desktops is null)
            return map;

        foreach (var subKeyName in desktops.GetSubKeyNames())
        {
            // Subkey names are braced GUIDs, e.g. {3C0A9D0E-...}.
            if (!Guid.TryParse(subKeyName, out var id))
                continue;

            using var key = desktops.OpenSubKey(subKeyName);
            if (key?.GetValue(NameValue) is string name && !string.IsNullOrWhiteSpace(name))
                map[id] = name;
        }

        return map;
    }

    /// <summary>
    /// Best-effort read of the current desktop from the registry.
    ///
    /// Explorer writes this value lazily, so it can lag behind the desktop the user
    /// is actually looking at. Do not rely on it for the palette's "current" marker.
    /// The authoritative and fully documented way is
    /// IVirtualDesktopManager.GetWindowDesktopId() on the foreground HWND captured at
    /// hotkey time, since that window is by definition on the current desktop.
    /// </summary>
    public static Guid? GetCurrentHint()
    {
        using var root = Registry.CurrentUser.OpenSubKey(RootPath);
        if (root?.GetValue(CurrentValue) is byte[] { Length: GuidSize } blob)
            return new Guid(blob);

        // Some builds keep it per-session instead.
        using var sessions = Registry.CurrentUser.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SessionInfo");

        if (sessions is null)
            return null;

        foreach (var session in sessions.GetSubKeyNames())
        {
            using var key = sessions.OpenSubKey($@"{session}\VirtualDesktops");
            if (key?.GetValue(CurrentValue) is byte[] { Length: GuidSize } sessionBlob)
                return new Guid(sessionBlob);
        }

        return null;
    }

    /// <summary>
    /// Writes a desktop's name straight to the registry.
    ///
    /// Open question Q3 in docs/NOTES.md: whether this alone makes Task View show the
    /// new name, or whether the undocumented IVirtualDesktop.SetName is also needed to
    /// force a UI refresh. If the registry write is sufficient, renaming stays on the
    /// stable tier. Not called by anything yet.
    /// </summary>
    public static void SetName(Guid id, string name)
    {
        // Desktops\{GUID} may not exist yet for a never-named desktop; CreateSubKey
        // handles both cases. Braced uppercase matches Explorer's own formatting
        // (registry lookups are case-insensitive, so this is cosmetic).
        var path = $@"{DesktopsPath}\{id.ToString("B").ToUpperInvariant()}";

        using var key = Registry.CurrentUser.CreateSubKey(path, writable: true)
            ?? throw new InvalidOperationException($"Could not open or create {path}");

        key.SetValue(NameValue, name, RegistryValueKind.String);
    }
}
