using Vdx.Interop;

// Vdx.Spike: capability report for the current Windows build.
//
// Kept in the repo permanently. When a Windows update breaks something, this is the
// first thing to run: it reports what works on this machine without involving the
// hotkeys, the palette, or any UI.
//
// Everything here is READ-ONLY for now. Nothing creates, renames, moves, or switches.

Console.OutputEncoding = System.Text.Encoding.UTF8;

Console.WriteLine($"OS            : {Environment.OSVersion.Version}");
Console.WriteLine($"64-bit process: {Environment.Is64BitProcess}");
Console.WriteLine($"Elevated      : {IsElevated()}");
Console.WriteLine();

// ---------------------------------------------------------------------------
// Stable tier: enumerate desktops from the registry. No COM involved.
// ---------------------------------------------------------------------------
Console.WriteLine("=== Desktops (registry) ===");

var desktops = DesktopRegistry.List();

if (desktops.Count == 0)
{
    Console.WriteLine("  NONE FOUND. Registry layout is not what we expect on this build.");
}
else
{
    var currentHint = DesktopRegistry.GetCurrentHint();

    foreach (var d in desktops)
    {
        var marker = d.Id == currentHint ? "  <- current (registry hint)" : "";
        var unnamed = d.IsUnnamed ? "  [unnamed]" : "";
        Console.WriteLine($"  {d.Index + 1,3}. {d.DisplayName,-28} {d.Id:B}{unnamed}{marker}");
    }

    Console.WriteLine();
    Console.WriteLine($"  total: {desktops.Count}   named: {desktops.Count(d => !d.IsUnnamed)}");
}

Console.WriteLine();
Console.WriteLine("Not yet implemented (see docs/NOTES.md Q1-Q4):");
Console.WriteLine("  Q1  cross-process MoveWindowToDesktop");
Console.WriteLine("  Q2  MoveDesktop / insert-after-current");
Console.WriteLine("  Q3  registry rename picked up by Task View");
Console.WriteLine("  Q4  IVirtualDesktopManagerInternal IID for this build");

static bool IsElevated()
{
    using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
    return new System.Security.Principal.WindowsPrincipal(identity)
        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
}
