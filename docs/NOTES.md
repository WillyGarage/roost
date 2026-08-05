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
| Move a window to a desktop | documented `IVirtualDesktopManager::MoveWindowToDesktop(hwnd, guid)`; fallback to internal `IVirtualDesktopManagerInternal::MoveViewToDesktop` | **Unverified cross-process. See Q1.** |
| Create a desktop | internal `CreateDesktopW`; fallback `Win+Ctrl+D` via SendInput | Versioned |
| Rename a desktop | write registry `Name`; plus internal `SetName` to force the Task View UI to refresh | Registry tier is stable |
| Switch desktop | internal `SwitchDesktop`; fallback N × `Win+Ctrl+←/→` | Versioned |
| Reorder ("insert after current") | internal `MoveDesktop` (exists only on 22H2+, where Task View gained drag-reorder) | Versioned. **See Q2.** |

Consequences:

1. **Probe for interfaces, never look up by build number.** Acquire the service from
   `CLSID_ImmersiveShell`, then try each known candidate IID in order until
   `QueryInterface` succeeds; cache the winner. Build-number tables are precisely how
   these tools die on Patch Tuesday.
2. **Every versioned operation needs a keystroke fallback,** so a broken update
   degrades the tool instead of killing it. The stable tier alone still gives us
   list + names + current-desktop, which is most of the palette.
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

## Open questions the spike must answer

**Q1 — Does documented `MoveWindowToDesktop` work cross-process?**
This is the load-bearing call for the entire acceptance criteria. There are
longstanding reports of it returning `E_ACCESSDENIED` for windows owned by other
processes, which is every window that matters here. If it fails, the core move has to
go through undocumented `MoveViewToDesktop` (which needs an `IApplicationView` for the
HWND, via `IApplicationViewCollection::GetViewForHwnd`), and the stability story
above gets meaningfully worse.
Test: launch Notepad, grab its HWND, try to move it.

**Q2 — Does `IVirtualDesktopManagerInternal::MoveDesktop` exist and work on 26200?**
Determines whether "insert new desktop after the current one" is achievable or whether
new desktops are stuck at the far right. Not a blocker for v1; the workflow is
tolerable without it since we address desktops by name.

**Q3 — Does writing the registry `Name` value alone refresh the Task View UI,**
or is internal `SetName` required? If the registry write suffices, renaming drops to
the stable tier.

**Q4 — Which IID set does 26200 accept** for `IVirtualDesktopManagerInternal`,
`IVirtualDesktop`, and `IApplicationViewCollection`? Record the winners here.

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
