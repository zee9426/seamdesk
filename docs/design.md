# Seamdesk design

Seamdesk shares one mouse and keyboard between Windows machines, screen by screen. It is an independent, open-source
alternative to Microsoft's Mouse Without Borders (MWB) that can also talk to it. This file records decisions. MWB citations are paths under
`src/modules/MouseWithoutBorders/App/` in PowerToys (at `a9e1f0f20^` unless another commit is named), or PowerToys
issues (#n). Win32 claims cite the Microsoft documentation of the API named,
or a run on a Windows 11 PC ("PC run").

## Scope

- **Open source, general case.** The repo, the app and its namespaces are all Seamdesk (first called Glide). One user's
  answers are their settings and defaults, never product limits: any number of machines, and any of them may drive;
  per-peer clipboard and compatibility options; optional auto-pause. The repo holds no personal details.
- **Windows 11 only** (TLS 1.3 in Schannel needs it). Seamdesk.Core and Seamdesk.Transport stay plain net10.0, so other
  systems can be added later.
- .NET 10, C#. UI: **WPF**, in the .NET Windows Desktop runtime with no Windows App SDK, so a per-user install is one
  self-contained folder; the tray icon uses WinForms' `NotifyIcon` from the same runtime. Packages come from nuget.org
  only (`nuget.config` clears every other source).

| Project | Target | Contents |
|---|---|---|
| `src/Seamdesk.Core` | net10.0 | Desk layout, edge crossing, movement, move coalescing. No Win32, no I/O. |
| `src/Seamdesk.Transport` | net10.0 | Identity, pairing, TLS 1.3 channel, frames, version handshake, reconnect supervisor. BouncyCastle (J-PAKE) is its only package. |
| `src/Seamdesk.Host` | net10.0 | Platform-neutral host logic: who has the cursor (`Cursor`), the peers and every rule about them (`Roster`), the desk the settings describe (`ConfiguredDesk`), hotkeys, monitor mapping, settings, the layout editor's maths (`LayoutEditor`). No Win32. |
| `src/Seamdesk.Mwb` | net10.0 | MWB compatibility: both wire formats (before 0.101 and 0.101+) with detection, handshake, a link per MWB peer behind `IPeer`, driving or driven. No Win32, no packages. |
| `src/Seamdesk.App` | net10.0-windows | The single per-user exe: hooks, injection, hidden cursor, monitors, DPAPI, system events, tray, settings window and layout editor (WPF Fluent, `docs/ui.md`). |
| `tests/*.Tests` | as above | Core, Transport and Host tests run on Linux and Windows; `Seamdesk.App.Tests` builds everywhere, runs on Windows only. |
| `tools/Smoke` | net10.0-windows | The interactive check behind `tools/smoke.ps1`. |
| `tools/Shots` | net10.0-windows | Renders every window and state offscreen to `docs/screenshots/` (light and dark, 100 % and 150 %). |
| `tools/mwb-vectors` | net10.0 | Not in the solution: runs `Seamdesk.Mwb` against MWB's own code, writes the test vectors. |

One process per machine, in the user's session. Every machine runs the same build in the same role.

## Layout model (`Seamdesk.Core.Desk`)

- **Desk:** one shared plane. One desk unit = one pixel of whichever monitor covers that spot.
- **Machine:** a name, an `Offset` (where its desktop origin sits on the desk), and its monitors as `Rect`s in its
  own desktop coordinates, in physical pixels, exactly as Windows reports them (Per-Monitor-V2 aware, as MWB is).
- A machine's monitors keep Windows' own arrangement; the layout editor places whole machines (one offset each). To
  rearrange a machine's own monitors, use Windows' display settings.
- **Neighbours** are not stored. They are derived at the moment of crossing: the monitor of another machine that owns
  the desk pixel just past the edge. So stacked, side-by-side, offset and portrait arrangements all work, and two
  monitors of one machine can touch different machines. MWB has a 2x2 machine matrix (`Core/MachineStuff.cs:536-673`)
  and one bounding box per machine (`Core/MachineStuff.cs:239-256`).
- Each machine stores the whole desk. Peers send their monitor list (`Monitors`) when a link comes up and on every
  `WM_DISPLAYCHANGE`. A layout edit is pushed to every peer with a revision number; the highest revision wins.
- The editor rejects placements where monitors of different machines overlap (`LayoutEditor.Problem`); `Desk.Cross`
  would take the first match.
- Mixed pixel pitch means desk alignment is in pixels, not millimetres, as Windows does between local monitors.
- **Saved layouts per peer.** A peer that cannot send its monitor list (an MWB peer reports only its outer box) gets
  several saved layouts, entered in the editor. When its outer box changes, Seamdesk picks the saved layout with that
  box. If none matches, it treats the box as one screen, shows a tray notice ("<peer>'s screens changed: check
  layout") and keeps working.

## Edge crossing (`Desk.Cross`)

Given a machine, a cursor point on one of its monitors and a direction: if one of the machine's own monitors owns
the next pixel, Windows moves the cursor locally (none). Otherwise the next pixel in desk coordinates (`+ Offset`)
is looked up; if a monitor of another machine contains it, the cursor arrives at that same desk pixel, converted to
that machine's coordinates (`- other.Offset`). Else the cursor stays. The arrival keeps the physical position along
the edge, so there is no arrival jump. `Desk.Move` then puts it 2 pixels further in (MWB's `JUMP_PIXELS`,
`Core/MachineStuff.cs:33-34`), so a hand sliding along the shared edge with a pixel of jitter doesn't flip the cursor
back and forth (seen as three crossings and returns at one edge in under 0.4 s). MWB tests the bounding-box edge (`Core/MachineStuff.cs:239-256`) and maps
arrival proportionally (`:181-190`): missed crossings from narrower stacked monitors and the jump of #41720.

## Movement model

- **Deltas, never absolute 0..65535.** While another machine has the cursor, the controller's low-level mouse hook
  swallows each move and sends `delta = Pt - parkPoint`, as MWB does (`Class/InputHook.cs:287-288`). `Pt` is after
  the controller's own ballistics, so acceleration is applied once. MWB's absolute mode stretches the controller's
  primary over the remote's bounding box (#27450).
- **Applied 1:1 by the holder** (the machine that has the cursor, `Desk.Move`). It keeps a tracked position, adds
  the delta and injects the result as an absolute move. MWB's relative mode lets the receiver accelerate again
  (#27450).
- **Dead zones:** a target outside every local monitor is clamped to the nearest pixel of any local monitor, then
  `Desk.Cross` is tried on each axis that overshot (x first). The overshoot is dropped on arrival.
- **Edge detection runs on the computed target**, never on a `GetCursorPos` read after injection (#27783).
- **Handoff.** When the holder's `Desk.Move` lands on another machine, it sends `Handoff(machine, point)` to the
  controller, which routes later deltas there (and sends it the `Handoff`), or, if it is the target, shows its own
  cursor at `point`. The keyboard follows the cursor. Leaving a holder releases every key and button its controller
  left down, so nothing sticks. A drag (a button held by the controller) stops at the edge instead of crossing.
- **Resync.** A holder whose own mouse moves follows it (`tracked = Pt`), so the local touchpad never fights Seamdesk.
- **Coalescing.** The hook thread only adds deltas (`MoveCoalescer`). One effects loop drains `Cursor.TakeEffects()`
  and awaits each send in turn; moves arriving meanwhile are summed into one `PointerMove`. A pending move is always
  queued ahead of a button, wheel or key, so a click lands where the pointer is. This replaces the planned 1 ms tick:
  waits run at the system timer resolution unless the process raises it with `timeBeginPeriod` (Sleep docs).
  Known limit: no rate cap; add a high-resolution waitable timer if an 8 kHz mouse costs measurable CPU. MWB sends every
  event on the hook thread (#51077, #36878).

## Windows host (`Seamdesk.App`)

- **App decides nothing about peers.** `App` translates Windows events in (hooks, displays, power, session, network,
  the clipboard, DPAPI) and executes the cursor's effects out. Everything per peer is `Seamdesk.Host.Roster`, tested on
  Linux over real nodes on loopback: the settings and saving them, the pins (one set: `Settings.Peers` is written
  from the node's `Pins`), pairing, removing a peer, one link handler for the Seamdesk node and the MWB node (link up
  and down for `Cursor`, each link's frames, the greeting of monitors and layout), the layout revision, copy and paste,
  the mapping from MWB peer settings to sessions, and restarting the MWB node. The roster owns the `Cursor` and keeps
  its desk; the tray, the settings window and `App` all go through it (the window and tray via `ISeamdesk`).
- **Input thread.** One dedicated thread owns the low-level mouse and keyboard hooks, the blank-cursor window and the
  desktop-switch watcher, on one message loop. Callbacks translate the event, ask `Cursor` (a lock and a few compares)
  and return 1 to swallow it: a nonzero return keeps the event from the rest of the chain and the target window, and
  a hook that times out "is silently removed" on Windows 7 and later (LowLevelMouseProc docs). Nothing slow (network,
  disk, SendInput) runs in a callback. Hotkey actions are posted out of the hook.
- **States** (`Seamdesk.Host.Cursor`): *Local* (input passes through), *Driving* (this machine's input is swallowed and
  forwarded to the holder), *Held* (a controller drives this machine; its own input still passes through).
- **Own injections.** Everything Seamdesk injects carries `dwExtraInfo = "GLID"`; both hooks pass such events through
  untouched (`MSLLHOOKSTRUCT.dwExtraInfo`, `KBDLLHOOKSTRUCT.dwExtraInfo`). Other injected input counts as physical.
- **Crossing from Local.** The hook's point is tried with `Desk.Move` from the last position. A point on an
  outer-edge pixel that moved towards that edge counts as one pixel past it, so a crossing is seen whether or not
  Windows clips the hook's point to the desktop (`tools/smoke.ps1` step 1 reports
  which). No crossing while a local button is held, while "stay" is on, while auto-pause applies, or into a peer
  that is down or on its secure desktop.
- **Park point and hidden cursor.** On crossing, the cursor is parked at the centre of the primary monitor, so one
  event would have to move half a monitor before any clipping could cut its delta; MWB parks at y = 4
  (`Core/Common.cs:1485-1489`).
- **The real cursor moves only inside a hook callback.** Windows computes a mouse event's point before it calls the
  low-level hooks and applies it after they return, unless one swallows it. A `SetCursorPos` made anywhere else (Seamdesk
  used to post it to the input thread) can land while the next event is already computed from the old position: after
  a park, that event read as a jump of (exit - park) on the peer; after a return, it put the cursor back by the park
  point, and moves between the return and the posted call passed there too. So `Cursor` asks for a `Place` and the hook
  does it in the callback that took the event, then swallows a move: crossing parks it in the crossing's own callback;
  a return shows it at the return point in the next mouse event's callback, swallowing everything that would move it
  until then. MWB moves its cursor inside its hook too, swallowing the event (`Class/InputHook.cs:268-283`), and comes
  back with an injected move, which Windows queues behind any event in flight (`Core/Helper.cs:105-113`). Only a
  return across the shared edge comes with the hand moving. Every other one (the snap-back and stay hotkeys, the
  holder's own snap-back, a dropped link, a lock, quitting) also shows the cursor at once (`Show`, outside the hook):
  with the hand still, no event is in flight to race, and none would come to place it, so the cursor would stay hidden
  at the park point and a click would land on the blank window. Quitting shows it before Seamdesk ends. Each
  placement logs where the next hook point was ("the first hook point after it parked/came back is (x, y)"): the park
  point plus a few pixels, or the return point plus a few, unless the cursor is somewhere else.
- **Hidden cursor.** The cursor is hidden by a 32x32 topmost, never-activating popup under the park point, layered at alpha 1 (alpha 0
  would let mouse messages through: "Layered Windows" in the Window Features docs). Its `WM_SETCURSOR` calls
  `SetCursor(NULL)`, which "removes the cursor from the screen". Swallowed moves never move the cursor, so it stays
  over the window. MWB does the same with a 1-3 px form showing a dot cursor (`Form/frmScreen.cs:254-255`,
  `Core/Helper.cs:195-225`). Rejected: `SetSystemCursor`, which replaces the system cursors for every app until
  restored, so a crash would leave the user without a cursor. `tools/smoke.ps1` step 1 checks the hidden state with
  `GetCursorInfo`.
- **Injection.** `SendInput` with `MOUSEEVENTF_MOVE | ABSOLUTE | VIRTUALDESK`: normalised 0..65535 over the whole
  virtual desktop (MOUSEINPUT docs). Only relative motion "is subject to the effects of the mouse speed and the
  two-mouse threshold values" (same docs), so absolute moves get no second acceleration (smoke
  step 5 measures a relative move for the record). The exact normalised-to-pixel rounding is not documented, so
  Seamdesk aims at the pixel's centre, which lands on that pixel whether Windows floors `n * width / 65536` or `/ 65535`
  (tested for every pixel; smoke step 4 checks real pixels, question 2). Buttons, wheel and keys use the same call.
- **Monitors.** `EnumDisplayMonitors` + `GetMonitorInfo`, Per-Monitor-V2 aware through the manifest, so rectangles
  are physical pixels ("Mixed-Mode DPI Scaling and DPI-aware APIs"). Re-read on `SystemEvents.DisplaySettingsChanged`
  (raised on `WM_DISPLAYCHANGE`), then sent to every peer.
- **Events → `Kick`.** Resume (`PowerModeChanged`), unlock and console/remote connect (`SessionSwitch`), any address
  change (`NetworkAddressChanged`) and the tray's "Reconnect now" retry every peer at once.
- **Secure desktop.** `EVENT_SYSTEM_DESKTOPSWITCH` ("the active desktop has been switched", Event Constants docs), then
  `OpenInputDesktop`: anything but "Default" (lock, UAC, Ctrl+Alt+Del) is secure. The machine then sends `Locked(true)`
  to every peer and gives up the cursor: a holder goes back to Local (its controller releases on `Locked`); a controller
  hands back with `Handoff`. While locked it refuses `Handoff` with `Locked(true)`.
- **Identity store.** `DpapiIdentityStore`: the PKCS#12 bytes encrypted with `ProtectedData`, CurrentUser scope, in
  `%LOCALAPPDATA%\Seamdesk\identity.dpapi`. `ProtectedData` already passes `CRYPTPROTECT_UI_FORBIDDEN` (dotnet/runtime
  `ProtectedData.cs`), so DPAPI never prompts; `CryptUnprotectData` has no timeout, so `Identity.LoadOrCreateAsync` reads
  the store off the UI thread and gives up after 10 s. A file DPAPI refuses or doesn't hand back is never replaced
  silently: Seamdesk asks, and on yes renames it `identity.dpapi.unreadable-<time>`, keeps it and makes a new identity
  (peers pair again).
- **Startup.** Each step goes to `seamdesk.log` (`start: <step>`). The log keeps to a size (`Seamdesk.Host.LogFile`): a line
  that finds it at 4 MB first moves it to `seamdesk.old.log`, replacing the one before, so at most 8 MB stay on disk
  (about four hours of driving an MWB peer, whose timing lines add 0.9 MB an hour). A watchdog thread (`Seamdesk.Host.Startup`) gives the
  tray 20 s, not counting dialogs that wait for the user; past that it logs the step, says so with the log's path and
  exits, so a stuck instance doesn't hold the single-instance mutex. Settings and pins: `%LOCALAPPDATA%\Seamdesk\settings.json` (pins are hashes).
- **Hotkeys**, both rebindable in settings: **"snap back to this machine"** (default Ctrl+Alt+Home) and the toggle
  **"stay on this machine"** (default Ctrl+Alt+Insert; turning it on snaps back first). Each also has a fixed second
  chord for keyboards without Home or Insert, **Ctrl+Alt+H** and **Ctrl+Alt+P** (`Hotkey.AlsoSnapBack`, `AlsoStay`),
  shown beside the rebindable one on the Shortcuts page and in the tray; neither can be chosen for the other action.
  They are not Windows' own and none of MWB's defaults (Ctrl+Alt+F1-F4 to switch; Win+Ctrl+Alt+E, L, R:
  `settings-ui/Settings.UI.Library/MouseWithoutBordersProperties.cs:33-39`, `:188`). Detected in the low-level
  keyboard hook with exact modifiers (left and right count alike); the hotkey's own key, down, repeat and up, is
  swallowed, modifiers are not. MWB matches hotkeys in its hook too (`Class/InputHook.cs:516-581`); `RegisterHotKey`
  would never fire while Driving, because the hook swallows the keys first. A hotkey needs a modifier.
- **Crossings and returns** go to `seamdesk.log`, one line each (`Cursor.TakeNotes`, written by the effects loop, never
  from the hook): `cursor: crossed to laptop through this machine's bottom edge at (2000, 1079), entering at (560, 0)`,
  and `cursor: back from laptop at (2000, 1079): <why>`, where why is "it crossed back at the edge", "laptop sent it
  back where it left" (its own snap-back, or a point off this machine), "the snap-back hotkey", "the stay hotkey",
  "laptop locked or stopped reading" (a Seamdesk peer's lock screen, or an MWB peer's stall and its 5 s backstop, whose
  own lines say which), "this machine went to its secure desktop (lock, UAC or Ctrl+Alt+Del)", "the link to laptop
  dropped", "it left there for X, which is down or locked", or "Seamdesk quit". A holder passing the cursor on logs
  "laptop passed it to X".
- **Auto-pause** (setting, default off): no crossing while `SHQueryUserNotificationState` reports a fullscreen
  Direct3D app, a busy fullscreen app or presentation mode. Checked only when a crossing is about to happen.
- **MWB detected** (a `MouseWithoutBorders`, `...Helper` or `PowerToys.MouseWithoutBorders*` process in this
  session): Seamdesk doesn't start and a dialog says why, offering "Quit Mouse Without Borders and start Seamdesk" or Cancel,
  which keeps MWB. MWB's processes in this session are ended only after that click; if one won't end (say it runs
  elevated), the dialog says to quit it from its tray icon.
- **Tray** (WPF's menu on WinForms' `NotifyIcon`, so it wears the Fluent theme): one status line per peer, "Stay on this
  machine" (with its shortcut), "Reconnect now", "Settings" (also a left click), "Go back to Mouse Without Borders" and
  "Quit Seamdesk". The MWB item shows only if MWB is installed: standalone Garage under Program Files (either), else
  PowerToys per-user, else per-machine; it quits Seamdesk and starts that exe. One instance per session (`Local\Seamdesk`
  mutex).

## Settings window and layout editor

The look, type, spacing and colours are in `docs/ui.md`; screenshots of every state are in `docs/screenshots/`. WPF's
Fluent theme (`ThemeMode = System`) follows the system light or dark setting and accent; .NET 10 still marks
`ThemeMode` experimental (WPF0001, suppressed in the two projects that set it). The window is built in C# from small
factories (`Ui.cs`) over one resource dictionary (`Theme.xaml`), so `tools/Shots` can drive each page into each state.
`ISeamdesk` is all the window and tray see of the app; `App` implements it and `tools/Shots` fakes it.

- **Pages.** Machines, Layout, Shortcuts, Behaviour, About. Everything but the layout applies at once, as Windows
  Settings does. `Apply` (`Roster.Apply`) saves `settings.json` and puts the change into effect without a restart: a new
  `HotkeyMatcher` for the input thread, a new desk for `Cursor`, a moved layout bumps `LayoutRevision` and is pushed to
  every Seamdesk peer (`Layout`), and a changed MWB peer or key restarts the MWB node from settings.
- **Pairing.** "Show a code" calls `Node.ShowPairingCode` and shows the 8 digits with a countdown until a new pin
  arrives or the 2 minutes run out; "Enter a code" calls `Node.PairAsync` and turns `RefusedException` and socket
  errors into plain sentences. "Unpair" and an MWB peer's "Remove" are one path, `Roster.Remove`: it forgets the
  peer's pin (`Node.Unpair` stops that supervisor and closes its connection, so the peer's next dial is refused as
  unpaired), offset, reported monitors, copy and paste, MWB settings and key. An MWB peer is on the other Seamdesk peers'
  desks only through this machine's layout, so removing it bumps `LayoutRevision` and pushes the layout; a Seamdesk
  peer's offset stays with the machines still paired with it.
- **MWB peers.** Name, address, the legacy security switch and its one-line explanation, the MWB version (Automatic,
  "Before 0.101", "0.101 or later"; with Automatic, what it found), "Let <peer> control this PC" (off by default), copy
  and paste (off, this PC to the peer, or both ways), and the key: typed in a password box, checked by `MwbKey`, kept with DPAPI and never shown again
  ("Change key"). A peer without a key is skipped and shows "Needs its security key"; there is no dialog at start.
- **Layout editor** (`Seamdesk.Host.LayoutEditor`, pure, tested on Linux). Machines move whole; their monitors keep
  Windows' arrangement. Dragging snaps on each axis to the nearest edge of another machine's monitor within 10 screen
  pixels (side by side or aligned), counting only monitors level with each other on the other axis; arrow keys move
  10 px (Shift: 1). **Where the cursor crosses** is computed by walking every outer edge pixel through `Desk.Cross`, so
  the preview is exactly what the cursor will do; runs of pixels landing on the same monitor become one numbered
  touch, drawn on the desk and listed in words. A machine that touches nothing gets a warning. Overlap disables Save.
  The editor's desk and the cursor's come from one module, `Seamdesk.Host.ConfiguredDesk` (`Saved` and `Live`): this
  machine at its offset, then each peer, Seamdesk then MWB, with monitors and an offset; the cursor takes each peer's
  monitors from its link, the editor the saved ones. In the editor, a peer not yet placed starts 200 px to the right
  of the desk, top aligned; a Seamdesk peer whose monitors are unknown (never connected) waits, and Seamdesk keeps the last
  monitors each peer reported in `settings.json` (`Monitors`) for this. An MWB peer without a saved layout that has
  a monitor (a hand-edited file) is on neither desk: Seamdesk doesn't link to it, and the editor says so.
- **MWB peers' screens** are entered per saved layout (size and position as the peer's display settings show them).
  The editor shows the layout in use ("in use now", from the last `Monitors` the link reported) and refuses two
  layouts with the same outer size, which no hand-back could tell apart, and overlapping screens.
- **Shortcuts.** A capture field listens with Seamdesk's own hotkeys paused, takes the first key pressed with at least one
  modifier, and checks it (`Hotkey.Problem`): the other Seamdesk shortcut, or a combination Windows keeps (Win with a
  letter, digit, arrow, Tab, Space, Home or Esc; Alt+Tab, Alt+Esc, Alt+Space, Alt+F4; Ctrl+Esc, Ctrl+Shift+Esc;
  Ctrl+Alt+Del), is refused with the reason; a shortcut apps commonly use (Ctrl, Ctrl+Shift or Alt with a letter,
  digit or F key; editing and navigation keys) is accepted with a warning, since the hook takes it everywhere.

## MWB compatibility mode (`Seamdesk.Mwb`)

A per-peer option labelled **"legacy security"**, off by default. It lets Seamdesk talk MWB's own protocol to a machine
that runs MWB, for example a managed laptop that may not run Seamdesk yet. It uses MWB's shared key (typed into Seamdesk and
kept with DPAPI): no weaker than MWB itself, so the label says so. All three stages of Q7 are built: Seamdesk drives old
MWB (standalone "Garage" or PowerToys up to 0.100), Seamdesk drives current PowerToys MWB (0.101 and later), and either
version may drive Seamdesk.

Citations in this section are to two PowerToys commits, paths under `src/modules/MouseWithoutBorders/App/`.
**`a9e1f0f20^`** (the parent of "Use per-connection random salt and IV", #48742) for the old format: its encryption,
package layout, handshake and clipboard code match the 2023 import (`29eebe16a`) apart from API renames
(`git log 29eebe16a~1..a9e1f0f20^`); horizontal wheel injection was added later (`dd420509a`), so an older peer ignores
it. Standalone MWB ("Garage", 2.2.1.0327, the build Seamdesk is tested against) is closed source; what is said here about
it was established by testing against the installed program. It speaks the old format with the same packages,
handshake, cipher and IV, but derives the key differently (**Standalone key**, below), so a key typed into both doesn't
make it pair with PowerToys. Seamdesk 0.7.0 assumed it matched PowerToys, and refused every Garage peer as "the
security key doesn't match".
**`48c4bad07`** (October 2026) for the current format. From `a9e1f0f20` to there the only wire change is in
`Core/Encryption.cs`: `a9e1f0f20` (a salt and IV per stream) and `a64c400b7` (100000 PBKDF2 iterations), both first
released in `v0.101.2211.0`; `Class/SocketStuff.cs`, `Core/Receiver.cs`, `DATA.cs`, `Package.cs` and `PackageType.cs`
are unchanged, and `Core/Clipboard.cs` changed only on the clipboard socket (port 15100), which Seamdesk never uses.
`5c9fddcdd` stores the key with DPAPI but still runs on the plain key (`Class/Setting.cs:612-626`). Unmarked line numbers
below hold at both commits.

**Wire format** (`MwbWire.cs`, ported with Microsoft's MIT notice):
- **Old key.** AES-256-CBC, zero padding, one cipher stream per direction for the life of the connection. Key =
  PBKDF2-SHA512(key, UTF-16 of "18446744073709551615", 50000 iterations, 32 bytes); IV = the first 16 ASCII characters
  of that string (`Core/Encryption.cs:93-154` at `a9e1f0f20^`, `Core/Common.cs:397-410`).
- **Standalone key.** Garage 2.2.1.0327 derives the old format's key with PBKDF2-**SHA1** (same salt, 50000 iterations
  and length) and the magic number with **SHA-256** (same 50000 rehashes and byte picks). PowerToys uses SHA-512 for
  both. Cipher, padding and IV are the same. Seamdesk derives it lazily (`MwbKey.Standalone`) and only tries it after the PowerToys key fails.
- **Current key.** Each stream starts with a random 16-byte salt and 16-byte IV in the clear; key =
  PBKDF2-SHA512(key, that salt, 100000 iterations, 32 bytes), same cipher and padding (`Encryption.cs:38`, `:42`,
  `:115-183` at `48c4bad07`). So the two directions of one connection have different keys.
- Both: each stream then starts with one random encrypted block (`Class/SocketStuff.cs:98-122`,
  `Core/Common.cs:1528-1560`; Garage does the same). Whitespace in the typed key is dropped as MWB's setup page does
  (`Form/Settings/SetupPage2a.cs:92`); fewer than 16 characters is refused (`Encryption.cs:232-239`). Platform `Aes` and
  `Rfc2898DeriveBytes.Pbkdf2` only.
- **Packages** of 32 bytes, 64 for "big" types (`Core/DATA.cs:24-133`): type, checksum, two bytes of the key's magic
  number (`Encryption.cs:170-197` at `a9e1f0f20^`, `:185-211` at `48c4bad07`, the same code), id, source, destination,
  then mouse data, keyboard data or handshake words; big ones carry the sender's name. The checksum covers bytes 2-31
  (`SocketStuff.cs:497-556`). MWB compares the magic as int against uint, so a key whose magic has the top bit set never
  works in MWB; Seamdesk keeps that.
- **Handshake** (`SocketStuff.cs:1261-1452`; Garage takes the same steps): each side writes its
  stream start and ten Handshakes of random words before it reads (`:1261-1295`), so nobody waits for the other to speak
  first; MWB sets a 500 ms send timeout and no read timeout, Seamdesk gives the whole handshake 10 s; answers each Handshake with the words inverted plus its id and name, and trusts the
  other on an answer that inverts its own words. Ten other packages first, or a wrong answer, close it. Ids: MWB's is
  random per install (`Class/Setting.cs:1048-1068` at `a9e1f0f20^`); Seamdesk makes one per peer and keeps it in settings.
  Name: the host name cut to 32 characters, as MWB (`Common.cs:1361-1385`), so the peer's MWB sees the PC it already
  knows. Port 15101, MWB's message port (`SocketStuff.cs:479`, `:1169`).
- **Version.** Per peer: **Auto** (default), **Old** (standalone MWB, or PowerToys up to 0.100) or **Current**. Since
  MWB always writes first, Seamdesk writes nothing until it has read the peer's first 80 bytes, and tries them as an old
  stream with the PowerToys key, then with the standalone key (block and first package), then as a current one (header,
  block and first package): the one whose package carries that key's magic and checksum is the peer's format and key,
  and Seamdesk answers in it. Neither means the key differs. A fixed choice refuses the other format with
  the reason ("LAPTOP runs PowerToys 0.101 or later: choose Current or Auto for it in Seamdesk's settings"). **Auto never
  falls back on its own:** once a peer has spoken the current format (kept in settings as `Detected`, and on the session
  for the rest of the run), Auto refuses the old one with "LAPTOP now offers the old Mouse Without Borders format after
  using 0.101 or later; Seamdesk won't fall back on its own: choose Old for it in Seamdesk's settings". The settings window
  shows what Auto found.

**Seamdesk drives the peer** (`MwbLink.SendAsync`) as MWB's controller does in its default absolute mode ("move mouse
relatively" off, `Core/Event.cs:104-114`): the arrival and **every move as an absolute position**, 0..65535 over the
peer's outer box, aimed at the pixel's centre (the peer maps it back over its box, `Class/InputSimulation.cs:160-233`).
The link keeps the peer's cursor on Seamdesk's desk: each `PointerMove` goes through `Desk.Move` from where it is, as a
Seamdesk peer's `Cursor` does when Held, and the pace sends the position (one package a millisecond, `MovePace`). Buttons
and wheel go as MWB's window messages with "relative zero" coordinates; keys as virtual key plus
`LLKHF_UP`/`LLKHF_EXTENDED` (`InputSimulation.cs:108-158`). Leaving the peer (across an edge, snap-back, lock, stall)
sends `HideMouse`, as MWB's controller does (`Event.cs:214`); moves that arrive after it are dropped.

Up to 0.7.7 Seamdesk sent **relative** moves (±100000, `Event.cs:32`), and that made crossings jumpy:
- The peer's MWB runs its own edge test after every relative move (`Receiver.cs:147-170`, `Core/MachineStuff.cs:239-257`):
  within one pixel of its desktop's edge (`SKIP_PIXELS`, `MachineStuff.cs:33`), with a neighbour there in its matrix,
  it sends `NextMachine`. Seamdesk's arrival is the first pixel past the shared edge, inside that band, so any move that
  didn't leave the band at once (sliding along the edge, a shallow approach) sent the cursor straight back, and the
  hand, still pushing, crossed again. Real MWB's controller enters two pixels in (`JUMP_PIXELS`, `:392-402`) and
  decides the edge itself. Measured on Linux (the real `Cursor` and `MwbLink`, a fake peer that runs MWB's receiver
  rules, an 8 kHz hand for 0.5 s): approaching at 1 in 16, 237 crossings and 236 returns and the cursor never got onto
  the peer; at 1 in 4, one bounce; with absolute moves, one crossing and no bounce at either. A test log shows the
  same on 0.7.7: three arrivals and three `HideMouse` in one second.
- A relative move is injected as relative input (`InputSimulation.cs:283-301`), so the peer applied its own pointer
  speed and acceleration on top of this PC's, and unevenly: Windows' curve depends on the size of each input, and the
  pace sums 1 to 9 hook events into one package (a test log, "at most 9 in one"). An absolute position is placed
  as it is (`Receiver.cs:180-184`): the peer's cursor is exactly where Seamdesk's desk says, however the moves were summed.

**Hand-back from the peer.** Seamdesk's desk decides when the cursor leaves the peer, and where it lands, in both
directions: the arrival is the desk pixel past the edge, and a move past one of the peer's outer edges onto another
machine hands the cursor there (`Handoff` to `Cursor`, then `HideMouse`). An absolute move never triggers the peer's
edge test. The peer's MWB still sends `NextMachine` when its own input takes the cursor off its edge (its touchpad, say):
with where the cursor would land on the next machine, 0..65535 over its own box, two pixels inside the opposite edge
(`MachineStuff.cs:34`, `:181-190`, Move* `:392-669`, `Common.cs:516-543`). Seamdesk inverts the exit point along the edge
and asks `Desk.Cross` for the pixel past it on this machine; if Seamdesk's desk has nothing of this machine there, the
cursor comes back where it left. A `NextMachine` while Seamdesk does not hold the peer (say, after a snap-back) is ignored;
one naming another machine is ignored with a line (`its Mouse Without Borders handed the cursor to machine id N, not this
one`), as MWB's controller cancels a switch to a machine it has no link to (`Event.cs:171-177`).

The peer's MWB may also switch by itself on Seamdesk's moves. Its hook tells injected moves from the user's by one flag set
before each injection, not by the injected bit (`InputHook.cs:205-227`), so of two packages injected before its hook
runs, the second passes for the user's, and its own edge test runs on it (`Event.cs:73-94`). On its edge pixel, with a
neighbour there in its matrix, it switches to that machine and parks its cursor (`Event.cs:207-231`); standalone MWB,
finding no link to it later, puts its cursor back where that test fired. Seamdesk's arrival and a push along the shared edge sit on that pixel, where MWB's controller never
puts the cursor (it switches first). The switch sends `MachineSwitched` to the new machine; Seamdesk logs one that reaches
it while it drives the peer (`its Mouse Without Borders switched to this machine by itself ...`). None so far: the line
is there so a report can tell.

**Whether the peer shows the cursor.** MWB's receiver answers nothing to a `Mouse` package (`Receiver.cs:118-193`); no
package says the peer injected it or that its screen is unlocked. So Seamdesk cannot tell from the protocol that the peer
took the cursor; it can tell only that the peer stopped reading (below). It does not guess beyond that. A peer whose
user-session MWB keeps reading while its input desktop is the lock screen injects into nothing (`Core/WinAPI.cs:273-345`
keeps the Default-desktop instance running), and only someone at that machine sees that.

**The peer drives Seamdesk** (only with "Let LAPTOP control this PC", off by default). MWB's controller sends `Mouse` and
`Keyboard` packages addressed to Seamdesk's id (`Event.cs:97-116`, `:263-268`). Seamdesk reads them as MWB's receiver does
(`Receiver.cs:118-193`): both coordinates at least 100000 from zero is a relative move, or a click where the cursor is
(`:147`); otherwise the point is absolute, 0..65535 over this machine's outer box (`InputSimulation.cs:160-171`). The
first package after a switch is the arrival as an absolute move (`InputHook.cs:268-281`). The link turns them into the
frames a Seamdesk controller sends: `Handoff` to this machine at the arrival, `PointerMove` (a relative delta, or the step
from the last absolute point), buttons, wheel and keys (the scan code is mapped from the virtual key on injection, as
MWB's `SendKey` does). So `Cursor`'s Held path applies them 1:1 and runs the edge test on Seamdesk's own desk, as MWB's
receiver runs it on its box in relative mode (`Receiver.cs:155-170`). When the desk moves the cursor onto the MWB peer,
Seamdesk sends `NextMachine` naming the peer, at that pixel as 0..65535 over the peer's saved box; the peer's MWB takes
the cursor there (`Receiver.cs:195-203`, `Event.cs:157-236`, `Core/Helper.cs:99-113`) and answers with `HideMouse`,
which Seamdesk then ignores. A snap-back, or a crossing onto a machine MWB can't reach, hands the cursor to the centre of
the peer's box. A `HideMouse` while driven means MWB took the cursor elsewhere: Seamdesk leaves Held and releases every
key and button, as MWB's receiver does (`Receiver.cs:383-388`). In MWB's default absolute mode the controller runs the
edge test on its own screen (`Event.cs:73-95`), so it decides when the cursor leaves Seamdesk.

**The peers' screens.** No package carries screen geometry (`Core/PackageType.cs`), so MWB cannot report
its outer box. The user saves the peer's layouts in the layout editor. Each `NextMachine` reveals one side of the box
exactly (the "two pixels inside" coordinate is `floor(2 * 65535 / height)` or its mirror), so Seamdesk checks the layout in
use against it and switches to the first saved layout that fits. If none fits, the tray says "<peer>'s screens changed:
check layout" and the cursor comes back where it left. Two layouts with the same size across that edge can't be told
apart; neither can be told from the peer's box until its first hand-back. Since Seamdesk sends absolute moves, a return it
drives sends no `NextMachine`, so only a hand-back by the peer's own input checks the layout. With a stale layout the
peer still maps 0..65535 onto its real box, edge to edge, so crossings and returns still meet at the edges; positions in
between scale, as MWB's own controller scales its primary screen onto the peer (`Event.cs:111-113`).

**Both ways** (PowerToys' `SocketStuff.cs`; Garage behaves the same): each MWB dials every machine in its matrix on port
15101 and accepts any connection; every link runs the same handshake. It sends each package on every trusted link to that
machine's id and acts on packages from any trusted link, so one trusted
link, in either direction, is enough to drive it and to take its hand-back. When Seamdesk closes the peer's own dial-in
(control off), that MWB logs the socket error ("Unable to read data from the transport connection") and dials again
(`frmScreen` timer, `REOPEN_WHEN_WSAECONNRESET` → `Common.UpdateClientSockets`, client links only); it closes no other
link, and with Seamdesk's link up it never reopens all sockets (`Common.AtLeastOneSocketEstablished`).

**Log.** Every step of every MWB link goes to `seamdesk.log`, one line each starting `mwb <peer>:` (`MwbNode.Log`, written
by the app): each dial (address, port, connected or the socket error), each connection to Seamdesk's listener (accepted,
or refused and why), the handshake (the 80-byte stream start read, or how many bytes came before the peer closed or the
10 s ran out; which format and key it opens as; trust, with the peer's claimed name and id), every refusal with the
same reason the card shows, and each close with its reason. Never key material, decrypted packages, input or clipboard
text. A line repeated before any link comes up is written the 1st, 10th, 100th and 1000th time, then every 10000th,
with the count (a peer's MWB redials about every second). `tools/mwb-check.md` explains how to read it.

**Timing lines** (`SendTiming`), for "the cursor lags on the MWB laptop": while Seamdesk drives an MWB peer, one line a
second, written in full (never folded like the lines above); a Mouse package opens the second, so an idle link (its
heartbeats, handshake answers) writes none. For example `mwb laptop: driving, last second: 1000 packages (1000 mouse, 0
waited for another write), 1000 moves from 1002 hook events (at most 3 in one), hook to written ms min 0.01 med 0.06 p95
0.08 max 3.02, write ms min 0.01 med 0.01 p95 0.02 max 0.06, tcp rtt 16.0 ms, peer window 248 KiB`. Packages: every
package written that second; "waited" counts writes that found another in progress (the link's only queue). Moves: the
`PointerMove`s sent, and the hook events they sum (`Cursor` stamps each move with its first hook event and their
count, `Send.Since` and `Send.Events`, which `Roster` hands to `MwbLink.Moving` before the send: the link may hold the
move back and sum it with the next, below). Hook to written: from the
first hook event of a move until its package is written to the socket. Write: encrypting and writing one package. Tcp:
the round trip and the window the peer's TCP last advertised, from the OS (`SIO_TCP_INFO` on Windows, `TCP_INFO` on
Linux; left out where neither answers). They are hints, not a meter: MWB asks for a 320000-byte receive buffer
(`Class/SocketStuff.cs:1284`), and a window that falls is data its MWB has received and not read, but on Linux a
backlog of 1500 packages did not move it (3000 did), and a reader that falls behind raised the round
trip from 1 to 16 ms with no network between. The decisive reading is Seamdesk's own: hook to written well under a
millisecond and packages a second near the mouse's report rate, while the cursor on the peer visibly trails, puts the
queue after the socket. Counts and times only: never key material, keys, coordinates or clipboard text.

**Pace** (0.7.6 on a tested PC: "the cursor lags", then "I keep losing the mouse"). Their mouse made 3,000 to 5,600
hook events a second, and Seamdesk sent a `Mouse` package for each. The peer's MWB reads and injects packages one at a
time, in order (`SocketStuff.cs:1329-1546`, `Receiver.cs:147-170`): faster than it injects, they queue in its receive
buffer and the cursor trails the hand by as much (on Linux, a peer taking 300 µs a package: an 8 kHz mouse trailed by
0.7, 2.1 and 3.5 s after one, two and three seconds of moving, and caught up 4.2 s after the hand stopped). So
`MwbLink` sends at most one move package a millisecond on average (`MovePace`), each summing the moves since the last
(the generic cell rate algorithm, one package of tolerance, so a 1 kHz mouse's jitter holds nothing back). A move that
must wait goes out as soon as the pace allows: the app waits with a high-resolution waitable timer
(`CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`, `MwbSession.Delay` from `Roster.MwbDelay`), because `Task.Delay` waits for
Windows' 15.6 ms tick; elsewhere `Task.Delay` rounded up to a whole millisecond. Every other package (a click, the
wheel, a key, a hand-back) first writes the held moves, under the same write lock, so it is never delayed and never
lands before them. MWB's own controller sends a package per hook event too (`Event.cs` `MouseEvent`), absolute by
default. The same Linux peer now: 1 kHz 0.4 ms (as before), 4 and 8 kHz a median of 1 ms, every pixel delivered.

**Stalled peer.** In one run a tested laptop stopped reading: its TCP stayed up (round trip 2 ms), its window fell
from 2048 to 609 KiB by what Seamdesk sent, and the cursor stayed there 14 minutes until the laptop reset the link.
Every 100 ms while Seamdesk drives the peer, the link reads the peer's window from the OS (as for the timing lines) and
counts what it hasn't read as the widest window it has advertised on this link minus the window now. At 64 KiB
(`StalledBytes`: 2048 packages, two seconds of moves at the pace; a peer that keeps up stayed within 1 KiB at 3,300
packages a second) it logs a line (`mwb laptop: stopped reading: 64 KiB sent to it are unread (its TCP window fell
from 2048 to 1984 KiB); the cursor comes back here and won't cross to it until it reads them, or for 5 s`), sends
`HideMouse` (queued behind the rest, so the peer releases its keys once it reads) and surfaces `Locked(true)`: the
cursor comes home and won't cross there, as for a Seamdesk peer on its lock screen. Back under 4 KiB unread it logs
"reading again" and surfaces `Locked(false)`; a link that drops ends it too. A Windows receiver reopens its window as
its app reads (the tested laptop's stayed at 2047-2048 KiB while the laptop read 3,300 packages a second), but a Linux receiver
flooded with 32-byte segments can keep it shut after reading, so the lock also ends after 5 s (`StallHold`) whatever
the window says, with a line (`mwb laptop: 64 KiB still unread after 5 s: the cursor may cross to it again, and comes
back if it still doesn't read`). Crossing to a peer still stalled brings the cursor back within 100 ms, with another
line. These lines are written in full, as the timing lines (a stall repeated on one link would otherwise be folded).
"Reading again" is not tested on Linux and needs a Windows PC; a Handshake probe (answered in order,
`SocketStuff.cs:1370-1383`) would prove it exactly, but is new traffic and was not chosen.

**Heartbeat** on trust and every minute (`SocketStuff.cs:1451`, `Form/frmScreen.cs:567`); a `Hello` is answered with
one (`Receiver.cs:251`). MWB sends nothing else while idle, so Seamdesk turns on TCP keepalive (5 s, then 3 probes 1 s
apart) to notice a peer that vanished. A `ByeBye` closes the link (`Receiver.cs:268`). **Silence**: MWB sends a
heartbeat or an awake beat at least every 5 minutes (its 100 ms timer, `Form/frmScreen.cs:286`, `:567-571`;
`Common.cs:545-560`), so a link that hears nothing for 11 minutes (two missed and a minute) is half-open, its end gone
or its MWB hung, as at 10:17:58 (trusted, then silence): Seamdesk closes it, saying so, and dials again.

**Hardening.** Off unless `LegacySecurity` is on for that peer, and off again once a Seamdesk pin has the peer's name. Seamdesk
dials only the configured host. Its listener closes, before a byte, any connection that is not from an address the
host resolves to (and from a private source, as Seamdesk's own listener), or whose peer may not control this machine:
only that switch lets the peer's MWB dial in. Refusing it doesn't stop Seamdesk driving the peer (Both ways, above). Only a link whose handshake succeeded exists as an object, so no input is
sent or taken before; packages that arrive before trust are counted and dropped. A stream start that opens with neither
format means the keys differ: the link drops and the tray says "the security key doesn't match <peer>'s Mouse Without
Borders key" (retried on the normal backoff). Nothing from the peer is acted on except `NextMachine`, `Hello`,
`ByeBye`, handshakes, and, only if allowed, its input and `HideMouse` (control) and its clipboard text (copy and paste
both ways); drag and drop, images, its matrix and clipboard requests are dropped, and Seamdesk never opens MWB's clipboard
port (15100).

**Copy and paste** per MWB peer, as for Seamdesk peers: off, this PC to the peer, or both ways. To the peer: when the
cursor crosses into the peer, the PC's clipboard text goes first, as MWB's own: "TXT" + text + MWB's separator, UTF-16,
deflated, in 48-byte slices of `ClipboardText` packages and a `ClipboardDataEnd` (`Core/Clipboard.cs:43-47`, `:253-306`
at `a9e1f0f20^`; `Helper/FormHelper.cs:187`). The same text is not sent twice on one link. From the peer, only with
"both ways": MWB sends its clipboard to every machine when it changes (`Clipboard.cs:229-240`, `:253-306` at `48c4bad07`); Seamdesk
collects the slices up to `ClipboardDataEnd`, inflates them and takes the "TXT" section only (`Clipboard.cs:1101-1180`),
ignoring RTF and HTML. The link surfaces the text only if the session takes it, and the app sets it only if the setting
says both ways. Over 1 MB compressed (MWB then uses its clipboard socket) is not sent or taken, nor is more than 16 MB
inflated (a known limit).

**Settings.** `settings.json` `MwbPeers`: `Name` (on the desk), `Host`, `LegacySecurity`, `Version` (`Auto`, `Old`,
`Current`), `Detected`, `AllowControl`, `Clipboard` (`Off`, `ToPeer`, `BothWays`), `MachineId`, `Layouts`; the offset is
in `Offsets` like any peer. A 0.4-0.6 file's `"SendClipboard": true` still loads as `ToPeer`. All of it but `Detected`
and `MachineId` is edited in the settings window (Machines and Layout). The key is typed there and kept in
`%LOCALAPPDATA%\Seamdesk\mwb-<name>.key` with DPAPI (CurrentUser); removing the peer deletes it.

**Proof without the peer** (`tools/mwb-vectors`, README there): MWB's own encryption, package, send/receive and
clipboard code is extracted from `a9e1f0f20^` and from `48c4bad07` at build time and run against `MwbLink` over
loopback, both ways. `tests/Seamdesk.Mwb.Tests/vectors.json` (old) and `vectors-current.json` (current) hold what it
captured. For the PowerToys formats the vectors prove: Seamdesk on Auto finds the format from MWB's real stream start; the handshake
both ways; Seamdesk decrypts both captured streams as MWB does and re-encrypts them byte for byte, with MWB's magic and
checksum on every package; every frame Seamdesk sends while driving becomes the package MWB's decoder reads; MWB's own
heartbeat, `Hello`, `ClipboardAsk`, `NextMachine` and `ByeBye` are handled as above; MWB's own clipboard sender's text is
taken (with RTF beside it and the buffer's spare zeros); 18 controller packages become the frames written in the
harness; and Seamdesk's hand-back `NextMachine` is what MWB's decoder reads. **Known gaps**, not proven by the vectors:
- MWB's handshake loop (`MainTCPRoutine`) is ported into the harness, not compiled: it is tangled with MWB's UI and
  socket list. Its sends and receives are MWB's own. The file is the same at both commits.
- MWB's hooks are not run, so the controller's `Mouse`, `Keyboard` and `HideMouse` packages are built in the harness
  field by field as `Event.cs:97-116`, `:263-266` and `:214` build them, and sent by MWB's own `SkSend` and `TcpSend`.
  The coordinates are the harness's choice: what MWB's hooks would put there in a real session is read from the code.
- MWB's receiver and injection are Windows-only and not run: what the peer does with a decoded package (relative
  injection, the edge test, `NextMachine`, `HideMouse`, dedup of recent ids, taking the cursor back at Seamdesk's point) is
  read from the code above, and needs the check in `tools/mwb-check.md`.
- Garage: only 2.2.1.0327 is tested: a PC on Seamdesk drives a laptop on it day to day. No released PowerToys
  binary is tested either: `48c4bad07` is a source commit after `v0.102.2933.0`.
- Clipboard slices are not byte-identical to MWB's: MWB also sends the spare capacity of its buffer (zeros after the
  deflate stream); MWB's own decoder reads Seamdesk's text.
- The settings-to-session mapping (`Roster.MwbSessions`: version, `Detected`, control, "both ways") and the
  clipboard check are in `Seamdesk.Host` and tested on Linux (`RosterTests`); the link-level gates they feed are tested
  in `Seamdesk.Mwb.Tests`.

Switching: the installer turns off MWB's autostart and leaves MWB installed; the tray's "Go back to Mouse Without
Borders" stops Seamdesk and starts MWB (Windows host, above). The manual check is `tools/mwb-check.md`.

## Clipboard

Per peer: **off** (default), **this machine to the peer only**, or **both ways**. Text only in v1. Each machine's own
setting decides what it sends and what it takes: it sends its text (`ClipboardText`, just before the `Handoff`) when
the cursor crosses into a peer it has set to "this PC to peer" or "both ways", and takes a peer's text only when set
to "both ways". So a direction works only when both machines allow it, and "this PC to peer" guarantees the peer's
clipboard never lands here (`ClipboardModes.Sends` and `Takes`, applied by `Roster`). MWB peers have the same three choices,
through MWB's clipboard message (above). Between Seamdesk peers the text is one `ClipboardText` frame, so text over the
64 KiB frame limit is not sent (seamdesk.log says so); the `Handoff` goes out either way. Seamdesk 0.7.3 and earlier can't
send it, and drop the link when they receive it, so copy and paste between Seamdesk peers needs both on a later version.

## Security model

Threat model: an attacker on the same LAN who can see, inject and replay traffic must not be able to inject input,
read keystrokes or pair themselves.

- **Identities.** Each install creates a key pair and a self-signed certificate on first run, kept per user with
  DPAPI (Windows host, above). No shared static key; MWB derives every session key from one shared secret
  (`Core/Encryption.cs:59`, `:115-123`). `FileIdentityStore` (a user-only file) serves tests and Linux. Schannel
  accepts the key as `X509CertificateLoader.LoadPkcs12` loads it (PC run: all transport tests and the DPAPI test).
- **Pairing with a short code.** Machine A shows an 8-digit code (single use, 2 minutes, 3 wrong tries kill it). B
  opens TLS 1.3 to A with nothing pinned, they exchange `Hello` and run **J-PAKE** with the code (BouncyCastle, NIST
  3072-bit group, SHA-256). The J-PAKE key confirmation is bound to both certificates as each side saw them: it
  confirms HKDF-SHA256 of the J-PAKE key with the label `seamdesk pairing v1`, the protocol version and both SPKI
  SHA-256 fingerprints (dialer, listener). This is channel binding in the style of `tls-server-end-point` (RFC 5929
  §4); .NET 10's `SslStream` exposes no TLS exporter. TLS 1.3's CertificateVerify proves each side holds its key, so
  a man in the middle must show his own certificates and the confirmation fails. One online guess per attempt;
  nothing to guess offline. Every J-PAKE attempt uses up a try from round 1; a normal dial from an unknown peer while
  a code is shown is refused as unpaired and costs none. Success pins the peer's SPKI hash with its machine id and
  address, replacing any pin with that SPKI or that machine id (the other side forgot this machine, or the peer got a
  new identity, and it paired again): the old key is no longer trusted, and `Node` stops dialling it. The supervisors
  then connect afresh. Settings that hold two pins of one SPKI or one machine id, as earlier versions saved them after
  such a re-pair, load with the later one.
- **Channel.** TLS 1.3 only, mutual, both certificates checked against the pins straight after the handshake, so a
  refusal carries its reason (`Refuse`: "<peer>'s identity changed: pair again"). The dialer accepts only the pin
  of the peer it dialled; the listener any pinned one. The listener writes each connection it refuses to seamdesk.log
  with the source address and the reason (`Node.Log`: not a private address, a failed TLS handshake, unpaired, a
  wrong or dead pairing code, a version mismatch), and each pairing it accepts. Repeats are counted, not written each
  time (`Repeats`, shared with MWB's listener), and the counts start over past 1000 distinct lines. An accepted socket
  closes gently (`GentleStream`): FIN first, then what the dialler still sends is read and dropped until it closes too,
  for at most 2 s. Closing with bytes unread sends RST, and Windows then drops the `Refuse` from the dialler's buffer
  before it reads it ("forcibly closed" instead of the reason). At most 4 connections from one source address and 16 in
  all may be in their handshake at once (`Node.MaxHandshakesPerSource`, `MaxHandshakes`); more are closed unread and
  logged ("too many connections still in their handshake"). A slot is freed once the peer is authenticated or its
  socket is closed.
- **Version handshake and Ready.** The first frame is `Hello { protocol, appVersion, machineId }`; a protocol mismatch
  gets `Refuse { reason, theirVersion }` and is retried every 5 minutes. States `Tls -> PinChecked -> HelloOk ->
  Ready`; only a Ready connection exists as a `Connection`, and any other frame before Ready closes it. Frames: a
  4-byte big-endian length (at most 64 KiB), a type byte and the fields. A frame with no type (a bug) or over the
  limit throws `ArgumentException` before a byte is written, and the link stays up; MWB's link throws the same for
  a frame it has no translation for.
- **Network.** One TCP port, 15200 (MWB uses 15100/15101, `Class/TcpServer.cs:144`), accepting only private,
  link-local and loopback sources (RFC 1918, 169.254/16, fc00::/7, fe80::/10). MWB listens on every interface with no
  source check (`Class/TcpServer.cs:42`). No discovery: peers are dialled by the stored name, re-resolved each time.
- **Either side may dial;** the first authenticated connection wins. When both come up at once, both keep the one
  dialled by the lower machine id. A machine whose firewall blocks inbound traffic still works by dialling out.
- **Privileges.** Per-user install under `%LOCALAPPDATA%\Seamdesk`, `asInvoker` manifest, autostart through
  `HKCU\...\Run`. No service, no admin, no SYSTEM; MWB starts a SYSTEM copy on the Winlogon desktop
  (`Service/Worker.cs:78`).

## Reconnect model

One supervisor per paired peer, never stopped; the app sees each peer as an `IPeer` (`SendAsync`, `StateChanged`,
`Kick`). `Supervisor<TLink>` works on any `ILink` (`Closed`, `DialedBy`, `SendAsync`), so an MWB peer is the same
supervisor over an `MwbLink`. Time and jitter are injected.

- **Backoff.** First dial at once; while disconnected retry after 1 s, doubling to 30 s, ±20 % jitter. It never
  disarms; MWB stops retrying once any socket is up or after a network change on the lock screen
  (`Form/frmScreen.cs:427-449`, `Core/InitAndCleanup.cs:248-272`; #51077). `Kick` resets it.
- **Liveness.** A ping every 2 s; any frame counts. 6 s of silence or a socket error hands the link back to the
  supervisor. Crossing into a down peer does nothing and the tray says "<peer>: reconnecting" (MWB cancels silently:
  `Core/Event.cs:171-177`).
- **Lock.** Connections stay open across desktop switches (MWB closes every socket: `Form/frmScreen.cs:322-353`;
  #51077). **Sleep:** connections die with the NIC; resume kicks a retry.

## Deliberately dropped from MWB

- **Control of the lock screen, UAC prompts and Ctrl+Alt+Del:** only a SYSTEM process can inject there, which MWB gets
  from its service (`Service/Worker.cs:78`). Seamdesk never runs as SYSTEM: unlock a machine with its own keyboard.
- **The shared security key** (except in opt-in compatibility mode), **absolute moves and "Move mouse relatively"**,
  **the 2x2 matrix, "devices in a single row" and "wrap mouse"**, **file sharing and screen capture**, and the
  **4-machine cap** (none in the model; 2 machines are tested, routing through a third is untested).

## Tests

`dotnet test Seamdesk.slnx` runs Core, Transport, Host and Mwb tests everywhere, plus `Seamdesk.App.Tests` on Windows. Mwb
tests use real sockets on loopback, a fake MWB peer on Seamdesk's codec, and the vectors from `tools/mwb-vectors`. Core and
Host expected values are hand-computed from `ExampleDesk` ("portrait + offset 1080p + laptop below"). Transport tests
use real sockets and TLS on loopback with `FakeTimeProvider`. Over ssh a Windows PC runs in session 0, which has no
input desktop (PC run: `GetCursorPos` fails with 1459, one 1024x768 dummy monitor), so hooks, the hidden cursor and
real monitors are checked by `tools/smoke.ps1` on the desktop itself.

| Test | 1. Protects | 2. Fails when | 3. Not already covered because | 4. Seam? |
|---|---|---|---|---|
| `DeskTests.Move_on_example_desk` (14 rows) | Crossings, local moves, no-neighbour stays, 1:1 deltas, dead-zone clamp, corner overshoot | Bounding-box edge as MWB (5 rows, by mutation); proportional arrival or scaled deltas | The one primary test of `Desk.Move` | No |
| `DeskTests.A_pixel_back_after_crossing_does_not_cross_back` (4 rows) | After a crossing on each shared edge, one pixel back stays on the new machine and short of its edge pixel (where the hook's push counts as a crossing) | Arrival on the edge pixel, or 1 pixel in (each fails all 4, by mutation) | The flip-flop contract; the table above pins the exact arrival | No |
| `DeskTests.Portrait_right_lower_stays_when_laptop_does_not_touch` | No neighbour: the cursor stays | Nearest-machine matching | Needs a 60 px gap fixture | No |
| `MoveCoalescerTests` (2) | Many deltas → one sum; none lost across threads | Last delta instead of sum; lock removed (mutation) | Only coalescer tests | No |
| `PairingTests.Right_code_pins_both_sides_and_they_connect` | Right code: each side pins the other (SPKI, machine id, address); the new pin's supervisor then completes the pinned TLS + Hello + Ready handshake | Wrong or missing pin; normal handshake broken | Only end-to-end happy path | `Node.PeerPort`: two nodes on one box need two ports |
| `PairingTests.Pairing_again_under_the_same_name_replaces_the_old_pin` | Regression: B pairs again under its name with a new key; A keeps only the new pin, stops supervising the old one, and the old key's dial is refused as unpaired | Old pin kept by name. Failed on the pre-fix code (A held both pins); passes after | The other pairing tests start with nothing pinned or the same key | `PeerPort` |
| `PairingTests.Saved_pins_keep_only_the_newest_for_a_name` | Settings saved with two pins of one name load with the later one | Loading the list as saved (failed before the fix: all three pins) | The test above covers pairing, not loading | No |
| `PairingTests.Three_wrong_codes_kill_the_code` | 3 attempts, then even the right code is refused; nothing pinned | Attempts not counted (mutation: fails) | Only test of the try limit | `PeerPort` |
| `PairingTests.Normal_dials_from_a_peer_A_forgot_do_not_use_up_the_code` | Regression: B still pins A and keeps dialling while A shows a code; those dials are refused as unpaired and the code still pairs | Counting a try before J-PAKE starts. Failed on the pre-fix code (A took B's dial for pairing: `Pake received while waiting for ReadyFrame`); passes after | The wrong-code test uses pairing dials only | `PeerPort` |
| `PairingTests.Code_expires_after_two_minutes` | At 119 s a guess still runs J-PAKE; at 120 s the right code is refused | No expiry (mutation: fails) | Only test of expiry | `PeerPort` |
| `PairingTests.Relaying_man_in_the_middle_with_the_right_code_fails_on_both_sides` | A relay with its own certificates passing every J-PAKE message through, with the right code, fails both sides; nothing pinned | Fingerprints dropped from the binding (mutation: pairing succeeds) | Only test where both TLS legs are valid but different | `PeerPort` |
| `ConnectionTests.Listener_refuses_a_certificate_it_has_not_pinned` | After pairing, an unpinned key claiming the peer's name is refused with a reason | Listener pin check skipped (mutation: fails) | The impostor pins A, so only A's check stands | `PeerPort` |
| `ConnectionTests.Listener_logs_why_it_refused_a_connection` (2 rows: unknown certificate, wrong pairing code) | The listener's log gets one line with the source address and the reason the dialler was sent | Refusals swallowed on the listening side (failed before the fix: nothing logged) | The other listener tests read the dialler's side only | `Node.Log` (the app's own hook) |
| `ConnectionTests.Repeated_log_lines_are_counted_in_bounded_memory` | Past 1000 distinct lines the repeat counts start over (a line seen before is logged again) | Counts kept for every line ever seen (failed before the cap) | Nothing else checks the counts' size | No |
| `ConnectionTests.A_refused_dialler_reads_the_reason_even_when_it_sent_bytes_the_listener_never_read` | A dialler that sent 32 KiB the listener never read, and reads half a second later, still gets the `Refuse` | Closing the socket at once (RST): **fails on Windows** (3 of 3 runs before the fix: nothing read); Linux keeps the bytes, so it passes there either way | The other refusal tests read before the RST lands; this was their flake on Windows | No |
| `ConnectionTests.Handshakes_in_progress_are_capped_per_source_and_in_total` | Past 4 stalled handshakes from one address, or 16 in all, the next is closed at once and logged; a paired peer from another address still connects | No cap (before the fix: the 5th from one address stayed open) | Nothing else opens connections that never finish | No (loopback source addresses) |
| `ConnectionTests.Dialer_refuses_a_peer_whose_certificate_changed` | The dialer refuses a changed key at the pinned address: "alpha's identity changed: pair again" | Dialer check skipped (mutation: fails) | The impostor pins B, so only B's check stands | `PeerPort` |
| `ConnectionTests.Listener_closes_on_frames_out_of_order_and_on_version_mismatch` (3 rows) | Read off the wire: input before `Hello` gets `[Hello]` then close; input after `Hello` gets `[Hello, Ready]` then close; protocol mismatch gets `[Hello, Refuse{reason, "0.1.0"}]` then close | Frames before Ready ignored (2 rows fail); no protocol check (1 row fails) | Only wire-level test of the state machine | No: raw TLS client + `FrameCodec` |
| `ConnectionTests.Crossed_dials_settle_on_the_one_dialled_by_the_lower_machine_id` | B's connection is up on both ends, then A's in-flight dial completes: both ends switch to A's (a frame crosses it) and close B's | First connection always wins (mutation: fails) | Only test of the tie-break; a truly simultaneous race caught that mutation only sometimes | `PeerPort` |
| `ConnectionTests.Clipboard_text_goes_before_the_handoff_and_a_frame_the_wire_cannot_carry_throws_without_dropping_the_link` | Regression (0.7.3 dropped the link and the crossing): clipboard text then a `Handoff` arrive in order over a real pair; a frame with no type and text over 64 KiB throw `ArgumentException`, and the next frame still arrives | No `ClipboardText` type (failed before the fix); the link closed on a refused frame, or the length unchecked on write (mutations: fail) | Nothing sent `ClipboardText` between Seamdesk peers; `RosterTests` check who sends and takes it, not what the wire refuses | `PeerPort` |
| `ConnectionTests.Silent_peer_is_dropped_after_three_missed_pings_and_redialled` | Any frame is liveness; drop exactly 6 s after the last frame; `SendAsync` false while down; redial after 1 s | Only pings count (mutation: fails); dead after 2 missed (mutation: fails) | Only liveness test; only test of a redial after a drop | `PeerPort` |
| `SupervisorTests.Backoff_doubles_from_1s_to_30s_with_jitter_and_never_stops` | 0.8, 2.2, 3.2, 8.8, 12.8, 33, 24, 33 ... s for jitter 0.8/1.1, twelve waits | No 30 s cap (mutation: fails); disarming | Only backoff test | No: injected time and random |
| `SupervisorTests.Kick_retries_at_once_and_restarts_the_backoff` | Kick while waiting 4 s: dial at once, next wait 1 s | Kick ignores the backoff count (mutation: fails) | Only Kick test | No |
| `SupervisorTests.A_peer_that_refused_our_version_is_retried_every_5_minutes` | Version refusal: 5 min, not doubling | Version refusal on normal backoff (mutation: fails) | Only test of it | No |
| `AddressFilterTests.Only_loopback_private_and_link_local_sources_are_allowed` (23 rows) | The listener's source filter, edges of each range, IPv4-mapped IPv6 | IPv4-mapped not unmapped (mutation: fails); off-by-one in a mask | Pure function, tested as one (brief) | No |
| `IdentityTests.Identity_survives_a_restart_in_a_user_only_file` | Same key after a restart; key file mode 0600 | New key each launch, so every pin breaks (mutation: fails); default file mode (mutation: fails) | Only identity test | No |
| `IdentityTests.A_stored_identity_that_cant_be_read_is_reported_within_the_timeout_and_never_overwritten` (2 rows: refuses, never answers) | A store that throws `CryptographicException` or doesn't answer in the timeout surfaces as `UnreadableIdentityException`, and nothing is saved over it | No timeout, the caller waits on the store (pre-fix: blocked the full 5 s); refusal not recognised (pre-fix: raw `CryptographicException`) | Only test of a store that fails | No: the store interface |
| `StartupTests` | The watchdog names the step that ran past 20 s, never while a dialog waits for the user (the clock restarts after it) or once the tray is up | Clock not restarted after a dialog (mutation: fails) | Only startup test | No |
| `CursorTests.A_local_move_crosses_only_when_pushing_past_an_outer_edge_onto_a_neighbour` (7 rows) | Clipped, unclipped and diagonal hook points cross; arrival at the same desk x; the crossing's own hook callback parks the cursor (`Place` hidden at the park point); no crossing one row short, sliding along an edge, or with nothing past it; each crossing logs one line with the edge, the edge pixel and the entry point, and a non-crossing none | Edge push removed (M1); push without outward motion (M2); the edge named from the last point instead of the edge pixel (the diagonal row: "right") | Only test of local crossing detection | No: `Cursor` is the host's boundary |
| `CursorTests.Crossing_is_blocked_while` (5 rows) | Stay, held button, auto-pause, peer down, peer locked each block; lifting each crosses | Each check removed (M7-M11); `Locked(false)` ignored (M12) | Only test of the blockers | No |
| `CursorTests.Driving_sends_hook_deltas_from_the_park_summed_and_ahead_of_a_click` | Deltas from the park point from the first event after the crossing; summed; a click and a key after the move; each move counts its hook events (2, then 1) for the MWB timing lines; the first hook point after parking is logged once | Deltas from the exit point (M3); input queued without flushing the move (M4); the count never started (mutation: fails) | Only Driving-forwarding test | No |
| `CursorTests.Driving_ends_and_the_cursor_reappears_here` (6 rows) | Hand-back on our monitors, off them, link drop, peer locked, snap-back, our secure desktop; each logs one "back from" line with where and why; every return but the edge crossing shows the cursor at once (`Show` at the return point); the next hook move (the park point plus the hand's motion) is swallowed and the hook shows the cursor at the return point (`Place` shown), then local input passes from there and its first point is logged | Link drop keeps Driving (M13); off-monitor point used (M15); holder not told on lock (M20); a return without its line; the move after a return passed at the park point (0.7.8: a reported "jumps to where the cursor was"; its first assertion failed on 0.7.8); a still return left hidden until the mouse moves (the five non-edge rows failed on 0.7.9) | Only test of leaving Driving | No |
| `CursorTests.Held_applies_moves_1to1_follows_the_local_mouse_and_hands_the_cursor_on_at_the_edge` | 1:1 absolute targets, local-mouse resync, crossing out sends `Handoff`, held key released, later moves ignored | Keys not released (M5) | Only test of the Held path | No |
| `CursorTests.Held_drag_stops_at_the_edge_instead_of_crossing` | A held button keeps the cursor on the machine | Drag check removed (M6) | Only drag test | No |
| `CursorTests.Held_ends_and_every_key_the_controller_held_is_released` (4 rows) | Taken elsewhere, link drop, secure desktop, snap-back here: keys and buttons released | Keys not released (M5); link drop ignored (M13) | Each way out of Held | No |
| `CursorTests.A_machine_on_its_secure_desktop_refuses_the_cursor` | `Handoff` while locked → `Locked(true)` back, stays Local | Secure check removed (M14) | Only test of the refusal | No |
| `ScreensTests.GetMonitorInfo_edges_become_desk_rects_and_the_cursor_parks_on_the_primary` | Win32 edges → `Rect`s, virtual box, park on the primary even when listed second | Park on the first monitor (M19) | Only mapping test | No |
| `ScreensTests.Normalized_coordinates_land_on_every_pixel` (2 rows) | Every pixel round-trips under both plausible Windows mappings, incl. a negative origin | `p * 65535 / w` (M18) | Only test of normalisation | No |
| `HotkeyTests` (12: 6 parse, 5 reject, 1 key script) | Parsing; exact modifiers, left/right alike, fires once per press, swallows only the hotkey's key | Subset modifiers match (M16); auto-repeat fires again (M17) | Only hotkey tests | No |
| `HotkeyTests.A_captured_key_shows_as_text_that_parses_back` (5 rows) | The capture field's key press becomes the text shown and saved, and `Parse` reads it back; no modifier, or a key Seamdesk can't name, gives nothing | Modifier check dropped from `Hotkey.From` (U5: fails) | Parse tests start from text, not from a key press | No |
| `HotkeyTests.Conflicts_with_Windows_block_and_app_shortcuts_warn` (10 rows) | The other Seamdesk shortcut, a fixed second chord (Ctrl+Alt+P) and Windows' own combinations are refused with the reason; common app shortcuts warn; the defaults pass | Duplicate check removed (U4: fails); the fixed chords allowed, so one key would do both actions | Only conflict test | No |
| `LayoutEditorTests.Touches_are_where_Desk_Cross_moves_the_cursor_once_per_pair` | The editor's crossing preview on the example desk, hand-computed: flush (portrait right edge 1440 px, 1080p bottom 1920 px) and gap (1080p bottom from x 1500 only) | Each pair reported from both sides (U2: fails) | `DeskTests` check single moves, not the edge walk the preview shows | No |
| `LayoutEditorTests.Dragging_snaps_to_the_nearest_edge_within_range` (5 rows) | Snap onto an edge and an alignment, the nearer of two edges in range, corners, nothing in range, and no pull from a monitor not level with the dragged one | Level filter removed (U1: fails); nearest by signed distance (U3: failed only after the "nearer of two" row was added) | Only snap test | No |
| `LayoutEditorTests.Overlapping_machines_block_saving_and_revert_restores_the_saved_desk` | One pixel of overlap gives the problem text; touching does not; Revert restores offsets and clears it | Touching counted as overlap (U6: fails) | Only overlap and revert test | No |
| `SettingsTests.Every_setting_survives_a_save_and_load_and_a_hand_edited_older_file_still_loads` | Every field round-trips; copy and paste and the MWB version are saved as words; `SendClipboard` is never written; the 0.4.0 file from `tools/mwb-check.md` still loads, as Auto, no control, this PC to the peer | Enum saved as a number (U7: fails) | Settings had no test | No |
| `SettingsTests.Duplicate_pins_an_older_Seamdesk_saved_load_as_the_newer_one` | A file with two pins of one SPKI (0.7.3 and earlier saved one on re-pairing) loads with the later one | No heal, or the older pin kept (mutations: fail) | The roster's pairing test checks that no duplicate is saved, not that an old one heals | No |
| `ConfiguredDeskTests.The_editor_starts_a_peer_not_placed_yet_right_of_the_desk_and_the_cursor_leaves_it_off` | The example desk plus a peer not placed yet with a monitor left of and above its origin: the editor puts its leftmost monitor 200 px right of the desk, top aligned (hand-computed: offset (8040, 300)); the cursor's desk is the example desk without it; the two agree on every placed machine | Gap changed, top or left of the peer's monitors ignored (mutations: fail) | The placement rule was page code with no test | No |
| `ConfiguredDeskTests.An_MWB_peer_shows_its_layout_in_use_and_one_without_a_saved_screen_and_a_peer_never_seen_wait` | An MWB peer shows the saved layout in use; an MWB peer with no saved layout and a Seamdesk peer never seen are on neither desk and wait; editor and cursor agree | Layout 0 shown instead of the one in use; no-layout check removed (mutations: fail; the old page indexed the empty list) | The editor's and the cursor's desks were built apart | No |
| `RosterTests.A_new_link_brings_monitors_and_the_layout_and_a_moved_layout_reaches_the_peer` | A new link greets with monitors (saved on the other side) and, at revision > 0, the layout (adopted); the cursor's desk then has the peer, so it crosses into it at the hand-computed point; `Apply` with a moved peer is the next revision and reaches it | No layout in the greeting; no bump on a move; desk not rebuilt on `Monitors`; pins not written to the settings (mutations: fail) | These rules were in `App`, with no Linux test | `PeerPort` |
| `RosterTests.Crossing_takes_the_clipboard_along_only_where_this_machine_sends_it_and_the_peer_takes_it` (4 rows) | Over a real pair: the text goes before the `Handoff` only if this machine sends to the peer and the peer takes from it; the `Handoff` arrives either way | Taken with "this PC to the peer"; sent only both ways (mutations: fail) | `ClipboardModes` alone was tested, not where `App` applied it | `PeerPort` |
| `RosterTests.Removing_a_Seamdesk_peer_forgets_its_pin_and_everything_kept_for_it_but_keeps_the_layout_revision` | Pin, offset, monitors, copy and paste and status gone, in memory and in the file; revision unchanged; no MWB key touched | Copy and paste, status or pin kept (mutations: fail) | Unpair was `App` code with no Linux test | `PeerPort` |
| `RosterTests.Removing_an_MWB_peer_forgets_its_key_and_takes_it_off_every_Seamdesk_peers_desk` | MWB settings, offset and key gone; the next revision reaches a Seamdesk peer and removes the offset there; the Seamdesk pin stays | Key kept; no revision bump (mutations: fail) | Removing an MWB peer was page and `App` code | `PeerPort` |
| `RosterTests.Pairing_saves_the_pin_and_pairing_a_pinned_machine_again_replaces_it` | Regression (0.7.3 then couldn't start): A forgets B, B pairs again; B's saved and live settings hold one pin | Re-pair appended (the pre-fix behaviour, as a `Pins.Add` mutation: fails with two pins); pins not written (mutation: fails) | Pairing tests check `Pins` alone | `PeerPort` |
| `RosterTests.MWB_settings_become_sessions_only_for_peers_Seamdesk_can_link_with` | Sessions only for legacy security on, not paired as Seamdesk (any case), a saved screen and a key, with a log line for the last two; version, `Detected`, control and "both ways" mapped; a missing MWB id made up and reported for saving | "This PC to the peer" taken as both ways (mutation: fails) | The mapping was `App` code with no Linux test | No |
| `ConnectionTests.Unpairing_closes_the_link_stops_dialling_and_refuses_the_peer` | After `Unpair`: B's link closes, A has no pin, B's dial is refused as unpaired, and A never redials B, even past any backoff (A's clock is fake) | Supervisor left running (U8: fails, B reconnects) | Only unpair test | `PeerPort`, as the other connection tests |
| `DpapiIdentityStoreTests.An_identity_kept_with_DPAPI_survives_a_restart_and_Schannel_uses_it_for_mutual_TLS_1_3` (Windows) | Same key after reload; the file is not a usable PKCS#12; Schannel TLS 1.3 mutual handshake with it | Plain file (mutation: fails; a first check for raw certificate bytes survived because PKCS#12 encrypts them) | Only DPAPI and only Schannel-identity test | No |
| `DisplaysTests.Monitors_cover_the_virtual_screen_with_one_primary_at_the_origin` (Windows) | Enumeration against `GetSystemMetrics` | Wrong primary flag (mutation: fails) | Only enumeration test | No |
| `InjectorTests.SendInput_accepts_the_INPUT_layout` (Windows) | `SendInput` takes our `INPUT` (error 5 in session 0, success on a desktop; never 87) | Size off by 8 (mutation: error 87) | Only injection test runnable over ssh | No |
| `VectorsTests.Seamdesk_decrypts_encrypts_and_stamps_the_captured_stream_exactly_as_MWB` (2 formats × 2 directions) | Against streams captured with MWB's own code at `a9e1f0f20^` and `48c4bad07`: Seamdesk's decryption equals MWB's, re-encryption gives the captured bytes (after the current format's clear header), and each package's magic and checksum are stamped as MWB's | PBKDF2 salt as ASCII (M11); checksum over 64 bytes (M10); current iterations 50000 (the harness itself fails: "MWB got Error before trusting Seamdesk") | Only test against MWB's code; it also covers keys, IVs and magic | No: vectors from `tools/mwb-vectors` |
| `VectorsTests.Input_frames_become_the_packages_MWB_decoded` (2 formats) | Each of 17 frames sent while driving (arrival, two moves as absolute positions over the peer's box, every button, both wheels, keys) becomes the package MWB's decoder read, apart from id and stamps | Zero delta offset the wrong way (M12); moves sent relative (0.7.7: fails) | Only test of the frame-to-package mapping | No |
| `VectorsTests.Packages_from_an_MWB_controller_become_the_frames_it_meant_and_the_hand_back_is_what_MWB_decoded` (2 formats) | 18 controller packages built as MWB builds them (arrival, relative and absolute moves, every button, both wheels, an absolute click, keys) become the frames written by hand in the harness; Seamdesk's `NextMachine` is the package MWB's decoder read | Trusting before the answer (D1) | Only test of the driven mapping against MWB's packages | No |
| `LinkTests.No_input_reaches_the_peer_before_its_handshake_answer` | Before the peer's answer: `SendAsync` is false and Seamdesk sends only its handshakes and answers | Trusting without an answer (M1, D1) | Only pre-trust test, driving | `MwbNode.PeerPort`, as `Node.PeerPort` |
| `LinkTests.No_input_from_the_peer_is_taken_before_its_handshake` | A peer allowed to control this machine sends a move, a click and a key before answering Seamdesk's handshake: once trusted, nothing but the layout was surfaced | Input taken during the handshake (D2) | Only pre-trust test, driven | `PeerPort` |
| `LinkTests.A_peer_with_another_key_is_dropped_with_a_plain_reason_and_logged_without_either_key` (2 formats) | Another key, old or current stream: no link, and the reason says the key doesn't match; `seamdesk.log` has the dial, what the stream start opened as and the same reason, and neither key's text | Detection and the handshake's own check both ignoring the key (D4c); each alone is caught by the other (D4a is caught by the format tests; D4b survives, see below); a log line carrying the key, or a refusal not logged | Only wrong-key test, and the only check that the log never holds a key | `PeerPort`, `MwbNode.Log` (the app's own hook) |
| `LinkTests.Auto_answers_in_the_format_the_peer_speaks_and_says_which` (2 formats) | Auto answers an old or a current peer in its format, reports it, and drives it | Every stream read as old (D4a) | Only test of detection on loopback | `PeerPort` |
| `LinkTests.A_format_other_than_the_chosen_or_the_one_auto_saw_is_refused_with_the_reason` (3 rows) | Old chosen, current peer; Current chosen, old peer; Auto after Current was seen (from settings), old peer: each refused with its sentence, no link | Auto's no-fallback rule removed (D5a); every stream read as old (D4a) | Only test of the refusals | `PeerPort` |
| `LinkTests.Auto_that_saw_the_current_format_refuses_the_old_one_on_the_next_link` | Regression: a current link, then the peer comes back old in the same run: refused | Seeing current not kept on the session (D5b: the pre-fix code, where `CurrentSeen` came only from settings at start; fails, passes after) | The row above starts from the setting, not from a link | `PeerPort` |
| `LinkTests.The_listener_answers_only_the_configured_address_of_a_peer_allowed_to_control_this_machine_and_logs_why` (3 rows) | The configured address with control on gets MWB's handshake back; another loopback address, or control off: closed with no bytes; `seamdesk.log` says accepted, or refused with the reason (not a configured peer; incoming control off) | Address check removed (M2, D3); control switch ignored (D6a); a refusal not logged, or logged with the wrong reason | Only accept-side test | `PeerPort`, `MwbNode.Log` (the app's own hook) |
| `LinkTests.The_peers_clipboard_input_and_other_packages_are_dropped_unless_allowed_and_never_answered` | With control and copy and paste from the peer off: the peer's clipboard text, mouse, keys and 11 other types: nothing surfaced, nothing sent back; `Hello` still answered | Peer clipboard surfaced (M5, D7) or echoed back (M6); input taken with control off (D6b) | Only test of what is dropped | `PeerPort` |
| `LinkTests.The_peers_input_drives_this_machine_and_the_desk_hands_the_cursor_back` | Hand-computed: arrival at (480, 1079), relative and absolute moves, click, X2, horizontal wheel, extended key; Seamdesk's hand-back is `NextMachine` at (14635, 22) naming the peer; `HideMouse` ignored after it, and ends control after a second arrival | Trusting before the answer (D1) | The vectors replay MWB's packages; this one checks the arithmetic by hand | `PeerPort` |
| `LinkTests.The_peers_clipboard_text_is_taken_with_copy_and_paste_both_ways` | With "both ways": RTF and text with spare zeros after the deflate stream: exactly the text surfaces | Taking the RTF section or keeping the zeros (would fail) | The vectors check MWB's sender once; this checks the parser's edge cases | `PeerPort` |
| `LinkTests.A_crossing_lands_where_the_desk_says_moves_follow_1to1_and_the_cursor_comes_back_only_across_the_shared_edge` | A fake peer that applies Seamdesk's packages as MWB's receiver does (relative: injected, then its own one-pixel edge test and `NextMachine`; absolute: placed, no edge test): the arrival at (1000, 0), 50 px along the shared edge and 50 down leave its cursor at exactly (1050, 50) with no hand-back; 51 up hand the cursor to `pc` (1050, 1079) once, after its cursor reached (1050, 0), with one `HideMouse` and no relative move ever | Relative moves, as 0.7.7 (fails: "the cursor bounced: Handoff pc (1001, 1079)" on the first move along the edge) | The other link tests check packages one by one, not the peer's cursor across a crossing | No |
| `LinkTests.A_hand_back_returns_the_cursor_here_once_and_hides_the_peers_cursor` | `NextMachine` gives `Handoff(pc, desk pixel)` and `HideMouse`; a second one is ignored | Holding check removed (M9) | Only link-level hand-back | `PeerPort` |
| `LinkTests.Driving_logs_one_timing_line_a_second_an_idle_link_none_and_never_what_was_typed_or_copied` | `FakeTimeProvider`: idle (the first heartbeat, a `Hello` answered, 5 s) logs no timing line; an arrival, three moves written 1, 2 and 3 ms after their first hook event (1, 2 and 3 events), a key and the clipboard log exactly one line a second later, with 4 mouse packages, 3 moves from 6 hook events (at most 3), hook to written 1/2/3/3 ms, and the TCP figures; idle again logs nothing; no line holds the text or the key | A heartbeat opening a window (mutation: fails); a frame written to the log (mutation: fails); a line per package (mutation: fails) | Only test of the timing lines | `MwbLink.Moving` (`Roster`'s own call) |
| `LinkTests.An_8_kHz_mouse_reaches_a_peer_slower_than_it_within_milliseconds_at_about_a_package_a_millisecond_every_pixel_kept` | Real time: one-pixel moves at 8 kHz for 0.5 s (4000 px, inside the peer's box) to a fake MWB that injects one package in 300 µs; the peer's cursor ends on the last pixel, at most 502 move packages, median lag under 5 ms and none over 50 | No pace, as before the fix (fails: 16,000 packages, lag in seconds); held moves never flushed (fails); flushed on a 16 ms timer (fails) | Only test of the pace against a slow peer | No |
| `LinkTests.A_click_or_wheel_goes_at_once_after_the_moves_held_back_before_it` | Frozen clock: two moves go, the next two are held and go as one position ahead of the click, then the wheel; positions hand-computed over the peer's 4480 x 1440 box | A click written before the held moves (fails) | Only ordering test across the pace | No |
| `LinkTests.A_peer_that_stops_reading_while_driven_gets_the_cursor_taken_back_with_one_line_and_its_cursor_let_go` | A fake MWB with a 2 MB window and a LAN's segment size stops reading: within 2048 to 4096 moves `Locked(true)` surfaces, one "stopped reading" line, nothing more while it stays unread; read, the moves up to the last position Seamdesk held, then `HideMouse` | Stall check off (fails) | Only stall test; "reading again" is not (Linux keeps its window shut) | No |
| `LinkTests.A_peer_still_unread_5_s_after_it_stalled_may_be_crossed_to_again_and_stalls_again_with_a_line_each` | Fake clock, real window: moves back and forth (each a new position) to a peer that stops reading surface `Locked(true)`; nothing at 4.9 s; `Locked(false)` at 5 s with the "still unread" line; crossed to again, `Locked(true)` within 100 ms; both stalls logged | Backstop off (fails); backstop at once (fails); a repeated line folded (fails) | Only test of the 5 s backstop | No |
| `LinkTests.A_link_silent_for_two_of_the_peers_heartbeats_and_a_minute_is_closed_and_says_why` | Fake clock: a peer heard at 10 minutes keeps the link at 20; at 21 it is closed with the reason, logged | Silence check off (fails); hearing not counted (fails) | Only half-open test | No |
| `LogFileTests.The_log_rolls_at_its_size_keeping_the_newest_lines_and_the_ones_before_and_dropping_older` | 35 lines of 100 bytes at a 1000-byte limit: lines 20-29 in `seamdesk.old.log`, 30-34 in `seamdesk.log`, older gone | Never rolling (fails) | Only log test | No |
| `LinkTests.Frames_MWB_has_no_message_for_send_nothing_and_an_unknown_frame_throws` | `Locked` and a `Handoff` between other machines send nothing; a frame with no case throws `ArgumentException`; the link still answers | Unknown frames silently dropped (failed before the fix); `Locked` thrown on (mutation: fails) | Only test of what the link does with frames it doesn't translate | `PeerPort` |
| `ScreensTests.A_hand_back_lands_where_the_desk_says_on_the_saved_layout_that_fits` (5 rows) | Exit point inverted exactly (incl. the corner pixel); no neighbour gives null; the revealed height picks saved layout 1; no fit is flagged | Exit floored (M7); only the current layout tried (M8) | Only geometry test; pure | No |

MWB compatibility mutations M1-M12 were each run alone and all but one failed a test. M4 (skip the magic-number
comparison) survives and is equivalent for any real key: the AES key and the magic come from the same key, so a wrong
key's packages already fail the checksum (255 in 256 each, and ten handshakes come first). Stage 2 and 3 mutations
D1-D7 (each named in the table) were each run alone; all but one failed at least one test. D4b (packages failing the
key ignored during the handshake loop) survives and is equivalent: format detection already drops a wrong key before
the loop, so the loop's check only guards a stream that goes bad after its first package. D5b is the one bug found in
that work (Auto kept "current seen" only from settings, so a reconnect in the same run could fall back); its test
failed on that code and passes on the fix.
Host mutations M1-M20 were each run alone and each failed at least one test; one Windows mutation survived at first and
changed its test (above). Transport: every mutation named was run; three survived at first and changed the code or the
test (git `19485d7`). One regression test (`Normal_dials_...`); none in that work: no bug reached a commit.
UI mutations U1-U8 were each run alone and each failed a test; U3 survived at first and gained a row (above). One
more survives on purpose: the order modifiers are shown in ("Win+Ctrl" or "Ctrl+Win") is cosmetic, and both parse. The
window itself has no automated test: `tools/Shots` builds every page in every state on Windows and would fail on a
broken one, and its screenshots were checked by eye.
**Interactive:** `tools/smoke.ps1` (about 3 s, watchdog 30 s), on the PC's own desktop with MWB quit: (1) a relative
push down off the highest-bottom monitor crosses to a fake peer at the same x, parks and hides the cursor, and reports
whether the hook clips its point; (2) parked deltas are forwarded 1:1 and the real cursor stays put; (3) after a
hand-back, the next move shows the cursor at the hand-back point; (4) absolute moves to every monitor's corners and centre, and past the top-left, land
on the pixel Seamdesk.Core computes; (5) it reports what a relative +20 px injection does. It restores the cursor.

## Open questions

- **Smoke results:** hook clipping, real-pixel exactness and the hidden cursor are designed to work either way but are
  confirmed only by `tools/smoke.ps1` on a desktop (not yet run).
- **Outbound firewall:** on a test PC, `dotnet.exe` from a user-local SDK got WSAEACCES on outbound sockets while
  PowerShell's web requests worked, so restore ran from a copied package cache. Seamdesk.exe may need an allow rule there.
- **Not exercised on a desktop yet:** `EVENT_SYSTEM_DESKTOPSWITCH` on lock and UAC, auto-pause, and two machines end
  to end; the 3-machine route through a controller is untested. The settings window, tray menu and MWB dialog have
  been rendered offscreen only (`tools/Shots`, in session 0): dragging, the capture field, pairing between two PCs and
  "Go back to Mouse Without Borders" need a run on a desktop.
- **Screenshots** use Windows 11's default blue accent (session 0 reports orange) and a drawn title bar; the real
  window takes the user's accent and the system title bar.
- **Managed PCs** may block an unsigned per-user exe by policy: compatibility mode covers the wait; signing is deferred.
- **Tests over ssh with key authentication** (PC run): DPAPI and the user key store refuse with "Access
  denied", so the 14 Transport tests that make identities and `DpapiIdentityStoreTests` fail there, identically on
  `main`. They need a password logon or the PC's own desktop; Core, Host, Mwb and the other App tests pass over ssh.
- **Start that hung with no tray** (PC run): suspected the `identity.dpapi` an ssh run had written. Disproven
  for DPAPI: that file and a fresh one both name the user's preferred master key, and over ssh both decrypt and load
  (`Unprotect` + `LoadPkcs12`) in under 30 ms. Where that start stopped is unknown; the per-step log and the watchdog
  will name it next time.
- **MWB compatibility against a real peer:** a PC on Seamdesk drives a laptop on Garage 2.2.1.0327 day to day.
  PowerToys MWB peers (either format) and MWB driving Seamdesk have not run against a real peer:
  `tools/mwb-check.md` is a 2-minute manual check. Known gaps are listed under "MWB compatibility mode".
