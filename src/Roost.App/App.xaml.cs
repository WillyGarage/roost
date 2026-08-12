using System.Windows;
using System.Windows.Threading;
using Roost.Interop;

// This project enables both WPF and WinForms (the latter only for the tray icon and
// Screen), so several type names exist in both. Alias the WPF ones explicitly rather
// than fully qualifying every use site.
using Application = System.Windows.Application;

namespace Roost.App;

/// <summary>
/// Tray-resident host. Owns the services, wires the hotkeys to the palette, and is the
/// only place that decides whether a given foreground window is something we should
/// act on.
/// </summary>
public partial class App : Application
{
    /// <summary>Guards against a second copy fighting over the same hotkeys.</summary>
    private static Mutex? _singleInstance;

    private Config _config = new();
    private AppState _state = new();
    private DesktopService? _desktops;
    private TrayHost? _tray;
    private HotkeyService? _hotkeys;
    private PaletteWindow? _palette;
    private HelpWindow? _help;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Closing the palette must not exit the app; there is no main window.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Logging comes first so a second instance losing the race is visible in the log.
        // It used to be initialised after the check, which made that case leave no trace
        // at all: the logon task appeared to start and stop for no reason.
        Log.Init();

        _singleInstance = new Mutex(initiallyOwned: true, "Roost.SingleInstance", out var isFirst);

        if (!isFirst)
        {
            // Exit quietly. This deliberately does NOT show a dialog: the app is normally
            // started by a logon scheduled task, and a modal message box there blocks the
            // process indefinitely behind a window the user may never find, possibly on
            // another virtual desktop. The already-running instance owns the tray icon and
            // the hotkeys, so there is nothing useful for this one to say.
            Log.Warn("another instance already holds the single-instance lock, exiting");
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("unhandled UI exception", args.Exception);
            _tray?.ShowError($"Something went wrong: {args.Exception.Message}");
            args.Handled = true; // a resident tray app should survive a bad action
        };

        _config = Config.Load();
        _state = AppState.Load();

        _desktops = new DesktopService();

        var status = _desktops.CanMoveWindows
            ? $"{_desktops.List().Count} desktops"
            : "moving windows unavailable";

        _tray = new TrayHost(_config, status);
        _tray.ExitRequested += () => Shutdown();
        _tray.ReloadRequested += Reload;
        _tray.HelpRequested += ShowHelp;

        if (!_desktops.CanMoveWindows)
        {
            Log.Warn($"desktop interfaces unavailable at startup: {_desktops.Unavailable}; will retry");
            ScheduleRetry();
        }

        RegisterHotkeys();

        Log.Info("startup complete");
    }

    // -----------------------------------------------------------------------
    // hotkeys
    // -----------------------------------------------------------------------

    private void RegisterHotkeys()
    {
        _hotkeys?.Dispose();
        _hotkeys = new HotkeyService();
        _hotkeys.Pressed += OnHotkey;

        var failures = new List<string>();

        void Bind(string chord, HotkeyAction action)
        {
            var error = _hotkeys!.Register(chord, action);
            if (error is not null)
                failures.Add(error);
        }

        Bind(_config.MoveWindowHotkey, HotkeyAction.MoveWindow);
        Bind(_config.SwitchDesktopHotkey, HotkeyAction.SwitchDesktop);
        Bind(_config.SendToLastCreatedHotkey, HotkeyAction.SendToLastCreated);

        if (failures.Count > 0)
            _tray?.ShowError(
                "Some hotkeys could not be registered, most likely because another app " +
                "already owns them. Edit the config file and reload. " + string.Join("; ", failures));
    }

    private void OnHotkey(HotkeyAction action)
    {
        if (_desktops is null || _tray is null)
            return;

        // Capture BEFORE anything of ours can take focus. Everything downstream works
        // from this HWND, never from "whatever is in front now".
        var captured = Native.GetForegroundWindow();

        Log.Info($"hotkey {action}, foreground 0x{captured:X} " +
                 $"\"{Native.GetWindowTitle(captured)}\" [{Native.GetWindowClass(captured)}]");

        switch (action)
        {
            case HotkeyAction.MoveWindow:
                // If there is nothing sensible to move (the desktop was focused, say),
                // open the same palette in go-there mode rather than refusing with an
                // error. The palette can do everything from there anyway, so a dead end
                // would just be rude.
                if (IsMovable(captured, out var why))
                {
                    OpenPalette(PaletteWindow.Mode.MoveWindow, captured);
                }
                else
                {
                    Log.Info($"nothing movable in the foreground ({why}), opening in go-there mode");
                    OpenPalette(PaletteWindow.Mode.SwitchDesktop, captured);
                }
                break;

            case HotkeyAction.SwitchDesktop:
                // No window is being moved, so an unusable foreground window is fine.
                OpenPalette(PaletteWindow.Mode.SwitchDesktop, captured);
                break;

            case HotkeyAction.SendToLastCreated:
                SendToLastCreated(captured);
                break;
        }
    }

    /// <summary>
    /// Whether the foreground window is something worth trying to move. Rejects the
    /// shell's own windows, since pressing the hotkey with the desktop or taskbar focused
    /// would otherwise try to move the shell and either fail confusingly or misbehave.
    ///
    /// <paramref name="reason"/> is a sentence fragment describing what it is instead
    /// ("the desktop itself"), for callers to compose into a message or a log line.
    /// </summary>
    private static bool IsMovable(IntPtr hWnd, out string? reason)
    {
        reason = null;

        if (hWnd == IntPtr.Zero)
        {
            reason = "no active window";
            return false;
        }

        if (hWnd == Native.GetShellWindow())
        {
            reason = "the desktop itself";
            return false;
        }

        // Our own windows: the help window in particular. Moving the palette's own app
        // between desktops is never what anyone meant.
        Native.GetWindowThreadProcessId(hWnd, out var owner);
        if (owner == (uint)Environment.ProcessId)
        {
            reason = "one of Roost's own windows";
            return false;
        }

        var cls = Native.GetWindowClass(hWnd);

        // Progman/WorkerW are the desktop, Shell_TrayWnd and friends are the taskbar,
        // and the XamlExplorerHost classes are Task View and the Alt-Tab switcher.
        string[] shellClasses =
        [
            "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
            "MultitaskingViewFrame", "XamlExplorerHostIslandWindow",
            "Windows.UI.Core.CoreWindow", "ForegroundStaging"
        ];

        if (shellClasses.Contains(cls, StringComparer.OrdinalIgnoreCase))
        {
            reason = $"a Windows shell window ({cls})";
            return false;
        }

        return true;
    }

    private void OpenPalette(PaletteWindow.Mode mode, IntPtr captured)
    {
        // Pressing the hotkey again while the palette is up should dismiss it, not stack
        // a second one.
        if (_palette is not null)
        {
            _palette.Close();
            _palette = null;
            return;
        }

        _palette = new PaletteWindow(
            _desktops!, _config, _state, mode, captured, message => _tray!.ShowError(message));

        _palette.Closed += (_, _) => _palette = null;
        _palette.ShowPalette();
    }

    private void SendToLastCreated(IntPtr captured)
    {
        if (!IsMovable(captured, out var why))
        {
            _tray!.ShowError($"Nothing to move: the active window is {why}.");
            return;
        }

        var target = _state.LastCreatedDesktop;

        if (target is null)
        {
            _tray!.ShowError(
                $"No desktop has been created yet. Use {_config.MoveWindowHotkey} and type a " +
                "name to create one, then this shortcut sends further windows to it.");
            return;
        }

        // The desktop may have been closed since we recorded it.
        if (_desktops!.List().All(d => d.Id != target))
        {
            _tray!.ShowError("The last desktop you created no longer exists.");
            _state.LastCreatedDesktop = null;
            _state.Save();
            return;
        }

        if (_desktops.MoveWindow(captured, target.Value, _config.FollowWindowAfterMove, out var error))
            _state.RecordDestination(target.Value, _config.RecentDestinationCount);
        else
            _tray!.ShowError(error ?? "Could not move the window.");
    }

    // -----------------------------------------------------------------------
    // startup retry
    // -----------------------------------------------------------------------

    private void ScheduleRetry(int attempt = 0)
    {
        const int maxAttempts = 10;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _desktops?.Dispose();
            _desktops = new DesktopService();

            if (_desktops.CanMoveWindows)
            {
                Log.Info($"desktop interfaces available after {attempt + 1} retries");
                _tray?.SetStatus($"{_desktops.List().Count} desktops");
            }
            else if (attempt + 1 < maxAttempts)
            {
                Log.Warn($"retry {attempt + 1}/{maxAttempts}: still unavailable: {_desktops.Unavailable}");
                ScheduleRetry(attempt + 1);
            }
            else
            {
                Log.Error($"desktop interfaces still unavailable after {maxAttempts} retries: {_desktops.Unavailable}");
                _tray?.ShowError(
                    $"Roost cannot move windows on this Windows build. {_desktops.Unavailable} " +
                    "Listing and switching may still work. See the log for details.");
            }
        };
        timer.Start();
    }

    // -----------------------------------------------------------------------
    // lifecycle
    // -----------------------------------------------------------------------

    /// <summary>
    /// Opens the help window, or brings the existing one forward. Built fresh each time so
    /// it reflects the current config after a reload.
    /// </summary>
    private void ShowHelp()
    {
        if (_help is not null)
        {
            _help.Activate();
            return;
        }

        _help = new HelpWindow(_config, _desktops!);
        _help.Closed += (_, _) => _help = null;
        _help.Show();
        _help.Activate();
    }

    private void Reload()
    {
        Log.Info("reloading config");

        _config = Config.Load();
        RegisterHotkeys();

        _tray?.SetStatus(_desktops?.CanMoveWindows == true
            ? $"{_desktops.List().Count} desktops"
            : "moving windows unavailable");

        _tray?.ShowError("Config reloaded.");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("shutting down");

        _hotkeys?.Dispose();
        _tray?.Dispose();
        _desktops?.Dispose();

        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();

        base.OnExit(e);
    }
}
