using System.Runtime.InteropServices;

namespace Vdx.Interop;

/// <summary>
/// Outcome of an operation, carrying the HRESULT so failures are diagnosable from
/// the log file. An elevated resident app has no console to watch, so "it failed"
/// without a code is useless.
/// </summary>
public readonly record struct OpResult(bool Ok, int HResult, string? Detail = null)
{
    public static OpResult Success => new(true, 0);
    public static OpResult Fail(int hr, string? detail = null) => new(false, hr, detail);

    public string Describe() => Ok
        ? "ok"
        : $"FAILED hr=0x{HResult:X8}{(Detail is null ? "" : $" ({Detail})")}"
          + $" {HResultName(HResult)}";

    private static string HResultName(int hr) => hr switch
    {
        unchecked((int)0x80070005) => "E_ACCESSDENIED",
        unchecked((int)0x80004005) => "E_FAIL",
        unchecked((int)0x80070057) => "E_INVALIDARG",
        unchecked((int)0x8007007B) => "ERROR_INVALID_NAME",
        unchecked((int)0x80040154) => "REGDB_E_CLASSNOTREG",
        unchecked((int)0x80004002) => "E_NOINTERFACE",
        _ => ""
    };
}

/// <summary>
/// Virtual desktop operations built on the documented shell32 API only.
///
/// This is the tier that matters most: if MoveWindowToDesktop works for windows
/// owned by other processes, the core workflow needs no undocumented COM at all.
/// </summary>
public sealed class VirtualDesktops : IDisposable
{
    private readonly IVirtualDesktopManager _manager;
    private object? _comObject;

    public VirtualDesktops()
    {
        var type = Type.GetTypeFromCLSID(Clsid.VirtualDesktopManager, throwOnError: false)
            ?? throw new InvalidOperationException("CLSID_VirtualDesktopManager is not registered.");

        _comObject = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("Could not create VirtualDesktopManager.");

        _manager = (IVirtualDesktopManager)_comObject;
    }

    /// <summary>Which desktop a window currently lives on.</summary>
    public OpResult TryGetDesktopId(IntPtr hWnd, out Guid desktopId)
    {
        var hr = _manager.GetWindowDesktopId(hWnd, out desktopId);
        return hr == 0 ? OpResult.Success : OpResult.Fail(hr, "GetWindowDesktopId");
    }

    /// <summary>
    /// Whether a window is on the desktop the user is currently looking at.
    /// Doubles as a reliable "which desktop am I on" probe: pick any window known
    /// to be on the desktop of interest and ask about it.
    /// </summary>
    public OpResult TryIsOnCurrentDesktop(IntPtr hWnd, out bool onCurrent)
    {
        var hr = _manager.IsWindowOnCurrentVirtualDesktop(hWnd, out var flag);
        onCurrent = flag != 0;
        return hr == 0 ? OpResult.Success : OpResult.Fail(hr, "IsWindowOnCurrentVirtualDesktop");
    }

    /// <summary>
    /// Moves a window to a desktop. The load-bearing call for the whole tool.
    /// </summary>
    public OpResult TryMoveWindow(IntPtr hWnd, Guid desktopId)
    {
        var id = desktopId;
        var hr = _manager.MoveWindowToDesktop(hWnd, ref id);
        return hr == 0 ? OpResult.Success : OpResult.Fail(hr, "MoveWindowToDesktop");
    }

    /// <summary>
    /// Activating a window that lives on another desktop makes Windows switch to
    /// that desktop. That gives us "follow the window after moving it" without any
    /// undocumented switch API.
    /// </summary>
    public static bool ActivateWindow(IntPtr hWnd) => Native.SetForegroundWindow(hWnd);

    public void Dispose()
    {
        if (_comObject is not null && Marshal.IsComObject(_comObject))
            Marshal.ReleaseComObject(_comObject);

        _comObject = null;
    }
}
