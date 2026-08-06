using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

// WinForms is enabled in this project for the tray icon, and System.Drawing defines its
// own Brush, FontFamily and Clipboard. Alias the WPF ones.
using Clipboard = System.Windows.Clipboard;
using Brush = System.Windows.Media.Brush;
using FontFamily = System.Windows.Media.FontFamily;

namespace Vdx.App;

/// <summary>
/// In-app documentation, opened from the tray menu.
///
/// Built in code rather than static XAML so that the hotkeys, file paths and status it
/// shows are the live ones, and so the config reference is generated from
/// <see cref="Config.Reference"/> instead of being a second copy that can drift.
/// </summary>
public partial class HelpWindow : Window
{
    private readonly Config _config;
    private readonly DesktopService _desktops;

    public HelpWindow(Config config, DesktopService desktops)
    {
        InitializeComponent();

        _config = config;
        _desktops = desktops;

        var version = typeof(HelpWindow).Assembly.GetName().Version;
        SubtitleText.Text =
            $"Move the active window to another virtual desktop.    " +
            $"v{version}    " +
            $"{(Log.IsElevated() ? "elevated" : "not elevated")}    " +
            $"{_desktops.List().Count} desktops";

        Build();
    }

    private void Build()
    {
        // ---- hotkeys -------------------------------------------------------
        Heading("Hotkeys");

        var anyHotkey = false;

        if (!string.IsNullOrWhiteSpace(_config.MoveWindowHotkey))
        {
            Row(_config.MoveWindowHotkey, "Open the palette and move the active window");
            anyHotkey = true;
        }

        if (!string.IsNullOrWhiteSpace(_config.SwitchDesktopHotkey))
        {
            Row(_config.SwitchDesktopHotkey, "Open the palette and switch desktop, moving nothing");
            anyHotkey = true;
        }

        if (!string.IsNullOrWhiteSpace(_config.SendToLastCreatedHotkey))
        {
            Row(_config.SendToLastCreatedHotkey, "Send the active window to the last desktop created");
            anyHotkey = true;
        }
        else
        {
            Row("(unbound)", "Send to last created desktop — set SendToLastCreatedHotkey to enable");
        }

        if (!anyHotkey)
            Note("No hotkeys are configured, so nothing will respond. Edit the config file.");

        Note("Key codes follow your active keyboard layout. On Dvorak, \"H\" is the physical " +
             "QWERTY-J key. Windows reserves many chords; run scripts\\probe-hotkeys.ps1 to " +
             "find free ones rather than guessing.");

        // ---- palette -------------------------------------------------------
        Heading("Inside the palette");

        Row("type", "Filter desktops. Matches prefix, then substring, then loose letters in order");
        Row("↑ ↓", "Move the selection. PageUp and PageDown jump further");
        Row("Enter", "Confirm. A single mouse click does the same");
        Row("Ctrl+Enter", $"Confirm, inverting follow-or-stay for this one action " +
                          $"(currently: {(_config.FollowWindowAfterMove ? "follow" : "stay")})");
        Row("Esc", "Cancel and hand focus back to where you were");

        Note("Type a name that matches no existing desktop and the last row offers to create " +
             "it. Choosing that creates the desktop, names it, positions it, moves your " +
             "window onto it, and follows. With an empty search box the list starts with " +
             "your most recent destinations.");

        // ---- config --------------------------------------------------------
        Heading("Config reference");
        Note($"File: {Config.FilePath}\nEdit it, save, then choose \"Reload config\" in the " +
             "tray menu. No restart needed. Delete the file to restore documented defaults.");

        foreach (var (key, defaultText, lines) in Config.Reference)
        {
            Setting(key, defaultText, string.Join(Environment.NewLine, lines));
        }

        // ---- locations -----------------------------------------------------
        Heading("Where things live");

        Row("Config", Config.FilePath);
        Row("Logs", Log.Directory);
        Row("State", System.IO.Path.Combine(Config.Directory, "state.json"));
        Row("Program", Environment.ProcessPath ?? "(unknown)");
        Row("Autostart", "Scheduled task \"Vdx\", at logon, highest privileges");

        Note("State holds your recent destinations and a backup of every desktop name it has " +
             "seen. Logs are one file per day, pruned after 14 days, and every failed " +
             "operation records its error code there.");

        // ---- troubleshooting ----------------------------------------------
        Heading("If something stops working");

        if (!_desktops.CanMoveWindows)
            Note("MOVING WINDOWS IS CURRENTLY DISABLED. " + (_desktops.Unavailable ?? "") +
                 "\nThis usually means a Windows update changed the interfaces Vdx relies on.");

        Setting("A hotkey does nothing",
            "",
            "Another application probably owns that chord. The log says so at startup.\n" +
            "Run scripts\\probe-hotkeys.ps1 to list what is free, then edit the config.");

        Setting("Windows will not move",
            "",
            "Windows exposes no supported API for this, so Vdx uses internal interfaces\n" +
            "whose identifiers change between Windows builds. A major update can break\n" +
            "them. Vdx checks at startup and disables moving rather than failing silently.\n" +
            "Diagnose with:  dotnet run --project spike\\Vdx.Spike -- --internal");

        Setting("One window refuses to move",
            "",
            "Windows refuses to move a window that is pinned to all desktops, and shell\n" +
            "windows like the desktop and taskbar are ignored on purpose. If the window\n" +
            "belongs to a program running as administrator, Vdx may need to be elevated\n" +
            "too; the autostart task already runs it elevated.");

        Setting("Desktop names disappeared",
            "",
            "An Explorer crash or a big Windows update can wipe the names Windows keeps.\n" +
            "Vdx backs them up, so they can be put back:\n" +
            "  dotnet run --project spike\\Vdx.Spike -- --restore-names          (preview)\n" +
            "  dotnet run --project spike\\Vdx.Spike -- --restore-names --apply  (write)");

        Setting("Other useful commands",
            "",
            "  --current            which desktop am I on\n" +
            "  --switch <name>      switch desktops without the app\n" +
            "  --internal           full capability report for this Windows build\n" +
            "All via:  dotnet run --project spike\\Vdx.Spike -- <command>");
    }

    // -----------------------------------------------------------------------
    // tiny layout helpers
    // -----------------------------------------------------------------------

    private void Heading(string text) => Body.Children.Add(new TextBlock
    {
        Text = text,
        FontSize = 16,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 20, 0, 8),
        Foreground = (Brush)FindResource("AccentBrush")
    });

    /// <summary>Two-column row: a short label and its explanation.</summary>
    private void Row(string label, string description)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new TextBlock
        {
            Text = label,
            FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
            Foreground = (Brush)FindResource("TextBrush"),
            TextWrapping = TextWrapping.Wrap
        };

        var right = new TextBlock
        {
            Text = description,
            Foreground = (Brush)FindResource("DimBrush"),
            TextWrapping = TextWrapping.Wrap
        };

        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);

        Body.Children.Add(grid);
    }

    private void Note(string text) => Body.Children.Add(new TextBlock
    {
        Text = text,
        Margin = new Thickness(0, 8, 0, 4),
        TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)FindResource("DimBrush")
    });

    /// <summary>A named setting with its default and multi-line explanation.</summary>
    private void Setting(string key, string defaultText, string body)
    {
        var header = new TextBlock
        {
            Margin = new Thickness(0, 12, 0, 2),
            Foreground = (Brush)FindResource("TextBrush"),
            TextWrapping = TextWrapping.Wrap
        };

        header.Inlines.Add(new Run(key)
        {
            FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
            FontWeight = FontWeights.SemiBold
        });

        if (!string.IsNullOrEmpty(defaultText))
            header.Inlines.Add(new Run($"    default: {defaultText}")
            {
                Foreground = (Brush)FindResource("DimBrush"),
                FontSize = 11
            });

        Body.Children.Add(header);

        Body.Children.Add(new TextBlock
        {
            Text = body,
            Margin = new Thickness(14, 0, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("DimBrush")
        });
    }

    // -----------------------------------------------------------------------
    // buttons
    // -----------------------------------------------------------------------

    private void OnOpenConfig(object sender, RoutedEventArgs e) => OpenInShell(Config.FilePath);

    private void OnOpenLogs(object sender, RoutedEventArgs e) => OpenInShell(Log.Directory);

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Everything useful for a bug report in one paste: build, elevation, desktop count,
    /// interface status, configured hotkeys, and the tail of today's log.
    /// </summary>
    private void OnCopyDiagnostics(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"Vdx {typeof(HelpWindow).Assembly.GetName().Version}");
        sb.AppendLine($"Windows {Environment.OSVersion.Version}");
        sb.AppendLine($"elevated: {Log.IsElevated()}");
        sb.AppendLine($"desktops: {_desktops.List().Count}");
        sb.AppendLine($"can move windows: {_desktops.CanMoveWindows}" +
                      (_desktops.CanMoveWindows ? "" : $" ({_desktops.Unavailable})"));
        sb.AppendLine($"move hotkey: {Or(_config.MoveWindowHotkey)}");
        sb.AppendLine($"switch hotkey: {Or(_config.SwitchDesktopHotkey)}");
        sb.AppendLine($"send-to-last hotkey: {Or(_config.SendToLastCreatedHotkey)}");
        sb.AppendLine();
        sb.AppendLine("--- last 40 log lines ---");

        try
        {
            var lines = System.IO.File.ReadAllLines(Log.CurrentFile);
            foreach (var line in lines.TakeLast(40))
                sb.AppendLine(line);
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(could not read the log: {ex.Message})");
        }

        try
        {
            Clipboard.SetText(sb.ToString());

            // Confirm on the button itself. Appending to the document would leave a
            // growing pile of notices at the bottom, one per click.
            if (sender is System.Windows.Controls.Button button)
            {
                var original = button.Content;
                button.Content = "Copied";

                var revert = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(2)
                };

                revert.Tick += (_, _) =>
                {
                    button.Content = original;
                    revert.Stop();
                };

                revert.Start();
            }
        }
        catch (Exception ex)
        {
            Log.Error("could not copy diagnostics", ex);
        }

        static string Or(string s) => string.IsNullOrWhiteSpace(s) ? "(unbound)" : s;
    }

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
}
