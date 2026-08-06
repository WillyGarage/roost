# bill_virtual_desktops

A resident Windows 11 tray utility that moves the active window to another virtual
desktop from a type-to-search palette, and creates + names new desktops on the fly.

`Vdx` is a placeholder assembly prefix; renaming it later is a find/replace.

## Requirements

- Windows 11. Built and tested on 25H2 (build 26200); the 22H2/23H2 interface generation
  is not currently in the candidate list, so older builds may need one added.
- .NET 9 SDK, on Windows, to build. WPF cannot be built on Linux.
- Nothing to install to run: the published exe is self-contained.

## A caveat worth reading first

Windows exposes **no supported API** for enumerating, creating, naming, switching or
reordering virtual desktops. Only moving a window has a documented call, and that one
refuses to touch windows owned by other processes, which is every window this tool exists
to move.

So the interesting half of this runs on undocumented COM interfaces whose identifiers
change between Windows builds. That is a deliberate, understood dependency, not an
oversight: it is the only way the feature exists at all. The design confines it, probes for
interfaces instead of hardcoding build numbers, keeps a keystroke fallback wherever Windows
provides one, and fails loudly at startup rather than silently mid-action. See
[docs/NOTES.md](docs/NOTES.md) for the full stability analysis, the traps found along the
way, and what to do when an update breaks something.

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
| Desktops in use | ~20 |
| Keyboard layout | Dvorak (this drives the default hotkey letters) |
| Displays | laptop + 1 external (virtual desktops switch both together) |
| Also running | PowerToys FancyZones (orthogonal; moving a window between desktops preserves its zone) |

The test scripts derive the desktop names they use at runtime, so they are not tied to this
layout and will run anywhere with at least two desktops.

## License

MIT. See [LICENSE](LICENSE).

## Install

```powershell
.\scripts\publish.ps1     # builds dist\Vdx.exe (single file, ~71 MB)
```

Then set up autostart. Easiest is the `.cmd` wrapper, which self-elevates and bypasses
the execution policy for its own process only:

```
scripts\install-autostart.cmd     (approve the UAC prompt)
```

Both of those are needed and neither is obvious. Registering a highest-privileges
scheduled task requires admin, and Windows' default execution policy refuses to run a
`.ps1` from disk at all, so calling the PowerShell script directly fails with
`running scripts is disabled on this system` even from an elevated prompt. To do it by
hand instead:

```powershell
# in an ELEVATED PowerShell
Set-ExecutionPolicy -Scope Process Bypass -Force
.\scripts\install-autostart.ps1
```

The task runs at highest privileges. That *may* matter for moving windows owned by
elevated processes, though see the elevation note in docs/NOTES.md: the requirement is
assumed, not verified. It costs nothing either way.

`scripts\uninstall-autostart.cmd` removes the task and stops the process.

## Usage

**`Win+Ctrl+H`** is the only hotkey you need. It opens the palette, and every desktop
operation is reachable from there. `Win+Ctrl+T` opens the same palette with `Enter` set to
go-there instead of move; it is a convenience, not a second UI, and can be unbound.

Letters chosen for a **Dvorak** layout: `H` and `T` are the right hand's index and middle
fingers on the home row. Virtual-key codes follow the active layout, so these move to the
left hand if the layout changes to QWERTY.

The list shows every desktop, growing to whatever the monitor can display before it has to
scroll. The number on the right of each row is the window count on that desktop; zero marks
a candidate for deleting.

Every key is printed along the bottom of the palette, so nothing has to be looked up:

| Key | What it does |
|---|---|
| type | Filter desktops. Prefix, then substring, then loose letters in order |
| `↑` `↓` | Move the selection. PageUp/PageDown jump further |
| `Enter` | Move the active window there and follow it. A single click does the same |
| `Ctrl+Enter` | Same, inverting follow-or-stay for this one action |
| `Alt+Enter` | Just go to that desktop, moving nothing |
| `F2` | Rename. The search box becomes the edit field, pre-filled |
| `Ctrl+↑` `Ctrl+↓` | Move the desktop one position earlier or later, updating live |
| `Alt+Delete` | Delete, after asking where its windows should go |
| `Esc` | Back one step, or cancel from the list and hand focus back |

Deleting never closes windows. It asks which desktop they should move to, defaulting to the
first one, and that list filters like any other. The palette stays open after a rename,
reorder or delete so several can be done in one visit.

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

## Operations

**Rebuilding after a code change.** `scripts\publish.ps1` stops any running instance
first (a live process holds `dist\Vdx.exe` open and the publish would fail partway), and
restarts the logon task afterwards if one is registered. So a rebuild is just:

```powershell
.\scripts\publish.ps1
```

Note the logon task points at `C:\dev\bill_virtual_desktops\dist\Vdx.exe`. Moving or
deleting the repo breaks autostart; re-run `install-autostart.ps1` after relocating it.

**If Windows loses your desktop names.** An Explorer crash or a major Windows update can
wipe the names in HKCU, leaving a row of "Desktop 7". The app snapshots GUID-to-name into
`%APPDATA%\Vdx\state.json` on every create and every palette open, so:

```powershell
dotnet run --project spike\Vdx.Spike -- --restore-names          # shows what it would do
dotnet run --project spike\Vdx.Spike -- --restore-names --apply  # writes them back
```

It only touches desktops that still exist, so deleted ones are never resurrected.

**If a Windows update breaks it.** The undocumented interface IDs change between builds.
The app checks at startup and, if the check fails, shows a tray balloon and disables
moving windows rather than failing silently per action. To diagnose:

```powershell
dotnet run --project spike\Vdx.Spike -- --internal
```

That reports which interface IDs this build accepts. Add the new one to the candidate
list in `src\Vdx.Interop\Com.cs` and check the vtable layout in `ComInternal.cs` against
a maintained reference. There is no fallback for moving windows: Windows ships no hotkey
for it, so that capability is the one thing an update can genuinely take away.

**Other useful spike modes.** All read-only except `--delete`.

```powershell
dotnet run --project spike\Vdx.Spike -- --list             # desktops with window counts
dotnet run --project spike\Vdx.Spike -- --list --windows   # ...and the window titles
dotnet run --project spike\Vdx.Spike -- --current          # which desktop am I on
dotnet run --project spike\Vdx.Spike -- --switch Comm      # switch, bypassing the app
dotnet run --project spike\Vdx.Spike -- --delete "Name"    # remove a stray desktop
```

`--list` is the one to reach for if the window counts in the palette ever look wrong; it
computes them the same way and can print the titles behind each number.

**Test scripts.** Each drives the real published exe with synthetic keystrokes and asserts
against the registry, then puts the machine back as it found it.

```powershell
.\scripts\smoke-test.ps1 -Commit -TestCreate   # move, follow, create-name-position-move
.\scripts\switch-bug.ps1                       # switching sticks, Win32 and UWP focused
.\scripts\manage-desktops.ps1                  # create, rename, reorder, delete
.\scripts\probe-hotkeys.ps1                    # which chords are free on this machine
```

**Logs** are in `%LOCALAPPDATA%\Vdx\logs`, one file per day, pruned after 14 days.
Every failed operation records its HRESULT there.

## Status

Working end to end on Windows 11 25H2 (26200.8875), verified by the test scripts above:

- move the active window to an existing desktop, and follow it there
- create a desktop, name it, insert it after the current one, move the window onto it
- switch desktops with no move, and have it stick
- rename, reorder and delete desktops from the palette
- window counts per desktop
- in-app help, and a config file that documents itself
- errors surface as tray balloons and always land in the log

Not yet built: find-a-window-across-all-desktops, mouse-button binding, marking several
windows to move together, moving every window of one application. See the deferred list in
docs/NOTES.md.
