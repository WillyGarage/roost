using System.IO;
using System.Text;

namespace Roost.App;

/// <summary>
/// Append-only file log with a daily file.
///
/// Not optional: this app is meant to run elevated from a scheduled task, where there
/// is no console and no debugger attached, so a failed move with no record would be
/// undiagnosable. Every operation that can fail logs its HRESULT.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _path;

    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Roost", "logs");

    public static string CurrentFile => _path ??= Path.Combine(
        Directory, $"roost-{DateTime.Now:yyyyMMdd}.log");

    public static void Init()
    {
        System.IO.Directory.CreateDirectory(Directory);
        Info($"--- started, pid {Environment.ProcessId}, " +
             $"v{typeof(Log).Assembly.GetName().Version}, " +
             $"OS {Environment.OSVersion.Version}, elevated={IsElevated()} ---");
        PruneOldFiles();
    }

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message) => Write("ERROR", message);

    public static void Error(string message, Exception ex) =>
        Write("ERROR", $"{message}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {level} {message}{Environment.NewLine}";

        lock (Gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.AppendAllText(CurrentFile, line, Encoding.UTF8);
            }
            catch
            {
                // Never let logging take the app down. If the disk is full or the
                // directory is unwritable there is nowhere useful to report it.
            }
        }
    }

    /// <summary>Keeps the last two weeks so the directory cannot grow forever.</summary>
    private static void PruneOldFiles()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-14);

            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "roost-*.log"))
                if (File.GetLastWriteTime(file) < cutoff)
                    File.Delete(file);
        }
        catch (Exception ex)
        {
            Warn($"could not prune old logs: {ex.Message}");
        }
    }

    public static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
}
