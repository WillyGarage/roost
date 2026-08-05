using System.Runtime.InteropServices;

namespace Vdx.Interop;

/// <summary>
/// Virtual desktop operations that require the undocumented shell interfaces.
///
/// Needed because the documented IVirtualDesktopManager.MoveWindowToDesktop returns
/// E_ACCESSDENIED for windows owned by other processes, which is every window this
/// tool exists to move. See docs/NOTES.md, finding Q1.
///
/// Construction resolves the interfaces by probing IIDs rather than by looking up the
/// Windows build number, so a build we have never seen still works as long as it
/// shares an IID with one we know. <see cref="Available"/> is false when nothing
/// matched, and callers are expected to degrade rather than crash.
/// </summary>
public sealed class VirtualDesktopsInternal : IDisposable
{
    private object? _shell;
    private IVirtualDesktopManagerInternal? _manager;
    private IApplicationViewCollection? _views;

    /// <summary>False when this Windows build exposes none of the IIDs we know.</summary>
    public bool Available => _manager is not null && _views is not null;

    /// <summary>Why <see cref="Available"/> is false, for the log.</summary>
    public string? UnavailableReason { get; private set; }

    public VirtualDesktopsInternal()
    {
        var shellType = Type.GetTypeFromCLSID(Clsid.ImmersiveShell, throwOnError: false);
        if (shellType is null)
        {
            UnavailableReason = "CLSID_ImmersiveShell is not registered";
            return;
        }

        _shell = Activator.CreateInstance(shellType);
        if (_shell is not IServiceProvider10 provider)
        {
            UnavailableReason = "ImmersiveShell does not expose IServiceProvider";
            return;
        }

        _manager = QueryService<IVirtualDesktopManagerInternal>(
            provider, Clsid.VirtualDesktopManagerInternal, typeof(IVirtualDesktopManagerInternal).GUID);

        if (_manager is null)
        {
            UnavailableReason =
                $"IVirtualDesktopManagerInternal {typeof(IVirtualDesktopManagerInternal).GUID:B} " +
                "not supported on this build";
            return;
        }

        _views = QueryService<IApplicationViewCollection>(
            provider, Clsid.ApplicationViewCollection, typeof(IApplicationViewCollection).GUID);

        if (_views is null)
            UnavailableReason =
                $"IApplicationViewCollection {typeof(IApplicationViewCollection).GUID:B} " +
                "not supported on this build";
    }

    private static T? QueryService<T>(IServiceProvider10 provider, Guid service, Guid iid)
        where T : class
    {
        var svc = service;
        var id = iid;

        if (provider.QueryService(ref svc, ref id, out var ptr) != 0 || ptr == IntPtr.Zero)
            return null;

        try
        {
            // QueryService already returned the pointer for this exact IID, so the
            // cast is a formality; it is here so the CLR builds a typed RCW.
            return Marshal.GetObjectForIUnknown(ptr) as T;
        }
        finally
        {
            // GetObjectForIUnknown took its own reference.
            Marshal.Release(ptr);
        }
    }

    /// <summary>Number of desktops, according to the shell rather than the registry.</summary>
    public OpResult TryGetCount(out int count)
    {
        count = 0;
        if (_manager is null) return OpResult.Fail(-1, UnavailableReason);

        var hr = _manager.GetCount(out count);
        return hr == 0 ? OpResult.Success : OpResult.Fail(hr, "GetCount");
    }

    /// <summary>
    /// The desktop currently being viewed, straight from the shell. More reliable than
    /// the lazily-written registry hint, and does not require having a window to ask
    /// about the way the documented API does.
    /// </summary>
    public OpResult TryGetCurrentDesktop(out Guid desktopId)
    {
        desktopId = Guid.Empty;
        if (_manager is null) return OpResult.Fail(-1, UnavailableReason);

        var hr = _manager.GetCurrentDesktop(out var desktop);
        if (hr != 0 || desktop is null)
            return OpResult.Fail(hr, "GetCurrentDesktop");

        hr = desktop.GetId(out desktopId);
        return hr == 0 ? OpResult.Success : OpResult.Fail(hr, "GetId");
    }

    /// <summary>
    /// Moves any top-level window to a desktop. This is the tool's core operation.
    /// </summary>
    public OpResult TryMoveWindow(IntPtr hWnd, Guid desktopId)
    {
        if (_manager is null || _views is null) return OpResult.Fail(-1, UnavailableReason);

        var hr = _views.GetViewForHwnd(hWnd, out var view);
        if (hr != 0 || view is null)
            return OpResult.Fail(hr, "GetViewForHwnd");

        var id = desktopId;
        hr = _manager.FindDesktop(ref id, out var desktop);
        if (hr != 0 || desktop is null)
            return OpResult.Fail(hr, "FindDesktop");

        hr = _manager.MoveViewToDesktop(view, desktop);
        return hr == 0 ? OpResult.Success : OpResult.Fail(hr, "MoveViewToDesktop");
    }

    /// <summary>
    /// Whether the shell considers this window movable between desktops. Pinned
    /// windows ("show on all desktops") and some shell windows are not.
    /// </summary>
    public OpResult TryCanMoveWindow(IntPtr hWnd, out bool canMove)
    {
        canMove = false;
        if (_manager is null || _views is null) return OpResult.Fail(-1, UnavailableReason);

        var hr = _views.GetViewForHwnd(hWnd, out var view);
        if (hr != 0 || view is null)
            return OpResult.Fail(hr, "GetViewForHwnd");

        hr = _manager.CanViewMoveDesktops(view, out var can);
        canMove = can != 0;
        return hr == 0 ? OpResult.Success : OpResult.Fail(hr, "CanViewMoveDesktops");
    }

    /// <summary>Instant switch, no animation, no keystroke counting.</summary>
    public OpResult TrySwitchTo(Guid desktopId)
    {
        if (_manager is null) return OpResult.Fail(-1, UnavailableReason);

        var id = desktopId;
        var hr = _manager.FindDesktop(ref id, out var desktop);
        if (hr != 0 || desktop is null)
            return OpResult.Fail(hr, "FindDesktop");

        hr = _manager.SwitchDesktop(desktop);
        return hr == 0 ? OpResult.Success : OpResult.Fail(hr, "SwitchDesktop");
    }

    /// <summary>
    /// Creates a desktop without switching to it, unlike the Win+Ctrl+D keystroke.
    /// </summary>
    public OpResult TryCreateDesktop(out Guid desktopId)
    {
        desktopId = Guid.Empty;
        if (_manager is null) return OpResult.Fail(-1, UnavailableReason);

        var hr = _manager.CreateDesktop(out var desktop);
        if (hr != 0 || desktop is null)
            return OpResult.Fail(hr, "CreateDesktop");

        hr = desktop.GetId(out desktopId);
        return hr == 0 ? OpResult.Success : OpResult.Fail(hr, "GetId");
    }

    /// <summary>
    /// Moves a desktop to a new position in the order, which is what "insert the new
    /// desktop right after the one I am on" needs.
    /// </summary>
    public OpResult TryReorderDesktop(Guid desktopId, int newIndex)
    {
        if (_manager is null) return OpResult.Fail(-1, UnavailableReason);

        var id = desktopId;
        var hr = _manager.FindDesktop(ref id, out var desktop);
        if (hr != 0 || desktop is null)
            return OpResult.Fail(hr, "FindDesktop");

        hr = _manager.MoveDesktop(desktop, newIndex);
        return hr == 0 ? OpResult.Success : OpResult.Fail(hr, "MoveDesktop");
    }

    /// <summary>
    /// Removes a desktop, relocating its windows to <paramref name="fallbackId"/>.
    /// </summary>
    public OpResult TryRemoveDesktop(Guid desktopId, Guid fallbackId)
    {
        if (_manager is null) return OpResult.Fail(-1, UnavailableReason);

        var id = desktopId;
        var hr = _manager.FindDesktop(ref id, out var desktop);
        if (hr != 0 || desktop is null)
            return OpResult.Fail(hr, "FindDesktop(target)");

        var fb = fallbackId;
        hr = _manager.FindDesktop(ref fb, out var fallback);
        if (hr != 0 || fallback is null)
            return OpResult.Fail(hr, "FindDesktop(fallback)");

        hr = _manager.RemoveDesktop(desktop, fallback);
        return hr == 0 ? OpResult.Success : OpResult.Fail(hr, "RemoveDesktop");
    }

    /// <summary>
    /// Sets a desktop name through the shell, which also refreshes the Task View UI.
    /// Uses combase HSTRING directly: built-in HSTRING marshalling was removed in
    /// .NET 5, so the string is created and freed by hand.
    /// </summary>
    public OpResult TrySetName(Guid desktopId, string name)
    {
        if (_manager is null) return OpResult.Fail(-1, UnavailableReason);

        var id = desktopId;
        var hr = _manager.FindDesktop(ref id, out var desktop);
        if (hr != 0 || desktop is null)
            return OpResult.Fail(hr, "FindDesktop");

        var created = WindowsCreateString(name, name.Length, out var hstring);
        if (created != 0)
            return OpResult.Fail(created, "WindowsCreateString");

        try
        {
            hr = _manager.SetDesktopName(desktop, hstring);
            return hr == 0 ? OpResult.Success : OpResult.Fail(hr, "SetDesktopName");
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string src, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    public void Dispose()
    {
        Release(ref _views);
        Release(ref _manager);

        if (_shell is not null && Marshal.IsComObject(_shell))
            Marshal.ReleaseComObject(_shell);
        _shell = null;
    }

    private static void Release<T>(ref T? obj) where T : class
    {
        if (obj is not null && Marshal.IsComObject(obj))
            Marshal.ReleaseComObject(obj);
        obj = null;
    }
}
