using Vdx.Interop;

namespace Vdx.App;

/// <summary>
/// The app's single entry point for desktop operations. Combines the three tiers:
/// registry reads for the list and names, the documented API for reading a window's
/// desktop, and the undocumented interfaces for everything that mutates.
/// </summary>
public sealed class DesktopService : IDisposable
{
    private readonly VirtualDesktops _documented;
    private readonly VirtualDesktopsInternal _internal;

    /// <summary>
    /// False when this Windows build does not expose the interfaces we need. Moving
    /// windows is impossible in that state, so the app says so at startup rather than
    /// failing silently on each attempt.
    /// </summary>
    public bool CanMoveWindows { get; }

    /// <summary>Human-readable reason when <see cref="CanMoveWindows"/> is false.</summary>
    public string? Unavailable { get; }

    public DesktopService()
    {
        _documented = new VirtualDesktops();
        _internal = new VirtualDesktopsInternal();

        if (!_internal.Available)
        {
            Unavailable = _internal.UnavailableReason ?? "unknown reason";
            Log.Error($"undocumented interfaces unavailable: {Unavailable}");
            return;
        }

        // Vtable sanity gate, same check the spike uses. GetCount is slot 1 in every
        // published version, so it is safe to call before trusting the rest. If it
        // disagrees with the registry, the interface layout is wrong for this build and
        // every later call would jump to the wrong function.
        var registryCount = DesktopRegistry.List().Count;
        var op = _internal.TryGetCount(out var shellCount);

        if (!op.Ok)
        {
            Unavailable = $"GetCount failed: {op.Describe()}";
            Log.Error(Unavailable);
            return;
        }

        if (shellCount != registryCount)
        {
            Unavailable = $"interface layout looks wrong for this Windows build " +
                          $"(shell reports {shellCount} desktops, registry reports {registryCount}). " +
                          $"Moving windows is disabled. Run the spike for details.";
            Log.Error(Unavailable);
            return;
        }

        CanMoveWindows = true;
        Log.Info($"desktop service ready, {shellCount} desktops, interfaces verified");
    }

    /// <summary>All desktops in display order, names resolved.</summary>
    public IReadOnlyList<VirtualDesktopInfo> List() => DesktopRegistry.List();

    /// <summary>
    /// The desktop currently on screen. Asks the shell first, then falls back to
    /// reading it off a window we know is visible, then to the registry hint.
    /// </summary>
    public Guid? GetCurrentDesktopId(IntPtr referenceWindow = default)
    {
        if (_internal.Available && _internal.TryGetCurrentDesktop(out var fromShell).Ok)
            return fromShell;

        if (referenceWindow != IntPtr.Zero
            && _documented.TryGetDesktopId(referenceWindow, out var fromWindow).Ok)
            return fromWindow;

        return DesktopRegistry.GetCurrentHint();
    }

    /// <summary>Which desktop a specific window is on. Documented API, works cross-process.</summary>
    public Guid? GetWindowDesktopId(IntPtr hWnd) =>
        _documented.TryGetDesktopId(hWnd, out var id).Ok ? id : null;

    /// <summary>
    /// Moves a window to a desktop, optionally switching to follow it.
    /// </summary>
    public bool MoveWindow(IntPtr hWnd, Guid targetDesktop, bool follow, out string? error)
    {
        error = null;

        if (!CanMoveWindows)
        {
            error = Unavailable;
            return false;
        }

        var title = Native.GetWindowTitle(hWnd);

        // A window set to "show on all desktops" silently ignores a move, which looks
        // like the tool is broken. Check first and say what actually happened.
        if (_internal.TryCanMoveWindow(hWnd, out var canMove).Ok && !canMove)
        {
            error = $"Windows will not move \"{Truncate(title)}\". It is probably pinned " +
                    $"to all desktops, or is a system window.";
            Log.Warn($"move refused by shell: \"{title}\"");
            return false;
        }

        var move = _internal.TryMoveWindow(hWnd, targetDesktop);
        if (!move.Ok)
        {
            error = $"Could not move \"{Truncate(title)}\": {move.Describe()}"
                    + (Log.IsElevated() ? "" : ". If that window is running as administrator, " +
                                               "this app has to be elevated too.");
            Log.Error($"move failed for \"{title}\" -> {targetDesktop:B}: {move.Describe()}");
            return false;
        }

        Log.Info($"moved \"{title}\" -> {targetDesktop:B}");

        if (!follow)
            return true;

        if (!SwitchTo(targetDesktop, out var switchError))
        {
            // The move worked, so this is a partial success, not a failure.
            error = $"Moved the window, but could not switch to the destination: {switchError}";
            return false;
        }

        // Bring the window we just moved to the front on its new desktop, so the user
        // lands with it focused rather than behind whatever was already there.
        VirtualDesktops.ActivateWindow(hWnd);
        return true;
    }

    public bool SwitchTo(Guid desktopId, out string? error)
    {
        error = null;

        if (!CanMoveWindows)
        {
            error = Unavailable;
            return false;
        }

        var op = _internal.TrySwitchTo(desktopId);
        if (!op.Ok)
        {
            error = $"Could not switch desktops: {op.Describe()}";
            Log.Error(error);
            return false;
        }

        Log.Info($"switched to {desktopId:B}");

        // Claim the foreground on the destination, or the switch can be silently undone.
        //
        // SwitchDesktop leaves the previously focused window still holding the
        // foreground, even though it lives on the desktop we just left. Well-behaved
        // Win32 windows sit there quietly, but a UWP window (hosted by
        // ApplicationFrameHost) asynchronously re-activates itself a moment later, and
        // activating a window on another desktop makes Windows switch back to it. The
        // result was a switch that appeared to do nothing whenever the active window was
        // something like Settings, while working fine from Notepad.
        //
        // Activating a window that is already on the destination moves the foreground off
        // the stale window, so the late re-activation becomes a foreground steal by a
        // background app, which Windows refuses.
        var landing = TopWindowOn(desktopId);

        if (landing != IntPtr.Zero)
        {
            VirtualDesktops.ActivateWindow(landing);
            Log.Info($"focused \"{Truncate(Native.GetWindowTitle(landing))}\" on the destination");
        }
        else
        {
            // An empty desktop has nothing to focus. Nothing is holding the foreground
            // that we care about either, so there is nothing to defend against.
            Log.Info("destination has no window to focus");
        }

        return true;
    }

    /// <summary>
    /// The frontmost ordinary window on a given desktop, or zero if it has none.
    /// EnumWindows walks top-level windows in Z-order, front to back, so the first match
    /// is the one the user would consider "on top".
    /// </summary>
    private IntPtr TopWindowOn(Guid desktopId)
    {
        var shell = Native.GetShellWindow();

        foreach (var hWnd in Native.GetTopLevelWindows())
        {
            if (hWnd == shell)
                continue;

            if (_documented.TryGetDesktopId(hWnd, out var id).Ok && id == desktopId)
                return hWnd;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Creates a desktop, names it, and optionally positions it right after the
    /// current one instead of at the far right.
    /// </summary>
    public bool CreateDesktop(
        string name, bool insertAfterCurrent, out Guid desktopId, out string? error)
    {
        desktopId = Guid.Empty;
        error = null;

        if (!CanMoveWindows)
        {
            error = Unavailable;
            return false;
        }

        var currentId = GetCurrentDesktopId();
        var currentIndex = currentId is null
            ? -1
            : List().FirstOrDefault(d => d.Id == currentId)?.Index ?? -1;

        var create = _internal.TryCreateDesktop(out desktopId);
        if (!create.Ok)
        {
            error = $"Could not create a desktop: {create.Describe()}";
            Log.Error(error);
            return false;
        }

        Log.Info($"created desktop {desktopId:B}");

        // Naming and positioning are both best-effort refinements of a desktop that
        // already exists. Neither failing should turn this into an error, because the
        // user's window still has somewhere to go.
        if (!string.IsNullOrWhiteSpace(name))
        {
            var named = _internal.TrySetName(desktopId, name);

            if (!named.Ok)
            {
                Log.Warn($"shell rename failed ({named.Describe()}), falling back to registry");
                try
                {
                    DesktopRegistry.SetName(desktopId, name);
                }
                catch (Exception ex)
                {
                    Log.Error($"registry rename also failed for {desktopId:B}", ex);
                }
            }
            else
            {
                Log.Info($"named desktop {desktopId:B} \"{name}\"");
            }
        }

        if (insertAfterCurrent && currentIndex >= 0)
        {
            var targetIndex = currentIndex + 1;
            var reorder = _internal.TryReorderDesktop(desktopId, targetIndex);

            if (reorder.Ok)
                Log.Info($"positioned desktop {desktopId:B} at index {targetIndex}");
            else
                Log.Warn($"could not reposition new desktop, leaving it at the end: {reorder.Describe()}");
        }

        return true;
    }

    private static string Truncate(string s, int max = 40) =>
        string.IsNullOrEmpty(s) ? "(untitled window)"
        : s.Length <= max ? s
        : s[..(max - 1)] + "…";

    public void Dispose()
    {
        _internal.Dispose();
        _documented.Dispose();
    }
}
