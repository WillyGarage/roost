using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vdx.App;

/// <summary>
/// User settings, stored as JSON next to the state file so both are easy to inspect
/// and hand-edit. Written back on first run so the file always documents itself.
/// </summary>
public sealed class Config
{
    // ---- hotkeys -----------------------------------------------------------
    // Format: modifiers joined by '+' then a key, e.g. "Win+Ctrl+K".
    // Recognised modifiers: Win, Ctrl, Alt, Shift. Empty string disables the hotkey.
    //
    // Defaults sit in the Win+Ctrl family because that is already Windows' own
    // virtual-desktop modifier prefix (Win+Ctrl+D, Win+Ctrl+F4, Win+Ctrl+arrows).
    // The obvious Win+Ctrl+M is NOT used: Windows reserves it for Magnifier settings,
    // and Win+Ctrl+L is taken too. Run scripts\probe-hotkeys.ps1 to see what is free
    // on a given machine rather than guessing.

    /// <summary>Opens the palette to move the active window.</summary>
    public string MoveWindowHotkey { get; set; } = "Win+Ctrl+K";

    /// <summary>Opens the palette to switch desktops without moving anything.</summary>
    public string SwitchDesktopHotkey { get; set; } = "Win+Ctrl+J";

    /// <summary>
    /// Sends the active window straight to the most recently created desktop with no
    /// palette. This is the "I just made a project desktop, now send five more windows
    /// there" shortcut.
    /// </summary>
    public string SendToLastCreatedHotkey { get; set; } = "Win+Ctrl+U";

    // ---- behaviour ---------------------------------------------------------

    /// <summary>
    /// Switch to the destination after moving a window. Hold Ctrl when confirming to
    /// invert this for a single action.
    /// </summary>
    public bool FollowWindowAfterMove { get; set; } = true;

    /// <summary>
    /// Place a newly created desktop directly after the current one instead of at the
    /// far right. Verified working on 26200; harmless if the call fails.
    /// </summary>
    public bool InsertNewDesktopAfterCurrent { get; set; } = true;

    /// <summary>How many recently used destinations to float to the top of the list.</summary>
    public int RecentDestinationCount { get; set; } = 5;

    /// <summary>Show a tray balloon when an operation fails. Errors always reach the log.</summary>
    public bool ShowErrorNotifications { get; set; } = true;

    // ---- persistence -------------------------------------------------------

    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vdx");

    public static string FilePath { get; } = Path.Combine(Directory, "config.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static Config Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath), Options);
                if (loaded is not null)
                {
                    Log.Info($"config loaded from {FilePath}");
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            // A corrupt config should not stop the app starting; fall back to defaults
            // and say so, rather than silently reverting the user's settings.
            Log.Error($"config at {FilePath} could not be read, using defaults", ex);
        }

        var fresh = new Config();
        fresh.Save();
        Log.Info($"wrote default config to {FilePath}");
        return fresh;
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex)
        {
            Log.Error("could not save config", ex);
        }
    }
}
