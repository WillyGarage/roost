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

## Status

Pre-spike. Nothing works yet. See docs/NOTES.md for the open questions that
determine the final architecture.
