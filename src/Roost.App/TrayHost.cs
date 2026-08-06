using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Roost.App;

/// <summary>
/// Tray icon and its menu. WinForms NotifyIcon because WPF has no tray support.
///
/// The icon is drawn at runtime rather than shipped as a .ico file, which keeps the
/// repo free of binary assets and the published exe free of embedded resources.
/// </summary>
public sealed class TrayHost : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Config _config;
    private Icon? _generated;

    public event Action? ReloadRequested;
    public event Action? ExitRequested;
    public event Action? HelpRequested;

    public TrayHost(Config config, string statusLine)
    {
        _config = config;
        _generated = BuildIcon();

        _icon = new NotifyIcon
        {
            Icon = _generated,
            Visible = true,
            Text = Trim($"Roost — {statusLine}")
        };

        var menu = new ContextMenuStrip();

        // Non-clickable header showing the bound hotkeys, so the answer to "what was the
        // shortcut again" is one right-click away.
        var header = new ToolStripMenuItem(HotkeySummary()) { Enabled = false };
        menu.Items.Add(header);
        menu.Items.Add(new ToolStripSeparator());

        // Help first: it is the entry that explains all the others, including the config
        // settings, so it should be the obvious thing to click when you do not know what
        // you are looking for.
        var help = new ToolStripMenuItem("Help and settings reference", null,
            (_, _) => HelpRequested?.Invoke());
        help.Font = new Font(help.Font, System.Drawing.FontStyle.Bold);
        menu.Items.Add(help);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open config file", null, (_, _) => OpenInShell(Config.FilePath));
        menu.Items.Add("Open log folder", null, (_, _) => OpenInShell(Log.Directory));
        menu.Items.Add("Reload config", null, (_, _) => ReloadRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());

        _icon.ContextMenuStrip = menu;

        // Double-clicking a tray icon should do the most obvious thing, and for an app
        // whose whole UI is a hotkey palette, that is "explain yourself".
        _icon.DoubleClick += (_, _) => HelpRequested?.Invoke();

        Log.Info("tray icon created");
    }

    private string HotkeySummary()
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(_config.MoveWindowHotkey))
            parts.Add($"{_config.MoveWindowHotkey}  move window");

        if (!string.IsNullOrWhiteSpace(_config.SwitchDesktopHotkey))
            parts.Add($"{_config.SwitchDesktopHotkey}  switch desktop");

        if (!string.IsNullOrWhiteSpace(_config.SendToLastCreatedHotkey))
            parts.Add($"{_config.SendToLastCreatedHotkey}  send to last created");

        return parts.Count == 0 ? "No hotkeys configured" : string.Join("     ", parts);
    }

    /// <summary>
    /// Shows a transient error. Errors always go to the log; this is the part the user
    /// actually notices, since a failed move is otherwise indistinguishable from
    /// nothing happening.
    /// </summary>
    public void ShowError(string message)
    {
        Log.Warn($"reported to user: {message}");

        if (!_config.ShowErrorNotifications)
            return;

        try
        {
            _icon.ShowBalloonTip(6000, "Roost", message, ToolTipIcon.Warning);
        }
        catch (Exception ex)
        {
            Log.Error("could not show balloon", ex);
        }
    }

    public void SetStatus(string statusLine) => _icon.Text = Trim($"Roost — {statusLine}");

    /// <summary>NotifyIcon.Text throws above 63 characters.</summary>
    private static string Trim(string s) => s.Length <= 63 ? s : s[..62] + "…";

    private static void OpenInShell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"could not open {path}", ex);
        }
    }

    /// <summary>Draws a simple rounded badge with two window shapes.</summary>
    private static Icon BuildIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var background = new SolidBrush(Color.FromArgb(255, 40, 44, 52));
            using var path = RoundedRect(new Rectangle(1, 1, 30, 30), 7);
            g.FillPath(background, path);

            // Two offset rectangles: a window and the desktop it is heading to.
            using var far = new SolidBrush(Color.FromArgb(255, 110, 118, 130));
            using var near = new SolidBrush(Color.FromArgb(255, 108, 168, 232));
            g.FillRectangle(far, 6, 8, 13, 11);
            g.FillRectangle(near, 14, 14, 13, 11);
        }

        // Icon.FromHandle does not own the handle, so copy into a managed Icon and free
        // the HICON immediately to avoid leaking a GDI object.
        var hIcon = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(hIcon);
            return (Icon)temporary.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(hIcon);
        }
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();

        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();

        return path;
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool DestroyIcon(IntPtr handle);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();

        _generated?.Dispose();
        _generated = null;
    }
}
