using System.Diagnostics;
using Roost.Interop;

namespace Roost.Spike;

/// <summary>
/// Round 2: exercises the undocumented interfaces, which is the only route to moving
/// another process's window (round 1 proved the documented API returns E_ACCESSDENIED).
///
/// Safety gate: before calling anything that mutates state, GetCount is compared
/// against the registry desktop count. Agreement is strong evidence the vtable layout
/// is correct for this build. If it disagrees, we abort without calling another slot,
/// because a misaligned vtable means every later call jumps to the wrong function.
/// </summary>
internal static class InternalSpike
{
    private const string TestDesktopName = "Roost round2 (delete me)";

    public static int Run()
    {
        var log = new List<string>();

        void Section(string s) { Console.WriteLine(); Console.WriteLine(s); Console.Out.Flush(); }
        void Line(string s) { Console.WriteLine("  " + s); Console.Out.Flush(); }
        void Result(string q, string v) { log.Add($"{q,-9} {v}"); Line($"==> {q}: {v}"); }

        Section("=== Round 2: undocumented interfaces ===");

        var registryDesktops = DesktopRegistry.List();
        Line($"registry says {registryDesktops.Count} desktops");

        using var internals = new VirtualDesktopsInternal();
        Line($"Available: {internals.Available}" +
             (internals.Available ? "" : $"  reason: {internals.UnavailableReason}"));

        if (!internals.Available)
        {
            Result("Internal", $"NOT AVAILABLE - {internals.UnavailableReason}");
            return 1;
        }

        // ---- safety gate ---------------------------------------------------
        var countOp = internals.TryGetCount(out var shellCount);
        Line($"GetCount: {countOp.Describe()}" + (countOp.Ok ? $" -> {shellCount}" : ""));

        if (!countOp.Ok || shellCount != registryDesktops.Count)
        {
            Result("VTable", $"MISMATCH - shell reports {shellCount}, registry reports " +
                             $"{registryDesktops.Count}. Aborting before calling any other slot.");
            return 1;
        }

        Result("VTable", $"OK - GetCount agrees with the registry ({shellCount}). " +
                         "Slot alignment confirmed for this build.");

        using var documented = new VirtualDesktops();

        var anchor = FindAnchor(documented);
        if (anchor == IntPtr.Zero)
        {
            Line("No anchor window on the current desktop. Aborting.");
            return 1;
        }

        documented.TryGetDesktopId(anchor, out var homeId);
        var homeName = registryDesktops.FirstOrDefault(d => d.Id == homeId)?.DisplayName ?? "?";
        var homeIndex = registryDesktops.FirstOrDefault(d => d.Id == homeId)?.Index ?? 0;
        Line($"home desktop: {homeName} (position {homeIndex + 1})");

        Process? scratch = null;
        Guid? testDesktop = null;

        try
        {
            // ---- scratch window --------------------------------------------
            Section("=== Scratch window ===");

            scratch = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "charmap.exe"),
                UseShellExecute = true
            });

            var hwnd = scratch is null ? IntPtr.Zero : WaitForWindow(scratch.Id, 8000);
            if (hwnd == IntPtr.Zero)
            {
                Line("charmap window never appeared. Aborting.");
                return 1;
            }

            Line($"charmap hwnd 0x{hwnd:X} pid {scratch!.Id}");

            var canOp = internals.TryCanMoveWindow(hwnd, out var canMove);
            Line($"CanViewMoveDesktops: {canOp.Describe()}" + (canOp.Ok ? $" -> {canMove}" : ""));

            // ---- Q1 redux: the operation that failed on the documented API ----
            Section("=== Q1 (internal): MoveViewToDesktop, cross-process ===");

            var target = registryDesktops.First(d => d.Id != homeId);
            Line($"moving charmap -> \"{target.DisplayName}\"");

            var move = internals.TryMoveWindow(hwnd, target.Id);
            Line($"MoveViewToDesktop: {move.Describe()}");

            var landed = false;
            if (move.Ok)
            {
                Thread.Sleep(250);
                // Verify with the DOCUMENTED reader, which works cross-process even
                // though the documented writer does not.
                if (documented.TryGetDesktopId(hwnd, out var nowOn).Ok)
                {
                    landed = nowOn == target.Id;
                    Line($"verify: {(landed ? "on the target desktop" : $"NOT on target ({nowOn:B})")}");
                }
            }

            Result("Q1", move.Ok && landed
                ? "YES - undocumented MoveViewToDesktop moves other processes' windows."
                : $"NO - {move.Describe()}");

            if (!(move.Ok && landed))
                return 1;

            // ---- instant switch --------------------------------------------
            Section("=== SwitchDesktop (instant, no animation) ===");

            var sw = internals.TrySwitchTo(target.Id);
            Thread.Sleep(400);
            var onTarget = documented.TryIsOnCurrentDesktop(hwnd, out var isOn).Ok && isOn;
            Line($"SwitchDesktop: {sw.Describe()}; now on target: {onTarget}");

            var back = internals.TrySwitchTo(homeId);
            Thread.Sleep(400);
            var atHome = documented.TryIsOnCurrentDesktop(anchor, out var isHome).Ok && isHome;
            Line($"switch back: {back.Describe()}; at home: {atHome}");

            Result("Switch", sw.Ok && onTarget && back.Ok && atHome
                ? "YES - instant switch works both directions. No keystroke fallback needed."
                : $"PARTIAL - to target {sw.Describe()}, back {back.Describe()}");

            // ---- create without switching -----------------------------------
            Section("=== CreateDesktop (without switching) + Q2 reorder + rename ===");

            var createOp = internals.TryCreateDesktop(out var newId);
            Line($"CreateDesktop: {createOp.Describe()}" + (createOp.Ok ? $" -> {newId:B}" : ""));

            if (!createOp.Ok)
            {
                Result("Create", $"NO - {createOp.Describe()}");
                return 1;
            }

            testDesktop = newId;
            Thread.Sleep(400);

            var stayedHome = documented.TryIsOnCurrentDesktop(anchor, out var still).Ok && still;
            var listAfter = DesktopRegistry.List();
            var createdInfo = listAfter.FirstOrDefault(d => d.Id == newId);

            Line($"appeared in registry at position {(createdInfo?.Index + 1)?.ToString() ?? "NOT FOUND"} " +
                 $"of {listAfter.Count}; still on home desktop: {stayedHome}");

            Result("Create", createdInfo is not null && stayedHome
                ? "YES - creates without stealing the view, unlike Win+Ctrl+D."
                : $"PARTIAL - inRegistry={createdInfo is not null}, stayedHome={stayedHome}");

            // ---- Q3: rename through the shell -------------------------------
            var nameOp = internals.TrySetName(newId, TestDesktopName);
            Thread.Sleep(400);
            var nameBack = DesktopRegistry.List().FirstOrDefault(d => d.Id == newId)?.Name;
            Line($"SetDesktopName: {nameOp.Describe()}; registry now reads \"{nameBack}\"");

            Result("Q3", nameOp.Ok && nameBack == TestDesktopName
                ? "YES - shell rename works and lands in the registry, so Task View is in sync."
                : $"NO - {nameOp.Describe()}, registry reads \"{nameBack}\"");

            // ---- Q2: reorder to sit right after the current desktop ----------
            var desiredIndex = homeIndex + 1;
            var reorderOp = internals.TryReorderDesktop(newId, desiredIndex);
            Thread.Sleep(500);

            var reordered = DesktopRegistry.List().FirstOrDefault(d => d.Id == newId);
            Line($"MoveDesktop(->{desiredIndex}): {reorderOp.Describe()}; " +
                 $"now at position {(reordered?.Index + 1)?.ToString() ?? "?"}");

            Result("Q2", reorderOp.Ok && reordered?.Index == desiredIndex
                ? $"YES - new desktop can be inserted directly after the current one " +
                  $"(position {desiredIndex + 1}). No far-right-then-drag needed."
                : $"NO - {reorderOp.Describe()}, ended at position {(reordered?.Index + 1)?.ToString() ?? "?"}");

            // ---- full acceptance flow ---------------------------------------
            var finalMove = internals.TryMoveWindow(hwnd, newId);
            var finalOk = finalMove.Ok
                          && documented.TryGetDesktopId(hwnd, out var f).Ok
                          && f == newId;

            Result("NewFlow", finalOk
                ? "YES - create, name, position, then move a window onto it: all working."
                : $"NO - {finalMove.Describe()}");
        }
        finally
        {
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
            catch (Exception ex) { Line($"charmap: {ex.Message}"); }

            try
            {
                if (testDesktop is not null)
                {
                    // RemoveDesktop takes an explicit fallback, so any windows left on
                    // it land somewhere known rather than wherever Windows decides.
                    var rm = internals.TryRemoveDesktop(testDesktop.Value, homeId);
                    Thread.Sleep(600);

                    var gone = !DesktopRegistry.List().Any(d => d.Id == testDesktop);
                    Line($"RemoveDesktop: {rm.Describe()}; gone: {gone}");

                    if (!gone)
                        Line($"WARNING: \"{TestDesktopName}\" still exists, remove it manually.");
                }
            }
            catch (Exception ex) { Line($"desktop: {ex.Message}"); }

            try
            {
                internals.TrySwitchTo(homeId);
                Thread.Sleep(300);
                var home = documented.TryIsOnCurrentDesktop(anchor, out var h).Ok && h;
                Line(home ? "back on home desktop" : "WARNING: not confirmed back on home desktop");
            }
            catch (Exception ex) { Line($"return home: {ex.Message}"); }
        }

        Section("=== Summary ===");
        foreach (var l in log)
            Line(l);

        return 0;
    }

    private static IntPtr FindAnchor(VirtualDesktops vd)
    {
        var self = Environment.ProcessId;

        foreach (var hWnd in Native.GetTopLevelWindows())
        {
            Native.GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == self) continue;

            if (vd.TryIsOnCurrentDesktop(hWnd, out var onCurrent).Ok && onCurrent)
                return hWnd;
        }

        return IntPtr.Zero;
    }

    private static IntPtr WaitForWindow(int pid, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            foreach (var hWnd in Native.GetTopLevelWindows())
            {
                Native.GetWindowThreadProcessId(hWnd, out var owner);
                if (owner == (uint)pid) return hWnd;
            }

            Thread.Sleep(150);
        }

        return IntPtr.Zero;
    }
}
