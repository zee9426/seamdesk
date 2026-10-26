# Changelog

All notable project changes are recorded here.

## [0.9.0] - 2026-10-26

- **release**: Renamed to Seamdesk with a one-time migration from Glide, gentler connection close and handshake limits, public-tree scrub, README, logo, screenshots and the public snapshot tool

Up to 0.8.0 the project was called Glide.

## [0.8.0] - 2026-10-25

- **security**: Security review fixes: re-pairing replaces a machine's old key and Remove unpairs it fully, the listener logs why it refused a connection, bounded repeat-log memory; plus SECURITY.md, a security doc, licence notices in the release, Dependabot and locked NuGet restores

## [0.7.11] - 2026-10-25

- **cursor**: Crossings land 2 pixels inside the next machine so a pixel of jitter at a shared edge no longer flips the cursor back and forth

## [0.7.10] - 2026-10-23

- **cursor**: Show the PC cursor at the return point at once when the hand is still (hotkeys, dropped link, lock, quit) instead of leaving it hidden until the mouse moves

## [0.7.9] - 2026-10-23

- **cursor**: Place the PC cursor inside the mouse hook at crossings and returns, swallowing the in-flight move, so the cursor appears at the edge being crossed instead of jumping to its last spot; log where it really landed

## [0.7.8] - 2026-10-23

- **mwb**: Drive MWB peers with absolute positions on Seamdesk's desk so crossings no longer bounce or re-accelerate; log every crossing and return with its reason; Ctrl+Alt+H / Ctrl+Alt+P as second snap-back and stay chords

## [0.7.7] - 2026-10-23

- **mwb**: Pace MWB moves at about one package a millisecond (8 kHz lag 3.5 s -> 1 ms), bring the cursor home from a peer that stops reading with a 5 s backstop, close silent links, roll seamdesk.log at 4 MB

## [0.7.6] - 2026-10-23

- **mwb**: MWB lag capture: while Seamdesk drives a Mouse Without Borders peer, seamdesk.log gets one timing line a second (packages, moves per hook events, hook-to-written and write times, TCP rtt and peer window); nothing while idle, never keys or clipboard

## [0.7.5] - 2026-10-23

- **host**: Architecture clean-up: the peer roster (pins, remove, copy and paste, MWB sessions) moves into Seamdesk.Host over both nodes, and the cursor and the layout editor build their desk from one module; App.cs 733 to 435 lines, 170 Linux tests

## [0.7.4] - 2026-10-22

- **transport**: Copy and paste between Seamdesk peers no longer drops the link (the crossing always goes out), and re-pairing a pinned machine no longer stops Seamdesk from starting; existing duplicate pins heal on load

## [0.7.3] - 2026-10-22

- **installer**: Installer retries briefly locked files on upgrade and uninstall; Seamdesk reports its real version (from CHANGELOG.md)

## [0.7.2] - 2026-10-22

- **mwb**: Pairs with standalone (Garage) MWB 2.x: its SHA-1/SHA-256 key derivation in Auto and Old, every MWB link step in seamdesk.log, tested against the installed program

## [0.7.1] - 2026-10-22

- **app**: Startup never hangs silently: per-step start log, 20 s tray watchdog, identity read with a 10 s timeout and a fresh-identity offer

## [0.7.0] - 2026-10-22

- **mwb**: MWB compatibility complete: current PowerToys MWB (0.101+) format with Auto/Old/Current per peer, MWB machines can control Seamdesk (off by default), copy and paste both ways per peer, vectors for both formats both ways, settings UI and screenshots

## [0.6.0] - 2026-10-22

- **installer**: Per-user installer and uninstaller, self-contained release zip, MWB autostart backup/restore wired to the tray, Seamdesk.exe --dial diagnostic, install and release docs

## [0.5.0] - 2026-10-22

- **ui**: Settings window (machines, pairing, MWB peers, per-peer clipboard, shortcuts, behaviour, about), drag-and-drop layout editor with crossing preview and saved MWB laptop layouts, tray menu with Go back to MWB, MWB-running dialog; Fluent light/dark; 56 screenshots; 136 tests

## [0.4.0] - 2026-10-22

- **mwb**: Seamdesk.Mwb: drive a pre-0.101 Mouse Without Borders peer (legacy security, opt-in, address-locked), one-way text clipboard, hand-back; vectors from MWB's own code both ways; a manual MWB check for contributors; 110 tests

## [0.3.0] - 2026-10-22

- **windows**: Windows host: Seamdesk.Host cursor ownership state machine + hotkeys + monitor mapping (pure), Seamdesk.App hooks/SendInput/hidden cursor/DPAPI/system events/tray, smoke tool; design decisions recorded in docs/design.md; 99 tests (3 Windows-only)

## [0.2.0] - 2026-10-22

- **transport**: Seamdesk.Transport: J-PAKE code pairing bound to cert fingerprints, pinned mutual TLS 1.3, framing + version handshake, LAN-only filter, never-disarming reconnect supervisor, IPeer seam; 39 transport tests

## [0.1.0] - 2026-10-22

- **core**: Seamdesk design doc and pure Seamdesk.Core layout/movement library (per-monitor desk plane, edge crossing, 1:1 deltas, 8 kHz coalescing), 17 tests

