# bill_virtual_desktops

A resident Windows 11 tray utility that moves the active window to another virtual
desktop from a type-to-search palette, and creates + names new desktops on the fly.

`Vdx` is a placeholder assembly prefix; renaming it later is a find/replace.

## Why this exists

Splitting a pile of unrelated windows on Desktop 1 into per-project desktops currently
means Win+Tab, horizontal scrolling through 14 desktops, creating a desktop at the far
right, renaming it, dragging it left, then dragging windows onto it. This replaces all
of that with: hotkey, type part of a desktop name, Enter.

## Layout

```
src/Vdx.Interop/    Virtual-desktop access layer. Registry reads + COM interop,
                    stratified by stability (see docs/NOTES.md). No UI.
src/Vdx.App/        WPF tray app: hotkey/mouse hooks, palette window, config, logging.
spike/Vdx.Spike/    Console harness for probing COM interfaces and verifying which
                    operations actually work on a given Windows build. Kept in the
                    repo permanently: it is the diagnostic to run after a Windows
                    update breaks something.
scripts/            Build, publish, and autostart-task install/uninstall.
docs/NOTES.md       Findings, registry paths, build-specific IIDs, open questions.
```

## Build

Requires the .NET 9 SDK on **Windows** (not WSL). WPF cannot be built on Linux, and
the shipped .exe must live on the Windows filesystem because it autostarts at logon.

```powershell
cd C:\dev\bill_virtual_desktops
dotnet build
dotnet run --project spike\Vdx.Spike        # capability report for this machine
```

Release, single self-contained .exe:

```powershell
.\scripts\publish.ps1
```

## Target environment

Recorded from the dev machine, 2026-08-05:

| | |
|---|---|
| Windows | 11 25H2, build 26200.8875 |
| Desktops in use | 14 |
| Displays | laptop + 1 external (virtual desktops switch both together) |
| Also running | PowerToys FancyZones (orthogonal; moving a window between desktops preserves its zone) |

## Install

```powershell
.\scripts\publish.ps1              # builds dist\Vdx.exe (single file, ~71 MB)
.\scripts\install-autostart.ps1    # ELEVATED PowerShell: logon task + starts it now
```

The autostart step needs an elevated PowerShell because it registers a scheduled task
that runs with highest privileges. That elevation is not decoration: moving a window
owned by an elevated process requires this app to be elevated too. A manually launched
(non-elevated) instance works fine for everything except admin-owned windows.

`scripts\uninstall-autostart.ps1` removes the task and stops the process.

## Usage

| Chord | What it does |
|---|---|
| `Win+Ctrl+H` | Palette: move the active window to a desktop |
| `Win+Ctrl+T` | Palette: switch desktop, moving nothing |

Letters chosen for a **Dvorak** layout: `H` and `T` are the right hand's index and middle
fingers on the home row. Virtual-key codes follow the active layout, so these move to the
left hand if the layout changes to QWERTY.

In the palette: type to filter, `Enter` or a single click to confirm, `Ctrl+Enter` to
invert the follow-or-stay behaviour for one action, `Esc` to cancel and hand focus back.

Typing a name that matches no existing desktop offers to create it. Choosing that
creates the desktop, names it, positions it directly after the one you are on, moves
your window onto it, and follows. That is the whole "split this project out" flow in
one gesture.

An empty search box lists recently used destinations first.

Right-click the tray icon for the hotkey list, the config file, and the log folder.

Defaults live in `%APPDATA%\Vdx\config.json` and are re-read by "Reload config" in the
tray menu. `Win+Ctrl+M` from the original brief is **not** used because Windows reserves
it for Magnifier settings; run `scripts\probe-hotkeys.ps1` before choosing replacements.

There is also a "send the active window to the last desktop you created" action. It is
implemented but left unbound, because typing two or three characters into the palette
turned out to be quick enough. Set `SendToLastCreatedHotkey` in the config to enable it.

## Status

Working end to end on Windows 11 25H2 (26200.8875), verified by
`scripts\smoke-test.ps1 -Commit -TestCreate` and `scripts\switch-bug.ps1`:

- move the active window to an existing desktop, and follow it there
- create a desktop, name it, insert it after the current one, move the window onto it
- switch desktops with no move
- errors surface as tray balloons and always land in the log

Not yet built: mouse-button binding, marking several windows to move together, moving
every window of one application. See the deferred list in docs/NOTES.md.
