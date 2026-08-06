using System.Runtime.InteropServices;

namespace Roost.Interop;

// ---------------------------------------------------------------------------
// Documented, stable API. Shipped in shell32 and covered by MSDN. Safe to call.
// ---------------------------------------------------------------------------

[ComImport]
[Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IVirtualDesktopManager
{
    // PreserveSig throughout so failures surface as HRESULTs we can log, rather
    // than exceptions that lose the code.
    [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out int onCurrentDesktop);
    [PreserveSig] int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);
    [PreserveSig] int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
}

// ---------------------------------------------------------------------------
// Undocumented territory. Declared only far enough to *probe* for the interface;
// no methods are declared, because the vtable layout differs per Windows build
// and calling a misordered slot silently corrupts rather than throwing.
// ---------------------------------------------------------------------------

[ComImport]
[Guid("6d5140c1-7436-11ce-8034-00aa006009fa")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IServiceProvider10
{
    [PreserveSig] int QueryService(ref Guid service, ref Guid riid, out IntPtr ppvObject);
}

internal static class Clsid
{
    internal static readonly Guid VirtualDesktopManager = new("aa509086-5ca9-4c25-8f95-589d3c07b48a");
    internal static readonly Guid ImmersiveShell = new("c2f03a33-21f5-47fa-b4bb-156362a2f239");
    internal static readonly Guid VirtualDesktopManagerInternal = new("c5e0cdca-7b6e-41b2-9fc4-d93975cc467b");
    internal static readonly Guid VirtualDesktopPinnedApps = new("b5a399e7-1c87-46b8-88e9-fc5747b171bd");
    internal static readonly Guid ApplicationViewCollection = new("1841c6d7-4f9d-42c0-af41-8747538f10e5");
}

/// <summary>
/// Result of asking the shell whether it supports a given undocumented interface.
/// </summary>
public sealed record ProbeResult(string Interface, string Label, Guid Iid, int HResult)
{
    public bool Supported => HResult == 0;
}

/// <summary>
/// Discovers which undocumented virtual-desktop interface IDs this Windows build
/// accepts, by asking the ImmersiveShell service provider for each candidate.
///
/// This is the "probe, don't look up by build number" strategy from docs/NOTES.md.
/// A QueryService call that returns a failure HRESULT is harmless, so trying a list
/// of candidates costs nothing and does not require knowing the build in advance.
/// </summary>
public static class ShellServiceProbe
{
    // Candidate IIDs published by the open-source virtual-desktop projects, oldest
    // first. Labels are the builds they were observed on, which is informational
    // only: we care which one answers, not which one "should".
    private static readonly (string Label, string Iid)[] ManagerInternalCandidates =
    [
        ("Win10 1607-1809",   "f31574d6-b682-4cdc-bd56-1827860abec6"),
        ("Win10 1903-21H2",   "0f3a72b0-4566-487e-9a33-4ed302f6d6ce"),
        ("Win11 21H2",        "b2f925b9-5a0f-4d2e-9f4d-2b1507593c10"),
        ("Win11 22H2/23H2",   "a3175f2d-239c-4bd2-8aa0-eeba8b0b138e"),
        ("Win11 24H2 (a)",    "53f5ca0b-158f-4124-900c-057158060b27"),
        ("Win11 24H2 (b)",    "4970ba3d-fd4e-4647-bea3-d89076ef4b9c"),
        ("Win11 24H2 (c)",    "094afe11-44f2-4ba0-976f-29a97e263ee0"),
    ];

    private static readonly (string Label, string Iid)[] PinnedAppsCandidates =
    [
        ("common",            "4ce81583-1e4c-4632-a621-07a53543148f"),
    ];

    private static readonly (string Label, string Iid)[] ViewCollectionCandidates =
    [
        ("Win10",             "2c08adf0-a386-4b35-9250-0fe183476fcc"),
        ("Win10 1809+",       "1841c6d7-4f9d-42c0-af41-8747538f10e5"),
    ];

    public static List<ProbeResult> ProbeAll()
    {
        var results = new List<ProbeResult>();

        var shellType = Type.GetTypeFromCLSID(Clsid.ImmersiveShell, throwOnError: false);
        if (shellType is null)
            return results;

        object? shell = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            if (shell is not IServiceProvider10 provider)
                return results;

            Probe(provider, results, "IVirtualDesktopManagerInternal",
                Clsid.VirtualDesktopManagerInternal, ManagerInternalCandidates);

            Probe(provider, results, "IVirtualDesktopPinnedApps",
                Clsid.VirtualDesktopPinnedApps, PinnedAppsCandidates);

            Probe(provider, results, "IApplicationViewCollection",
                Clsid.ApplicationViewCollection, ViewCollectionCandidates);
        }
        finally
        {
            if (shell is not null && Marshal.IsComObject(shell))
                Marshal.ReleaseComObject(shell);
        }

        return results;
    }

    private static void Probe(
        IServiceProvider10 provider,
        List<ProbeResult> results,
        string interfaceName,
        Guid serviceClsid,
        (string Label, string Iid)[] candidates)
    {
        foreach (var (label, iidText) in candidates)
        {
            var service = serviceClsid;
            var iid = new Guid(iidText);

            var hr = provider.QueryService(ref service, ref iid, out var ptr);

            // Release immediately. We only want to know whether the shell recognises
            // the IID; we never call through the pointer, because the method layout
            // varies per build and a wrong slot is not a catchable error.
            if (hr == 0 && ptr != IntPtr.Zero)
                Marshal.Release(ptr);

            results.Add(new ProbeResult(interfaceName, label, iid, hr));
        }
    }
}
