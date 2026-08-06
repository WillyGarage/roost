using System.IO;
using System.Text;
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
    //
    // The letters are chosen for a DVORAK layout: H and T are the right hand's index and
    // middle fingers on the home row. Note that virtual-key codes follow the active
    // keyboard layout, so "H" here means the key that types h in Dvorak, which is the
    // physical QWERTY-J position. On a QWERTY layout these would land under the left
    // hand instead, so change them if the layout changes.
    //
    // The obvious Win+Ctrl+M is NOT used: Windows reserves it for Magnifier settings.
    // Also taken on this machine: Win+Ctrl+N/S/D/C/L/F/V. Run
    // scripts\probe-hotkeys.ps1 to check a machine rather than guessing.

    /// <summary>Opens the palette to move the active window. Dvorak right index, home row.</summary>
    public string MoveWindowHotkey { get; set; } = "Win+Ctrl+H";

    /// <summary>Opens the palette to switch desktops. Dvorak right middle, home row.</summary>
    public string SwitchDesktopHotkey { get; set; } = "Win+Ctrl+T";

    /// <summary>
    /// Sends the active window straight to the most recently created desktop with no
    /// palette.
    ///
    /// Unbound by default: it turned out not to be needed in practice, since typing two
    /// or three characters into the palette is already fast enough. The implementation is
    /// kept because it costs nothing to keep and the wiring is done; set a chord here to
    /// turn it back on. Available right-hand Dvorak options include Win+Ctrl+G and
    /// Win+Ctrl+R.
    /// </summary>
    public string SendToLastCreatedHotkey { get; set; } = "";

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
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,

        // The file we write is documented with // comments, which strict JSON forbids.
        // Skip them on read so our own output round-trips, and so a hand-edited file
        // keeps its notes instead of being rejected as corrupt.
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Used when writing values into the documented file. The default encoder escapes '+'
    /// as +, which is valid JSON but turns "Win+Ctrl+H" into line noise in a file
    /// people are meant to hand-edit. Relaxed escaping is safe here: this goes to a local
    /// file, never into HTML or a web response.
    /// </summary>
    private static readonly JsonSerializerOptions ValueOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Per-setting documentation, used both to comment the config file and to build the
    /// reference in the Help window. Kept in one place so the two cannot disagree.
    /// </summary>
    public static readonly (string Key, string Default, string[] Lines)[] Reference =
    [
        ("MoveWindowHotkey", "Win+Ctrl+H",
        [
            "Opens the palette to move the active window to another desktop.",
            "Format: modifiers joined by '+' then a key, e.g. \"Win+Ctrl+H\".",
            "Modifiers: Win, Ctrl, Alt, Shift. At least one is required.",
            "Keys: any letter or digit, F1-F24, Space, Enter, Tab, arrows,",
            "Insert, Delete, Home, End, PageUp, PageDown.",
            "Set to \"\" to disable.",
            "NOTE: key codes follow the active keyboard layout. On Dvorak, \"H\" is",
            "the physical QWERTY-J key. Windows reserves a lot of chords; run",
            "scripts\\probe-hotkeys.ps1 to see what is actually free."
        ]),

        ("SwitchDesktopHotkey", "Win+Ctrl+T",
        [
            "Opens the palette to switch desktops without moving any window.",
            "A replacement for Win+Tab that does not need scrolling.",
            "Same format as MoveWindowHotkey."
        ]),

        ("SendToLastCreatedHotkey", "\"\" (disabled)",
        [
            "Sends the active window straight to the most recently created desktop,",
            "with no palette. Intended for 'I just made a project desktop, now send",
            "several more windows there'.",
            "Unbound by default because typing two or three characters in the palette",
            "turned out to be fast enough. Set a chord to enable it."
        ]),

        ("FollowWindowAfterMove", "true",
        [
            "true  = after moving a window, switch to its destination.",
            "false = move it and stay where you are.",
            "Either way, holding Ctrl when you confirm inverts this for that one",
            "action, so both behaviours are always one keystroke away."
        ]),

        ("InsertNewDesktopAfterCurrent", "true",
        [
            "true  = a newly created desktop is placed immediately after the one you",
            "        are on, so related projects stay next to each other.",
            "false = leave it at the far right, which is where Windows puts it.",
            "Desktops are tracked by ID, not position, so reordering breaks nothing."
        ]),

        ("RecentDestinationCount", "5",
        [
            "How many recently used destinations float to the top of the palette when",
            "the search box is empty. 0 disables the recents section."
        ]),

        ("ShowErrorNotifications", "true",
        [
            "Show a tray balloon when an operation fails.",
            "Failures are always written to the log regardless of this setting."
        ])
    ];

    /// <summary>
    /// Emits the config as JSON with the reference comments inline, so the file explains
    /// itself to whoever opens it. Values come from this instance.
    /// </summary>
    public string ToCommentedJson()
    {
        var values = new Dictionary<string, object>
        {
            [nameof(MoveWindowHotkey)] = MoveWindowHotkey,
            [nameof(SwitchDesktopHotkey)] = SwitchDesktopHotkey,
            [nameof(SendToLastCreatedHotkey)] = SendToLastCreatedHotkey,
            [nameof(FollowWindowAfterMove)] = FollowWindowAfterMove,
            [nameof(InsertNewDesktopAfterCurrent)] = InsertNewDesktopAfterCurrent,
            [nameof(RecentDestinationCount)] = RecentDestinationCount,
            [nameof(ShowErrorNotifications)] = ShowErrorNotifications
        };

        var sb = new StringBuilder();

        sb.AppendLine("// Vdx configuration");
        sb.AppendLine("//");
        sb.AppendLine("// Edit, save, then use \"Reload config\" in the tray menu. No restart needed.");
        sb.AppendLine("// Delete this file to get the documented defaults back.");
        sb.AppendLine("// Comments are preserved by Vdx but will be lost if another tool rewrites it.");
        sb.AppendLine("//");
        sb.AppendLine("// The tray menu's \"Help\" entry shows this same reference plus the hotkeys,");
        sb.AppendLine("// palette keys, file locations, and troubleshooting steps.");
        sb.AppendLine("{");

        var index = 0;

        foreach (var (key, defaultText, lines) in Reference)
        {
            if (index > 0)
                sb.AppendLine();

            foreach (var line in lines)
                sb.AppendLine($"  // {line}");

            sb.AppendLine($"  // default: {defaultText}");

            var json = JsonSerializer.Serialize(values[key], ValueOptions);
            var comma = index == Reference.Length - 1 ? "" : ",";
            sb.AppendLine($"  \"{key}\": {json}{comma}");

            index++;
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    public static Config Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var text = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<Config>(text, Options);

                if (loaded is not null)
                {
                    Log.Info($"config loaded from {FilePath}");

                    // Upgrade a file written before the settings were documented. Values
                    // are already parsed, so rewriting only adds the comments.
                    if (!text.TrimStart().StartsWith("//"))
                    {
                        loaded.Save();
                        Log.Info("rewrote config with the inline settings reference");
                    }

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
            File.WriteAllText(FilePath, ToCommentedJson());
        }
        catch (Exception ex)
        {
            Log.Error("could not save config", ex);
        }
    }
}
