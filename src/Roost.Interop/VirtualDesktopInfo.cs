namespace Roost.Interop;

/// <summary>
/// One virtual desktop, as enumerated from the registry.
/// </summary>
/// <param name="Index">
/// Zero-based position in the desktop order. Positional only: it changes whenever
/// desktops are reordered or removed, so never persist it. Persist <see cref="Id"/>.
/// </param>
/// <param name="Id">Stable GUID. This is the identity used for every operation.</param>
/// <param name="Name">
/// User-assigned name, or null when the desktop has never been renamed.
/// </param>
public sealed record VirtualDesktopInfo(int Index, Guid Id, string? Name)
{
    /// <summary>
    /// What to show in the palette. Unnamed desktops fall back to the same label
    /// Windows itself uses, which is 1-based.
    /// </summary>
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Name) ? $"Desktop {Index + 1}" : Name;

    /// <summary>True when the desktop has no user-assigned name.</summary>
    public bool IsUnnamed => string.IsNullOrWhiteSpace(Name);
}
