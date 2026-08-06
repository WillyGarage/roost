# Engineering notes

## Target machine

- Windows 11 **25H2**, build **26200.8875** (`10.0.26200.0`), x64
- 14 virtual desktops currently defined
- Laptop display + 1 external monitor. Windows switches virtual desktops on all
  monitors together; there is no per-monitor virtual desktop. Nothing to implement.
- PowerToys FancyZones is running. Irrelevant to this tool: moving a window between
  desktops does not change its position or size, so its zone is preserved.

Build 26200 is recent. Public virtual-desktop libraries (MScholtes/VirtualDesktop,
VirtualDesktopAccessor) maintain per-build interface-ID tables and may not have an
entry for it yet. Assume we resolve IIDs ourselves.

## Core design principle

Windows exposes **no public API** for enumerating, creating, naming, switching, or
reordering virtual desktops. The functionality exists only behind undocumented COM
interfaces whose IIDs change between Windows builds. Every tool in this space
eventually breaks on a Windows update for exactly this reason.

So: stratify by stability, and keep the operations in the core workflow on the stable
tier wherever possible.

| Capability | Mechanism | Stability |
|---|---|---|
| Enumerate desktops, in display order | `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops` → `VirtualDesktopIDs` (REG_BINARY, packed 16-byte GUIDs) | Stable. No COM. |
| Desktop names | `HKCU\...\Explorer\VirtualDesktops\Desktops\{GUID}` → `Name` (REG_SZ) | Stable. No COM. |
| Which desktop is current | documented `IVirtualDesktopManager::GetWindowDesktopId(hwnd)` on the HWND we captured at hotkey time — that window is by definition on the current desktop | Stable. Documented API. |
| Read which desktop a window is on | documented `IVirtualDesktopManager::GetWindowDesktopId` — works cross-process | Stable. Documented API. |
| **Move a window to a desktop** | internal `MoveViewToDesktop` + `IApplicationViewCollection::GetViewForHwnd`. The documented `MoveWindowToDesktop` **cannot** do this (see Q1) | **Versioned. No fallback exists.** |
| Create a desktop | internal `CreateDesktop`; fallback `Win+Ctrl+D` + registry diff | Versioned, with fallback |
| Rename a desktop | internal `SetDesktopName`; fallback registry `Name` write | Versioned, with fallback |
| Switch desktop | internal `SwitchDesktop`; fallback N × `Win+Ctrl+←/→` | Versioned, with fallback |
| Reorder ("insert after current") | internal `MoveDesktop(desktop, index)` | Versioned, no fallback (cosmetic only) |
| Is a window movable at all | internal `CanViewMoveDesktops` | Versioned |

Consequences:

1. **Probe for interfaces, never look up by build number.** Acquire the service from
   `CLSID_ImmersiveShell`, then try each known candidate IID in order until
   `QueryInterface` succeeds; cache the winner. Build-number tables are precisely how
   these tools die on Patch Tuesday.
2. **Every versioned operation gets a keystroke fallback where one exists,** so a
   broken update degrades the tool instead of killing it. Note the important
   exception: **there is no fallback for moving a window.** Windows ships no hotkey
   for it, so if the internal interface breaks, the tool's core function breaks with
   it. Create, rename, switch, list and names all survive. Plan for the interface
   check to run at startup and say so loudly rather than failing silently per action.
3. Because desktops are addressed by GUID and name, never by index, reordering
   desktops cannot corrupt anything we persist.

## COM interop hazard

`[ComImport]` interface declarations for these interfaces are **vtable-order
dependent**. A missing or misordered method does not throw; it silently calls the
wrong slot, giving wrong behavior or a hard crash. Every declaration must be
transcribed carefully against known-good sources, and `Vdx.Spike` exists to
smoke-test each method individually rather than discovering breakage inside the app.

## Findings

**2026-08-05 — registry tier confirmed working on 26200.8875.**
`Vdx.Spike` enumerates all 13 desktops in correct display order with names, using
registry reads only. No COM, not elevated. So the palette's list, ordering, names, and
unnamed-fallback labels are all on the stable tier as designed.

**Orphaned name subkeys exist.** `Desktops\` held 14 subkeys against 13 live desktops:
a deleted desktop leaves its name entry behind. The desktop list must therefore be
driven by the `VirtualDesktopIDs` order blob, with names joined in as a lookup.
Enumerating `Desktops\` subkeys directly would show phantom desktops. `DesktopRegistry`
already does this correctly.

**`CurrentVirtualDesktop` registry hint was accurate** at the time of the run, but it
is still only a hint; the documented `GetWindowDesktopId` path remains the plan.

**2026-08-05 — a successful `SwitchDesktop` can be silently undone.**
Symptom: picking a desktop in the switch palette appeared to do nothing, even though the
log showed `SwitchDesktop` returning S_OK.

Cause: `SwitchDesktop` changes the visible desktop but leaves the previously focused
window still holding the **foreground**, even though that window now lives on the desktop
we just left. A well-behaved Win32 window sits there quietly and the switch holds. A UWP
window, hosted by `ApplicationFrameHost`, asynchronously re-activates itself a moment
later, and activating a window on another desktop makes Windows switch back to it. So the
bug only reproduced when the active window was something like Settings, and never from
Notepad or Character Map. That intermittency is what made it look like the switch call
itself was failing.

Fix: after a successful switch, `SetForegroundWindow` the frontmost window already on the
destination desktop. That moves the foreground off the stale window, so its late
re-activation becomes a foreground steal by a background app, which Windows refuses.

Two consequences worth remembering:

- The palette must still own the foreground when this runs, because Windows only honours
  `SetForegroundWindow` from the process that currently has it. So the palette hides and
  acts, then closes; it must not close first.
- Move-and-follow never showed the bug because by the time anything re-activated the
  captured window, that window was already on the destination, so the re-activation
  pulled the desktop the right way.

`scripts\switch-bug.ps1` is the regression test. It runs the same flow twice, once with a
plain Win32 window focused and once with a UWP window, because only the second one
catches this.

## Spike results — all questions closed, 2026-08-05

Run `dotnet run --project spike\Vdx.Spike` for the read-only + keystroke round, and
`-- --internal` for the undocumented-interface round.

**Q1 — documented `MoveWindowToDesktop` cross-process: NO.**
Returns `E_ACCESSDENIED` (0x80070005) for a window owned by another process, verified
against a `charmap.exe` window. Reading is fine: `GetWindowDesktopId` and
`IsWindowOnCurrentVirtualDesktop` both work cross-process. So the documented API is
useful only as a *reader*, and the core move must use internal `MoveViewToDesktop`.

That was the plan's biggest hope and it is dead. The tool depends on undocumented COM
for its central operation, with no fallback. Everything else still degrades gracefully.

**Internal `MoveViewToDesktop`: YES.** `GetViewForHwnd` → `FindDesktop` →
`MoveViewToDesktop` moves another process's window correctly, verified by reading the
window's desktop back with the documented API. `CanViewMoveDesktops` returned true and
is the right pre-check for pinned or unmovable windows.

**Q2 — `MoveDesktop` reorder: YES.** A freshly created desktop was moved from position
14 to position 2, i.e. directly after the current desktop. "Insert after current" is
achievable; no far-right-then-drag needed. Indices are zero-based.

**Q3 — rename: YES, both ways.** Writing the registry `Name` value persists and reads
back. Internal `SetDesktopName` also works and is preferred, since going through the
shell keeps Task View in sync by construction (it writes the same registry value).
HSTRING note below.

**Q4 — IIDs accepted on build 26200.8875:**

| Interface | IID | Notes |
|---|---|---|
| `IVirtualDesktopManagerInternal` | `{53F5CA0B-158F-4124-900C-057158060B27}` | The 24H2 generation. Matches the vtable layout with `SwitchDesktopAndMoveForegroundView` at slot 8. |
| `IApplicationViewCollection` | `{1841C6D7-4F9D-42C0-AF41-8747538F10E5}` | Unchanged since Win10 1809. |
| `IVirtualDesktopPinnedApps` | `{4CE81583-1E4C-4632-A621-07A53543148F}` | For detecting/undoing "show on all desktops". |
| `IVirtualDesktop` | `{3F07F4BE-B107-441A-AF0F-39D82529072C}` | |
| `IApplicationView` | `{372E1D3B-38D3-42E4-A15B-8AB2B178F513}` | |
| `IObjectArray` | `{92CA9DCD-5622-4BBA-A805-5E9F541BD8C9}` | |

All four 22H2-and-earlier candidate IIDs returned `E_NOINTERFACE`, confirming the
probe-don't-guess approach was necessary. Note `{4970BA3D-FD4E-4647-BEA3-D89076EF4B9C}`
*also* QueryService'd successfully; it is not the interface we want and its layout is
unknown, so ignore it. Vtable layout cross-checked against MScholtes/VirtualDesktop
`VirtualDesktop11-24H2.cs` v1.21 (2025-08-11).

**Vtable sanity gate.** `GetCount` is slot 1 in every published version, so it is safe
to call before trusting the rest. `InternalSpike` compares it against the registry
desktop count and refuses to call another slot if they disagree. Keep that gate: it is
the cheap check that catches a wrong interface before a wrong call does damage.

**Additional wins beyond the original plan:**

- `SwitchDesktop` gives instant, animation-free switching in both directions, so the
  keystroke fallback is only ever a degraded mode.
- `CreateDesktop` creates *without* switching to it, unlike `Win+Ctrl+D`. That means
  "send this window to a new desktop and stay put" is possible, and the Ctrl-modifier
  behaviour in the spec comes free.

**HSTRING caveat.** `SetDesktopName` and `IVirtualDesktop::GetName` take/return
`HSTRING`. Built-in `UnmanagedType.HString` marshalling was removed in .NET 5, so the
parameter is declared `IntPtr` and the string is created with `WindowsCreateString` /
freed with `WindowsDeleteString` from `combase.dll`. `GetName` is simply not declared;
names come from the registry, which needs no COM.

## Hotkey availability, 2026-08-05

`Win+Ctrl+M` from the original spec **cannot be used**: Windows reserves it for
Magnifier settings, and `RegisterHotKey` returns error 1409. Also taken on this machine:
`Win+Ctrl+N/S/D/C/L/F/V/O`, `Win+Ctrl+Space`, `Win+Ctrl+Enter`, `Win+Alt+M`,
`Win+Alt+K`, `Win+Alt+Space`, `Win+Shift+M`, `Ctrl+Alt+K`.

Free in the `Win+Ctrl` family: `H T G R B W Z` (plus `J K U Y I`, which are left-hand
on Dvorak).

Current defaults. The machine uses a **Dvorak** layout, so the letters are chosen for
where they land on Dvorak, not QWERTY. `H` and `T` are the right hand's index and middle
fingers on the home row. Staying inside `Win+Ctrl` keeps them in Windows' own
virtual-desktop modifier family (`Win+Ctrl+D`, `Win+Ctrl+F4`, `Win+Ctrl+arrows`).

| Chord | Dvorak position | Action |
|---|---|---|
| `Win+Ctrl+H` | right index, home row | palette: move the active window |
| `Win+Ctrl+T` | right middle, home row | palette: switch desktop, no move |
| *(unbound)* | | send active window to the last desktop created |

**Virtual-key codes follow the active keyboard layout.** `"H"` in the config means the
key that types `h` under the current layout, which on Dvorak is the physical QWERTY-J
position. Switching the layout to QWERTY would move these chords to the left hand.

Send-to-last-created is implemented but unbound: in practice typing two or three
characters into the palette is already fast enough. Set a chord to re-enable.

Run `scripts\probe-hotkeys.ps1` to re-check on any machine. Do not guess.

`RegisterHotKey` is used rather than a low-level keyboard hook, deliberately: it never
sees keystrokes it did not claim, so it cannot leak input or swallow keys. The cost is
that it handles keyboard chords only, which is why the mouse side button is not bound
by default. `XButton1` is browser Back, and a low-level mouse hook would have to
swallow it globally to claim it. The clean answer is to map a spare mouse button to one
of these chords in the mouse's own vendor software.

## Deferred / known limitations

- **Elevation.** Moving a window owned by an elevated process requires this app to be
  elevated too (UIPI blocks it otherwise). That rules out the `Run` registry key for
  autostart, which would show a UAC prompt every logon. Use a Scheduled Task set to
  run at logon with highest privileges.
- **Logging is not optional.** An elevated resident app has no console to watch, so
  file logging goes in from day one.
- **Pinned windows.** A window set to "show on all desktops" silently ignores a move.
  Detect via `IVirtualDesktopPinnedApps` (or the move being a no-op) and unpin first,
  otherwise the tool looks broken.
- **XButton1 is browser Back.** Swallowing it with a low-level mouse hook breaks Back
  globally. Preferred solution is to map a spare mouse button to an obscure keyboard
  chord in the mouse vendor's own software and bind that; otherwise require a modifier
  alongside XButton1.
- Explorer crashes and major Windows updates can scramble desktop order and
  occasionally drop names. Keep a GUID → name JSON sidecar so names can be restored.
- Capture the foreground HWND **before** showing the palette. The palette must take
  focus in order to receive typing.

## Scope decisions

In v1, beyond the base workflow:

- Type-to-search palette is the primary UI, not a popup menu. A 14-item menu
  reproduces the scrolling problem the tool is meant to eliminate.
- Recent destinations sorted to the top of an empty query.
- Send-to-last-created-desktop on its own hotkey. The real workflow is "create Project
  A, then send five more windows there".
- Switch-only mode (same palette, no window move) as a Win+Tab replacement.

Deferred out of v1:

- "Move all windows of the same process." Chrome and Electron apps expose many hidden
  and helper top-level windows; doing this blind is a footgun. Needs a confirmation
  step listing exactly what will move.
- Multi-window marking mode.
