# Contributing to Seamdesk

Thanks for helping. Bug reports, Mouse Without Borders compatibility reports and pull requests are all welcome. For a
security problem, please don't open an issue: follow [SECURITY.md](SECURITY.md) instead.

By contributing, you agree that your contribution is licensed under the project's MIT licence ([LICENSE](LICENSE)).

## Build and test

You need the .NET 10 SDK. Packages come from nuget.org only (`nuget.config`), and every project's `packages.lock.json`
pins them: commit the lock file with any package change.

```sh
dotnet test Seamdesk.slnx
```

This runs on Linux and Windows. The transport tests open loopback sockets and need TLS 1.3 (OpenSSL 1.1.1 or later on
Linux, Windows 11). `tests/Seamdesk.App.Tests` runs on Windows only.

## How the code is laid out

| Project | Runs on | What it holds |
|---|---|---|
| `src/Seamdesk.Core` | anywhere | Desk layout, edge crossing, movement, move coalescing. No Win32 and no I/O. |
| `src/Seamdesk.Transport` | anywhere | Identities, pairing (J-PAKE), the pinned TLS 1.3 channel, frames, the reconnect supervisor. No Win32. |
| `src/Seamdesk.Host` | anywhere | Every decision: who has the cursor, the peers, the configured desk, hotkeys, monitor mapping, settings. No Win32. |
| `src/Seamdesk.Mwb` | anywhere | Mouse Without Borders compatibility: both wire formats, the handshake, a link per MWB machine. No Win32, no packages. |
| `src/Seamdesk.App` | Windows | The exe: hooks, injection, the hidden cursor, DPAPI, system events, tray and the settings window (WPF). |

Some rules keep it that way:

- **Logic goes in Host, not App.** App only turns Win32 events into calls on Host and carries out the effects Host
  returns. If you can test it on Linux, it belongs in Core, Transport, Host or Mwb.
- **Time is injected.** Transport and Mwb take a `TimeProvider` (and a `Func<double>` for jitter); never use wall-clock
  timers there. In tests, freeze or fake the clock of any node whose timeout could hide the behaviour you're testing.
- **Mouse Without Borders claims cite MWB's own code**: a file and line in PowerToys at the commit the design names
  (`a9e1f0f20^` for the old format, `48c4bad07` for the current one). MWB is MIT; if you copy any of its code, keep its
  licence header and the notice in `THIRD-PARTY-NOTICES.txt`.
- **No personal details** in code, tests, logs or docs: no real host names, addresses, user names or keys. Never put a
  real Mouse Without Borders key anywhere.
- **Windows PowerShell 5.1** syntax for everything under `installer/` and `tools/*.ps1`.

## Tests

`docs/design.md` has a table of every test with four answers: what it protects, which regression makes it fail, why
nothing else already catches that, and whether it needed a seam. Add your test's row there. A few habits:

- A bug fix comes with a test that fails on the old code and passes on the fix. Say in the pull request that you saw it
  fail.
- One test per behaviour, at the strongest boundary; extend a table-driven test before writing a near-copy.
- Test what the code does, not how: a test that breaks under a refactor that changes no behaviour needs rewriting.
- Expected values come from the geometry or the spec, never from the code under test. `ExampleDesk` (portrait,
  offset 1080p, laptop below) is the shared desk; its expected points are worked out by hand.

## Checks that need a Windows desktop

Some things can't be tested over a remote session (no input desktop, one virtual monitor):

- `tools/smoke.ps1`: hooks, injection, the hidden cursor and real-pixel moves. Run it on the PC's own desktop with
  Mouse Without Borders quit; read its header first.
- `tools/mwb-check.md`: about five minutes with a machine that runs Mouse Without Borders, both directions.
- `tests/installer/Installer.Tests.ps1`: installs, upgrades, goes back and uninstalls in a scratch folder with
  fixtures; it never touches the real Mouse Without Borders or Run key.
- `tools/Shots`: renders every window in every state to `docs/screenshots/`. Regenerate them when the UI changes.

`tools/mwb-vectors` (not in the solution) runs Seamdesk's MWB link against MWB's own code from a PowerToys checkout and
rewrites the test vectors; its README explains how.

## Pull requests

- Keep each one to one change, with a short description of what and why.
- Run `dotnet test Seamdesk.slnx`, plus the Windows checks above if you touched what they cover.
- Update `docs/design.md` when you change a decision it records, and `CHANGELOG.md` for anything users notice.

## Releases

`docs/release.md` describes the release zip, how `tools/release.ps1` builds it, and what the installer does.
