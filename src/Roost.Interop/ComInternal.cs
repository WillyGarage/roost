using System.Runtime.InteropServices;

namespace Roost.Interop;

// ---------------------------------------------------------------------------
// Undocumented shell interfaces.
//
// These are VTABLE-ORDER DEPENDENT. Every method must appear in exactly the order
// the shell declares it, including methods we never call, because the CLR computes
// the slot offset by counting declarations. A missing or reordered method does not
// throw: it calls the wrong function pointer. Do not "tidy" these declarations.
//
// Layout below matches Windows 11 24H2 / 25H2 (build 26100+), cross-checked against
// MScholtes/VirtualDesktop VirtualDesktop11-24H2.cs v1.21. The distinguishing
// feature of this generation is SwitchDesktopAndMoveForegroundView occupying slot 8,
// which shifts everything after it relative to 22H2.
//
// Every method is [PreserveSig] with explicit out-parameters. That is deliberate:
// it makes the managed signature exactly match the native one, and it surfaces the
// HRESULT instead of throwing, so failures land in the log with a code.
// BOOLs are declared as int (native BOOL is 4 bytes; C# bool would marshal as a
// 2-byte VARIANT_BOOL in a COM interface).
// ---------------------------------------------------------------------------

[ComImport]
[Guid("92ca9dcd-5622-4bba-a805-5e9f541bd8c9")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IObjectArray
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetAt(int index, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object obj);
}

/// <summary>
/// Identity only. We obtain these from GetViewForHwnd and hand them straight back to
/// MoveViewToDesktop without ever calling a method, so declaring the (very long and
/// build-variable) method list would add risk for no benefit. An empty ComImport
/// interface still marshals correctly as an IUnknown-derived pointer.
/// </summary>
[ComImport]
[Guid("372e1d3b-38d3-42e4-a15b-8ab2b178f513")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IApplicationView
{
}

[ComImport]
[Guid("1841c6d7-4f9d-42c0-af41-8747538f10e5")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IApplicationViewCollection
{
    [PreserveSig] int GetViews(out IObjectArray array);
    [PreserveSig] int GetViewsByZOrder(out IObjectArray array);
    [PreserveSig] int GetViewsByAppUserModelId([MarshalAs(UnmanagedType.LPWStr)] string id, out IObjectArray array);

    /// <summary>Slot 4. The bridge from an HWND to something the shell will move.</summary>
    [PreserveSig] int GetViewForHwnd(IntPtr hwnd, out IApplicationView view);

    [PreserveSig] int GetViewForApplication([MarshalAs(UnmanagedType.IUnknown)] object application, out IApplicationView view);
    [PreserveSig] int GetViewForAppUserModelId([MarshalAs(UnmanagedType.LPWStr)] string id, out IApplicationView view);
    [PreserveSig] int GetViewInFocus(out IntPtr view);
    [PreserveSig] int Unknown1(out IntPtr view);
    [PreserveSig] int RefreshCollection();
    [PreserveSig] int RegisterForApplicationViewChanges([MarshalAs(UnmanagedType.IUnknown)] object listener, out int cookie);
    [PreserveSig] int UnregisterForApplicationViewChanges(int cookie);
}

[ComImport]
[Guid("3f07f4be-b107-441a-af0f-39d82529072c")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IVirtualDesktop
{
    [PreserveSig] int IsViewVisible(IApplicationView view, out int visible);
    [PreserveSig] int GetId(out Guid id);

    // Remaining slots (GetName, GetWallpaperPath, IsRemote) are intentionally not
    // declared. GetName returns an HSTRING, and built-in HSTRING marshalling was
    // removed in .NET 5, so it would need manual combase.dll calls. We read names
    // from the registry instead, which needs no COM at all.
}

[ComImport]
[Guid("53f5ca0b-158f-4124-900c-057158060b27")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IVirtualDesktopManagerInternal
{
    [PreserveSig] int GetCount(out int count);                                              // 1

    /// <summary>Slot 2. The operation the documented API refuses to do cross-process.</summary>
    [PreserveSig] int MoveViewToDesktop(IApplicationView view, IVirtualDesktop desktop);    // 2

    [PreserveSig] int CanViewMoveDesktops(IApplicationView view, out int can);              // 3
    [PreserveSig] int GetCurrentDesktop(out IVirtualDesktop desktop);                       // 4
    [PreserveSig] int GetDesktops(out IObjectArray desktops);                               // 5
    [PreserveSig] int GetAdjacentDesktop(IVirtualDesktop from, int direction, out IVirtualDesktop desktop); // 6
    [PreserveSig] int SwitchDesktop(IVirtualDesktop desktop);                               // 7

    /// <summary>Slot 8. Present from 24H2 onward; absent on 22H2 and earlier.</summary>
    [PreserveSig] int SwitchDesktopAndMoveForegroundView(IVirtualDesktop desktop);          // 8

    [PreserveSig] int CreateDesktop(out IVirtualDesktop desktop);                           // 9

    /// <summary>Slot 10. Reordering, i.e. "insert the new desktop after this one".</summary>
    [PreserveSig] int MoveDesktop(IVirtualDesktop desktop, int index);                      // 10

    [PreserveSig] int RemoveDesktop(IVirtualDesktop desktop, IVirtualDesktop fallback);     // 11

    /// <summary>Slot 12. GUID to IVirtualDesktop, so we never have to hold live objects.</summary>
    [PreserveSig] int FindDesktop(ref Guid desktopId, out IVirtualDesktop desktop);         // 12

    [PreserveSig] int GetDesktopSwitchIncludeExcludeViews(IVirtualDesktop desktop, out IObjectArray unknown1, out IObjectArray unknown2); // 13

    /// <summary>Slot 14. Second parameter is an HSTRING, hence IntPtr.</summary>
    [PreserveSig] int SetDesktopName(IVirtualDesktop desktop, IntPtr nameHString);          // 14

    // Slots beyond this point (SetDesktopWallpaper, UpdateWallpaperPathForAllDesktops,
    // CopyDesktopState, CreateRemoteDesktop, SwitchRemoteDesktop,
    // SwitchDesktopWithAnimation, GetLastActiveDesktop, WaitForAnimationToComplete)
    // are not declared because nothing here calls them. Adding one means appending
    // in the shell's own order, never inserting.
}
