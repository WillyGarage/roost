using System.Diagnostics;
using Roost.Interop;

// Roost.Spike: capability report for the current Windows build.
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
    return Roost.Spike.InternalSpike.Run();

// Delete a desktop by exact name. A repair tool for when a test or a crash leaves one
// behind. Windows on it are relocated to a neighbour, never closed.
if (args.Contains("--delete"))
{
    var wanted = args.SkipWhile(a => a != "--delete").Skip(1).FirstOrDefault();

    if (string.IsNullOrWhiteSpace(wanted))
    {
        Console.WriteLine("usage: --delete \"<exact desktop name>\"");
        return 1;
    }

    using var remover = new VirtualDesktopsInternal();
    if (!remover.Available)
    {
        Console.WriteLine($"unavailable: {remover.UnavailableReason}");
        return 1;
    }

    var candidates = DesktopRegistry.List();

    // Exact match only. Deleting a desktop is destructive, so no fuzzy matching here,
    // unlike --switch.
    var doomed = candidates.FirstOrDefault(d =>
        d.DisplayName.Equals(wanted, StringComparison.OrdinalIgnoreCase));

    if (doomed is null)
    {
        Console.WriteLine($"no desktop named exactly \"{wanted}\"");
        return 1;
    }

    if (candidates.Count <= 1)
    {
        Console.WriteLine("refusing to delete the only desktop");
        return 1;
    }

    var fallback = candidates[doomed.Index > 0 ? doomed.Index - 1 : 1];
    var removed = remover.TryRemoveDesktop(doomed.Id, fallback.Id);

    Console.WriteLine(removed.Ok
        ? $"deleted \"{doomed.DisplayName}\"; any windows moved to \"{fallback.DisplayName}\""
        : $"failed: {removed.Describe()}");

    return removed.Ok ? 0 : 1;
}

// Desktop list with window counts, the same way the palette computes them. Use this to
// sanity-check the counts against what you can actually see on screen.
if (args.Contains("--list"))
{
    using var reader = new VirtualDesktops();
    using var shellForCurrent = new VirtualDesktopsInternal();
    var all = DesktopRegistry.List();

    Guid? currentId = shellForCurrent.Available
                      && shellForCurrent.TryGetCurrentDesktop(out var cur).Ok
        ? cur
        : DesktopRegistry.GetCurrentHint();

    var counts = new Dictionary<Guid, int>();
    var titles = new Dictionary<Guid, List<string>>();

    foreach (var hWnd in Native.GetUserWindows())
    {
        if (!reader.TryGetDesktopId(hWnd, out var owner).Ok)
            continue;

        // Same rule the app uses: cloaked is normal for windows on other desktops, but a
        // cloaked window on the CURRENT desktop is a ghost (suspended UWP and friends).
        if (owner == currentId && Native.IsCloaked(hWnd) != 0)
            continue;

        counts[owner] = counts.TryGetValue(owner, out var n) ? n + 1 : 1;

        if (!titles.TryGetValue(owner, out var list))
            titles[owner] = list = [];

        list.Add(Native.GetWindowTitle(hWnd));
    }

    var verbose = args.Contains("--windows");

    foreach (var d in all)
    {
        var count = counts.TryGetValue(d.Id, out var c) ? c : 0;
        Console.WriteLine($"{d.Index + 1,3}. {d.DisplayName,-26} {(count == 0 ? "empty" : count + " window(s)")}");

        if (verbose && titles.TryGetValue(d.Id, out var list))
            foreach (var t in list)
                Console.WriteLine($"       - {t}");
    }

    Console.WriteLine();
    Console.WriteLine($"{all.Count} desktops, {counts.Values.Sum()} windows counted, " +
                      $"{all.Count(d => !counts.ContainsKey(d.Id))} empty");
    return 0;
}

// Put desktop names back from the app's backup after Windows loses them.
// Dry run by default; add --apply to write.
if (args.Contains("--restore-names"))
    return Roost.Spike.RestoreNames.Run(args);

// Switch desktops programmatically: --switch "Inbox" or --switch 3 (1-based position).
// A repair tool, and how test scripts put the machine back where they found it without
// depending on the app under test.
if (args.Contains("--switch"))
{
    var wanted = args.SkipWhile(a => a != "--switch").Skip(1).FirstOrDefault();

    if (string.IsNullOrWhiteSpace(wanted))
    {
        Console.WriteLine("usage: --switch <desktop name or 1-based position>");
        return 1;
    }

    using var switcher = new VirtualDesktopsInternal();
    if (!switcher.Available)
    {
        Console.WriteLine($"unavailable: {switcher.UnavailableReason}");
        return 1;
    }

    var all = DesktopRegistry.List();

    var match = int.TryParse(wanted, out var position)
        ? all.FirstOrDefault(d => d.Index == position - 1)
        : all.FirstOrDefault(d =>
              d.DisplayName.Equals(wanted, StringComparison.OrdinalIgnoreCase))
          ?? all.FirstOrDefault(d =>
              d.DisplayName.Contains(wanted, StringComparison.OrdinalIgnoreCase));

    if (match is null)
    {
        Console.WriteLine($"no desktop matching \"{wanted}\"");
        return 1;
    }

    var switched = switcher.TrySwitchTo(match.Id);
    Console.WriteLine(switched.Ok
        ? $"switched to {match.Index + 1}. {match.DisplayName}"
        : $"failed: {switched.Describe()}");

    return switched.Ok ? 0 : 1;
}

// Move a window to a desktop, both matched loosely. A repair tool for when a test moves
// the wrong window - the palette captures the real foreground, which is not always the
// scratch window - and a deterministic way for a test to place a specific window without
// depending on SetForegroundWindow actually sticking.
//   --move "Character Map" "Comm"      (title fragment, then desktop name or 1-based pos)
if (args.Contains("--move"))
{
    var rest       = args.SkipWhile(a => a != "--move").Skip(1).ToArray();
    var titlePart  = rest.ElementAtOrDefault(0);
    var deskWanted = rest.ElementAtOrDefault(1);

    if (string.IsNullOrWhiteSpace(titlePart) || string.IsNullOrWhiteSpace(deskWanted))
    {
        Console.WriteLine("usage: --move \"<window title fragment>\" \"<desktop name or 1-based position>\"");
        return 1;
    }

    // Internal mover: the documented MoveWindowToDesktop returns E_ACCESSDENIED for
    // windows owned by other processes, which is every window worth moving. Documented
    // reader is fine for verifying where it landed afterwards.
    using var mover  = new VirtualDesktopsInternal();
    using var reader = new VirtualDesktops();

    if (!mover.Available)
    {
        Console.WriteLine($"unavailable: {mover.UnavailableReason}");
        return 1;
    }

    var all = DesktopRegistry.List();

    var desk = int.TryParse(deskWanted, out var pos)
        ? all.FirstOrDefault(d => d.Index == pos - 1)
        : all.FirstOrDefault(d => d.DisplayName.Equals(deskWanted, StringComparison.OrdinalIgnoreCase))
          ?? all.FirstOrDefault(d => d.DisplayName.Contains(deskWanted, StringComparison.OrdinalIgnoreCase));

    if (desk is null)
    {
        Console.WriteLine($"no desktop matching \"{deskWanted}\"");
        return 1;
    }

    // Either an exact window handle (0x...) when the title is ambiguous, or the first
    // visible top-level window whose title contains the fragment. Our own windows are
    // skipped either way.
    var self = Environment.ProcessId;
    var hWnd = IntPtr.Zero;

    long handleValue = 0;
    var byHandle = titlePart.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                   && long.TryParse(titlePart.AsSpan(2),
                           System.Globalization.NumberStyles.HexNumber, null, out handleValue);

    foreach (var h in Native.GetTopLevelWindows())
    {
        Native.GetWindowThreadProcessId(h, out var pid);
        if (pid == self)
            continue;

        var matches = byHandle
            ? h == new IntPtr(handleValue)
            : Native.GetWindowTitle(h).Contains(titlePart, StringComparison.OrdinalIgnoreCase);

        if (matches)
        {
            hWnd = h;
            break;
        }
    }

    if (hWnd == IntPtr.Zero)
    {
        Console.WriteLine(byHandle
            ? $"no visible top-level window with handle {titlePart}"
            : $"no window with a title containing \"{titlePart}\"");
        return 1;
    }

    var title = Native.GetWindowTitle(hWnd);
    var move = mover.TryMoveWindow(hWnd, desk.Id);

    if (!move.Ok)
    {
        Console.WriteLine($"failed to move \"{Truncate(title, 50)}\": {move.Describe()}");
        return 1;
    }

    Thread.Sleep(200);
    var landed = reader.TryGetDesktopId(hWnd, out var nowOn).Ok && nowOn == desk.Id;
    Console.WriteLine($"moved \"{Truncate(title, 50)}\" -> {desk.Index + 1}. {desk.DisplayName}" +
                      (landed ? "" : " (WARNING: verify says it did not land)"));
    return landed ? 0 : 1;
}

// Authoritative "which desktop am I on", straight from the shell rather than the
// lazily-written registry hint. Used by switch-bug.ps1 to check whether a switch stuck.
if (args.Contains("--current"))
{
    using var probe = new VirtualDesktopsInternal();

    if (!probe.Available)
    {
        Console.WriteLine($"unavailable: {probe.UnavailableReason}");
        return 1;
    }

    var op = probe.TryGetCurrentDesktop(out var currentId);
    if (!op.Ok)
    {
        Console.WriteLine($"failed: {op.Describe()}");
        return 1;
    }

    var found = DesktopRegistry.List().FirstOrDefault(d => d.Id == currentId);
    Console.WriteLine($"{(found is null ? "?" : (found.Index + 1).ToString())}. " +
                      $"{found?.DisplayName ?? "unknown"}  {currentId:B}");
    return 0;
}

const string TestDesktopName = "Roost spike (delete me)";

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
