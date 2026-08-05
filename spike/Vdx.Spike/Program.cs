using System.Diagnostics;
using Vdx.Interop;

// Vdx.Spike: capability report for the current Windows build.
//
// Kept in the repo permanently. When a Windows update breaks something, this is the
// first thing to run: it reports what works on this machine without involving the
// hotkeys, the palette, or any UI.
//
// It DOES mutate state: it launches a scratch window (charmap), moves it between
// desktops, and creates + renames + deletes one test desktop. Everything is cleaned
// up in a finally block. It never touches a pre-existing window of yours.

Console.OutputEncoding = System.Text.Encoding.UTF8;

// Round 2 lives in its own file and exercises the undocumented interfaces.
if (args.Contains("--internal"))
    return Vdx.Spike.InternalSpike.Run();

const string TestDesktopName = "Vdx spike (delete me)";

var log = new List<string>();
void Section(string s) { Console.WriteLine(); Console.WriteLine(s); Console.Out.Flush(); }
void Line(string s) { Console.WriteLine("  " + s); Console.Out.Flush(); }
void Result(string q, string verdict) { log.Add($"{q,-6} {verdict}"); Line($"==> {q}: {verdict}"); }

Line($"OS             : {Environment.OSVersion.Version}");
Line($"64-bit process : {Environment.Is64BitProcess}");
Line($"Elevated       : {IsElevated()}");

// ---------------------------------------------------------------------------
// Desktop list (stable tier, registry only)
// ---------------------------------------------------------------------------
Section("=== Desktops (registry) ===");

var desktops = DesktopRegistry.List();
foreach (var d in desktops)
    Line($"{d.Index + 1,3}. {d.DisplayName,-24} {d.Id:B}{(d.IsUnnamed ? "  [unnamed]" : "")}");
Line($"total: {desktops.Count}");

if (desktops.Count < 2)
{
    Line("Need at least 2 desktops to test moving. Aborting.");
    return 1;
}

// ---------------------------------------------------------------------------
// Q4: which undocumented interfaces does this build recognise?
// Safe: QueryService with a wrong IID just returns a failure HRESULT.
// ---------------------------------------------------------------------------
Section("=== Q4: undocumented interface probe ===");

var probes = ShellServiceProbe.ProbeAll();
if (probes.Count == 0)
{
    Line("Could not create the ImmersiveShell service provider at all.");
}
else
{
    foreach (var group in probes.GroupBy(p => p.Interface))
    {
        Line(group.Key);
        foreach (var p in group)
            Line($"    {(p.Supported ? "SUPPORTED" : "no       ")}  {p.Label,-18} {p.Iid:B}" +
                  (p.Supported ? "" : $"  hr=0x{p.HResult:X8}"));
    }
}

var internalOk = probes.Any(p => p is { Interface: "IVirtualDesktopManagerInternal", Supported: true });
Result("Q4", internalOk
    ? $"IVirtualDesktopManagerInternal matched: " +
      probes.First(p => p is { Interface: "IVirtualDesktopManagerInternal", Supported: true }).Label
    : "NO candidate IID matched IVirtualDesktopManagerInternal on this build");

// ---------------------------------------------------------------------------
// Documented API
// ---------------------------------------------------------------------------
Section("=== Documented IVirtualDesktopManager ===");

VirtualDesktops vd;
try
{
    vd = new VirtualDesktops();
    Line("created OK");
}
catch (Exception ex)
{
    Line($"FAILED to create: {ex.Message}");
    return 1;
}

// An anchor is any pre-existing window that is on the desktop we start on. We never
// touch it; we only ask "is it on the current desktop?" to know where we are. This
// avoids trusting the lazily-written CurrentVirtualDesktop registry value.
var anchor = FindAnchorWindow(vd);
if (anchor == IntPtr.Zero)
{
    Line("No anchor window found on the current desktop. Aborting rather than guessing.");
    vd.Dispose();
    return 1;
}

Line($"anchor window  : 0x{anchor:X} \"{Truncate(Native.GetWindowTitle(anchor), 40)}\" " +
     $"[{Native.GetWindowClass(anchor)}]");

vd.TryGetDesktopId(anchor, out var homeDesktopId);
var homeName = desktops.FirstOrDefault(d => d.Id == homeDesktopId)?.DisplayName ?? "?";
Line($"home desktop   : {homeName} {homeDesktopId:B}");

var registryHint = DesktopRegistry.GetCurrentHint();
Line($"registry hint  : {(registryHint == homeDesktopId ? "agrees" : $"DISAGREES ({registryHint:B})")}");

// ---------------------------------------------------------------------------
// Scratch window. charmap.exe is deliberate: plain Win32, always present, one
// window per process, and it never tab-merges into an existing window the way
// Notepad on Windows 11 would (which would mean moving one of YOUR windows).
// ---------------------------------------------------------------------------
Process? scratch = null;
Guid? testDesktopId = null;

try
{
    Section("=== Scratch window ===");

    scratch = Process.Start(new ProcessStartInfo
    {
        FileName = Path.Combine(Environment.SystemDirectory, "charmap.exe"),
        UseShellExecute = true
    });

    if (scratch is null)
    {
        Line("Could not start charmap.exe. Aborting.");
        return 1;
    }

    var scratchHwnd = WaitForProcessWindow(scratch.Id, 8000);
    if (scratchHwnd == IntPtr.Zero)
    {
        Line($"charmap started (pid {scratch.Id}) but no window appeared within 8s. Aborting.");
        return 1;
    }

    Line($"charmap hwnd   : 0x{scratchHwnd:X} \"{Truncate(Native.GetWindowTitle(scratchHwnd), 40)}\" " +
         $"[{Native.GetWindowClass(scratchHwnd)}] pid {scratch.Id}");

    var r = vd.TryGetDesktopId(scratchHwnd, out var scratchDesktop);
    Line($"GetWindowDesktopId (cross-process): {r.Describe()}" +
         (r.Ok ? $" -> {(scratchDesktop == homeDesktopId ? "home desktop, as expected" : $"{scratchDesktop:B}")}" : ""));

    // -----------------------------------------------------------------------
    // Q1: the load-bearing question. Move a window owned by ANOTHER process.
    // -----------------------------------------------------------------------
    Section("=== Q1: MoveWindowToDesktop, cross-process ===");

    var target = desktops.First(d => d.Id != homeDesktopId);
    Line($"moving charmap -> \"{target.DisplayName}\"");

    var move = vd.TryMoveWindow(scratchHwnd, target.Id);
    Line($"MoveWindowToDesktop: {move.Describe()}");

    var verified = false;
    if (move.Ok)
    {
        Thread.Sleep(250);
        if (vd.TryGetDesktopId(scratchHwnd, out var nowOn).Ok)
        {
            verified = nowOn == target.Id;
            Line($"verify: window reports {(verified ? "the target desktop" : $"{nowOn:B} (NOT the target)")}");
        }
    }

    Result("Q1", move.Ok && verified
        ? "YES - documented API moves other processes' windows. No undocumented COM needed for the core move."
        : move.Ok
            ? "PARTIAL - call succeeded but the window did not land on the target"
            : $"NO - {move.Describe()}. Core move must use undocumented MoveViewToDesktop.");

    // -----------------------------------------------------------------------
    // Follow behaviour: does activating a window on another desktop switch to it?
    // If yes, "move and follow" needs no undocumented switch API either.
    // -----------------------------------------------------------------------
    Section("=== Follow: activate a window on another desktop ===");

    if (move.Ok && verified)
    {
        var activated = VirtualDesktops.ActivateWindow(scratchHwnd);
        Thread.Sleep(600);

        var onCurrent = vd.TryIsOnCurrentDesktop(scratchHwnd, out var isCurrent).Ok && isCurrent;
        Line($"SetForegroundWindow returned {activated}; charmap on current desktop: {onCurrent}");

        Result("Follow", onCurrent
            ? "YES - activating the moved window switches desktops. No SwitchDesktop needed."
            : "NO/INCONCLUSIVE - note the spike is not the foreground process, so Windows may " +
              "have refused the activation. The real app will hold foreground rights when its " +
              "palette is open, so retest there before concluding.");

        DesktopKeys.ReturnToWindowsDesktop(vd, anchor);
        Line($"back on home desktop: {vd.TryIsOnCurrentDesktop(anchor, out var back).Ok && back}");
    }
    else
    {
        Result("Follow", "skipped - the move did not succeed");
    }

    // -----------------------------------------------------------------------
    // Q3 + create: make a desktop by keystroke, find its GUID by registry diff,
    // then name it by writing the registry.
    // -----------------------------------------------------------------------
    Section("=== Create (Win+Ctrl+D) + Q3: rename via registry ===");

    testDesktopId = DesktopKeys.CreateDesktopAndSwitch();

    if (testDesktopId is null)
    {
        Result("Create", "NO - pressed Win+Ctrl+D but no new desktop appeared in the registry");
        Result("Q3", "skipped - no desktop to rename");
    }
    else
    {
        var afterCreate = DesktopRegistry.List();
        var created = afterCreate.First(d => d.Id == testDesktopId);
        Line($"new desktop appeared at position {created.Index + 1} of {afterCreate.Count}, " +
             $"{testDesktopId:B}");

        Result("Create", $"YES - keystroke create works, new desktop lands at position " +
                         $"{created.Index + 1} (far right = {created.Index == afterCreate.Count - 1})");

        DesktopRegistry.SetName(testDesktopId.Value, TestDesktopName);
        Thread.Sleep(400);

        var readBack = DesktopRegistry.List().First(d => d.Id == testDesktopId).Name;
        Line($"name read back : \"{readBack}\"");

        Result("Q3", readBack == TestDesktopName
            ? "Registry write persists the name. Whether Task View shows it live needs an eyeball."
            : $"Registry write did not persist (read back \"{readBack}\")");

        // Move the scratch window onto the brand-new desktop, which is the exact
        // acceptance-criteria flow: create, name, move.
        var move2 = vd.TryMoveWindow(scratchHwnd, testDesktopId.Value);
        var ok2 = move2.Ok
                  && vd.TryGetDesktopId(scratchHwnd, out var landed).Ok
                  && landed == testDesktopId.Value;

        Result("NewFlow", ok2
            ? "YES - create, name, then move a window onto the new desktop all work"
            : $"NO - {move2.Describe()}");
    }
}
finally
{
    // -----------------------------------------------------------------------
    // Cleanup. Scratch window first: closing a desktop relocates its windows
    // rather than closing them, which would leave charmap on a real desktop.
    // -----------------------------------------------------------------------
    Section("=== Cleanup ===");

    try
    {
        if (scratch is { HasExited: false })
        {
            scratch.Kill();
            scratch.WaitForExit(3000);
            Line("charmap closed");
        }
    }
    catch (Exception ex) { Line($"charmap cleanup: {ex.Message}"); }

    try
    {
        if (testDesktopId is not null)
        {
            // We are still on the test desktop after creating it, so Win+Ctrl+F4
            // closes that one. Verify by GUID afterwards rather than assuming.
            DesktopKeys.CloseCurrentDesktop();
            Thread.Sleep(800);

            var stillThere = DesktopRegistry.List().Any(d => d.Id == testDesktopId);
            Line(stillThere
                ? $"WARNING: test desktop \"{TestDesktopName}\" still exists. Remove it manually."
                : "test desktop removed");
        }
    }
    catch (Exception ex) { Line($"desktop cleanup: {ex.Message}"); }

    try
    {
        var home = DesktopKeys.ReturnToWindowsDesktop(vd, anchor);
        Line(home ? "returned to home desktop" : "WARNING: could not confirm return to home desktop");
    }
    catch (Exception ex) { Line($"return home: {ex.Message}"); }

    vd.Dispose();
}

Section("=== Summary ===");
foreach (var l in log)
    Line(l);

return 0;

// ---------------------------------------------------------------------------
// helpers
// ---------------------------------------------------------------------------

static bool IsElevated()
{
    using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
    return new System.Security.Principal.WindowsPrincipal(identity)
        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
}

// A pre-existing window on the current desktop, used only as a read-only position
// reference. Skips our own process so we never anchor to something transient.
static IntPtr FindAnchorWindow(VirtualDesktops vd)
{
    var self = Environment.ProcessId;

    foreach (var hWnd in Native.GetTopLevelWindows())
    {
        Native.GetWindowThreadProcessId(hWnd, out var pid);
        if (pid == self)
            continue;

        if (vd.TryIsOnCurrentDesktop(hWnd, out var onCurrent).Ok && onCurrent)
            return hWnd;
    }

    return IntPtr.Zero;
}

static IntPtr WaitForProcessWindow(int pid, int timeoutMs)
{
    var deadline = Environment.TickCount64 + timeoutMs;

    while (Environment.TickCount64 < deadline)
    {
        foreach (var hWnd in Native.GetTopLevelWindows())
        {
            Native.GetWindowThreadProcessId(hWnd, out var owner);
            if (owner == (uint)pid)
                return hWnd;
        }

        Thread.Sleep(150);
    }

    return IntPtr.Zero;
}

static string Truncate(string s, int max) =>
    s.Length <= max ? s : s[..(max - 1)] + "…";
