using System.IO;
using System.Text.Json;

namespace Roost.App;

/// <summary>
/// Small persisted state: recently used destinations, the last desktop we created, and
/// a GUID-to-name snapshot.
///
/// The name snapshot exists because Explorer crashes and major Windows updates can drop
/// desktop names. Keeping our own copy means they can be restored instead of retyped.
/// </summary>
public sealed class AppState
{
    /// <summary>Destination desktop GUIDs, most recently used first.</summary>
    public List<Guid> RecentDestinations { get; set; } = [];

    /// <summary>Target for the send-to-last-created hotkey.</summary>
    public Guid? LastCreatedDesktop { get; set; }

    /// <summary>GUID to name, as last seen. Backup only; the registry is the source of truth.</summary>
    public Dictionary<string, string> KnownNames { get; set; } = [];

    private static string FilePath { get; } = Path.Combine(Config.Directory, "state.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppState Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppState>(File.ReadAllText(FilePath), Options)
                       ?? new AppState();
        }
        catch (Exception ex)
        {
            Log.Error("state.json could not be read, starting fresh", ex);
        }

        return new AppState();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Config.Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex)
        {
            Log.Error("could not save state", ex);
        }
    }

    /// <summary>Moves a destination to the front of the recent list.</summary>
    public void RecordDestination(Guid desktopId, int keep)
    {
        RecentDestinations.Remove(desktopId);
        RecentDestinations.Insert(0, desktopId);

        if (RecentDestinations.Count > Math.Max(keep, 0))
            RecentDestinations.RemoveRange(keep, RecentDestinations.Count - keep);

        Save();
    }

    public void RecordCreated(Guid desktopId)
    {
        LastCreatedDesktop = desktopId;
        Save();
    }

    /// <summary>Refreshes the name backup from whatever the registry currently reports.</summary>
    public void SnapshotNames(IEnumerable<Roost.Interop.VirtualDesktopInfo> desktops)
    {
        var changed = false;

        foreach (var d in desktops.Where(d => !d.IsUnnamed))
        {
            var key = d.Id.ToString();
            if (!KnownNames.TryGetValue(key, out var existing) || existing != d.Name)
            {
                KnownNames[key] = d.Name!;
                changed = true;
            }
        }

        if (changed)
            Save();
    }
}
