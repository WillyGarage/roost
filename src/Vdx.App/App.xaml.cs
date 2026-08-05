using System.Windows;
using Vdx.Interop;

// This project enables both WPF and WinForms (the latter only for the tray icon and
// Screen), so several type names exist in both. Alias the WPF ones explicitly rather
// than fully qualifying every use site.
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace Vdx.App;

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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Closing the palette must not exit the app; there is no main window.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _singleInstance = new Mutex(initiallyOwned: true, "Vdx.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show(
                "Vdx is already running. Look for its icon in the notification area.",
                "Vdx", MessageBoxButton.OK, MessageBoxImage.Information);

            Shutdown();
            return;
        }

        Log.Init();

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

        if (!_desktops.CanMoveWindows)
            _tray.ShowError(
                $"Vdx cannot move windows on this Windows build. {_desktops.Unavailable} " +
                $"Listing and switching may still work. See the log for details.");

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
                if (!IsMovable(captured, out var why))
                {
                    _tray.ShowError(why!);
                    return;
                }

                OpenPalette(PaletteWindow.Mode.MoveWindow, captured);
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
    /// Rejects the shell's own windows. Without this, pressing the hotkey with the
    /// desktop or taskbar focused would try to move the shell, which either fails
    /// confusingly or misbehaves.
    /// </summary>
    private static bool IsMovable(IntPtr hWnd, out string? reason)
    {
        reason = null;

        if (hWnd == IntPtr.Zero)
        {
            reason = "There is no active window to move.";
            return false;
        }

        if (hWnd == Native.GetShellWindow())
        {
            reason = "The desktop itself cannot be moved. Focus an application window first.";
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
            reason = $"That is a Windows shell window ({cls}), not an application window.";
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
            _tray!.ShowError(why!);
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
    // lifecycle
    // -----------------------------------------------------------------------

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
