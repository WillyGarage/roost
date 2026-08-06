using System.Text.Json;
using Vdx.Interop;

namespace Vdx.Spike;

/// <summary>
/// Restores desktop names from the app's own backup.
///
/// Windows keeps desktop names in HKCU, and an Explorer crash or a major Windows update
/// can drop them, leaving a row of "Desktop 4" where named projects used to be. The app
/// snapshots GUID to name into state.json every time it opens the palette, so the
/// information to put them back is already on disk. This applies it.
///
/// Only touches desktops that currently exist. state.json accumulates names of deleted
/// desktops too, and those must not be resurrected.
///
/// Dry run unless --apply is passed.
/// </summary>
internal static class RestoreNames
{
    public static int Run(string[] args)
    {
        var apply = args.Contains("--apply");

        // Optional explicit path, otherwise the app's own state file.
        var path = args.SkipWhile(a => a != "--restore-names").Skip(1)
                       .FirstOrDefault(a => !a.StartsWith("--"))
                   ?? Path.Combine(
                       Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                       "Vdx", "state.json");

        Console.WriteLine($"backup file : {path}");

        if (!File.Exists(path))
        {
            Console.WriteLine("NOT FOUND. Has the app ever run?");
            return 1;
        }

        Dictionary<string, string> known;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));

            if (!doc.RootElement.TryGetProperty("KnownNames", out var namesNode))
            {
                Console.WriteLine("no KnownNames section in the backup");
                return 1;
            }

            known = namesNode.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.String)
                .ToDictionary(p => p.Name, p => p.Value.GetString()!);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"could not read the backup: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"backup holds {known.Count} names");
        Console.WriteLine();

        var live = DesktopRegistry.List();
        var planned = new List<(VirtualDesktopInfo Desktop, string NewName)>();

        foreach (var desktop in live)
        {
            if (!known.TryGetValue(desktop.Id.ToString(), out var backedUp))
            {
                Console.WriteLine($"  {desktop.Index + 1,3}. {desktop.DisplayName,-24} no backup entry, leaving alone");
                continue;
            }

            if (desktop.Name == backedUp)
            {
                Console.WriteLine($"  {desktop.Index + 1,3}. {desktop.DisplayName,-24} already correct");
                continue;
            }

            Console.WriteLine($"  {desktop.Index + 1,3}. {desktop.DisplayName,-24} -> \"{backedUp}\"");
            planned.Add((desktop, backedUp));
        }

        Console.WriteLine();

        if (planned.Count == 0)
        {
            Console.WriteLine("Nothing to restore.");
            return 0;
        }

        if (!apply)
        {
            Console.WriteLine($"{planned.Count} name(s) would change. Re-run with --apply to do it.");
            return 0;
        }

        // Prefer the shell's own SetDesktopName: it writes the same registry value but
        // also keeps Task View's display in sync. Fall back to the registry directly,
        // which is the tier that survives Windows updates.
        using var shell = new VirtualDesktopsInternal();
        var failures = 0;

        foreach (var (desktop, newName) in planned)
        {
            var applied = false;

            if (shell.Available)
            {
                var op = shell.TrySetName(desktop.Id, newName);
                applied = op.Ok;

                if (!op.Ok)
                    Console.WriteLine($"  shell rename failed for \"{newName}\": {op.Describe()}");
            }

            if (!applied)
            {
                try
                {
                    DesktopRegistry.SetName(desktop.Id, newName);
                    applied = true;
                    Console.WriteLine($"  \"{newName}\" written via registry " +
                                      "(restart Explorer if Task View still shows the old name)");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  FAILED \"{newName}\": {ex.Message}");
                    failures++;
                }
            }

            if (applied)
                Console.WriteLine($"  restored \"{newName}\"");
        }

        Console.WriteLine();
        Console.WriteLine($"Done. {planned.Count - failures} restored, {failures} failed.");
        return failures == 0 ? 0 : 1;
    }
}
