# Releasing and installing Seamdesk

## What a release is

`Seamdesk-<version>-win-x64.zip`, with `Seamdesk-<version>-win-x64.zip.sha256` beside it:

```
Seamdesk-<version>-win-x64\
  install.ps1  uninstall.ps1  mwb-autostart.ps1
  app\Seamdesk.exe + the .NET and WPF runtime (self-contained, win-x64; the PC needs no .NET)
  app\LICENSE  app\THIRD-PARTY-NOTICES.txt  app\dotnet-THIRD-PARTY-NOTICES.txt
```

The licence and notices sit in `app\` so an install carries them too: Seamdesk's MIT licence (with Microsoft's line for
the code ported from Mouse Without Borders), the notices of PowerToys, BouncyCastle and the .NET runtime, and the .NET
runtime's own third-party notices, taken from the runtime pack the publish used.

It is one **folder**, not a single file. WPF cannot be trimmed, and a single file would unpack its native WPF libraries
to a temp folder on every start (slower start, ~160 MB either way). A folder also upgrades in place and is easier for
antivirus to scan once. The zip is not signed (no code signing, no MSIX); Windows SmartScreen may warn on first run.

## Cutting one

On Windows, with the .NET SDK (a user-local SDK works):

```powershell
powershell -ExecutionPolicy Bypass -File tools\release.ps1 [-Version 0.5.0] [-Out <dir>]
```

`-Version` defaults to the top entry of `CHANGELOG.md`. The script publishes with
`src/Seamdesk.App/Properties/PublishProfiles/win-x64.pubxml`, restoring exactly what the committed `packages.lock.json`
files pin (locked mode: a lock that no longer matches fails the release), adds the licence and notices and
`installer\*.ps1`, zips, and prints the SHA-256 (also written to the `.sha256` file; `sha256sum -c` reads it). **Do not
commit the zip.** Publish the hash next to the download; the person installing checks it with `Get-FileHash
.\Seamdesk-<version>-win-x64.zip`.

Without the SDK's nuget access (a PC that blocks outbound `dotnet.exe`), set `NUGET_PACKAGES` to a copied package
cache that holds the win-x64 runtime packs (`microsoft.netcore.app.runtime.win-x64`,
`microsoft.windowsdesktop.app.runtime.win-x64`, `microsoft.aspnetcore.app.runtime.win-x64`,
`microsoft.netcore.app.host.win-x64`).

Test before a release: `powershell -ExecutionPolicy Bypass -File tests\installer\Installer.Tests.ps1`. It installs,
upgrades, goes back and uninstalls in a scratch folder under `%TEMP%`, with fixtures standing in for MWB's autostart.

## What install.ps1 does

Per user, no admin. `powershell -ExecutionPolicy Bypass -File install.ps1` from the unzipped folder:

- copies the files to `%LOCALAPPDATA%\Programs\Seamdesk` (stops a running Seamdesk from there first, replaces the folder,
  starts it again: an upgrade is the same command);
- adds a Start menu shortcut and a run-at-sign-in value `Seamdesk` under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`;
- leaves settings, identity and keys in `%LOCALAPPDATA%\Seamdesk` alone;
- once, if it finds Glide (the project's first name): stops the Glide in `%LOCALAPPDATA%\Programs\Glide`, moves
  `%LOCALAPPDATA%\Glide` (settings, identity, MWB keys, the MWB autostart backup, `glide.log` as `seamdesk.log`) to
  `%LOCALAPPDATA%\Seamdesk` unless that already exists, and removes Glide's files, Run value and Start menu shortcut;
- turns off Mouse Without Borders' (MWB) autostart where a per-user process can, and records it in
  `%LOCALAPPDATA%\Seamdesk\mwb-autostart-backup.json`.

### What it does and does not change for MWB

| Where MWB starts from | Install |
| --- | --- |
| A Run value in `HKCU\...\Run` whose name or command mentions Mouse Without Borders | **removed**, with its data and type saved |
| A shortcut in the user's Startup folder to MWB | **moved** to `%LOCALAPPDATA%\Seamdesk\mwb-autostart-backup\`, put back byte for byte |
| PowerToys' Mouse Without Borders module (`enabled.MouseWithoutBorders` in `%LOCALAPPDATA%\Microsoft\PowerToys\settings.json`) | **turned off** (that one value, nothing else in the file) |
| Garage MWB's Windows service (`MouseWithoutBorders*`) | **left alone, needs admin**; install prints that it exists |
| A Run value in `HKLM` | **left alone, needs admin**; install prints it |

Never touched: MWB's files, its settings, its key, its running processes (Seamdesk asks before ending a running MWB). The
service and HKLM entry are why Seamdesk's first start may still show the "Mouse Without Borders is running" window after
a sign-in: stop them as administrator if you want MWB gone for good.

Going back, `Go back to Mouse Without Borders` in the tray (it runs `mwb-autostart.ps1 -Action Restore`), and
`uninstall.ps1`, put back exactly what the backup lists, skipping anything that has since been recreated by hand, then
delete the backup. The tray item also removes Seamdesk's own run-at-sign-in value, so the two don't both start at the
next sign-in, and then quits Seamdesk and starts MWB. Running install twice keeps the first backup.

Not verified against a real PowerToys or Garage install (no MWB runs on the test PC): the detection patterns are
from MWB's names and the test uses fixtures. If PowerToys is running when the module is turned off, it should notice the
file change; if it does not, the module stays on until PowerToys restarts.

## uninstall.ps1

`powershell -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\Programs\Seamdesk\uninstall.ps1"` removes the files, the
shortcut and the Run value, and restores MWB's autostart. Settings and keys are kept (`-KeepSettings` defaults to on);
`-KeepSettings:$false` asks before deleting `%LOCALAPPDATA%\Seamdesk` (`-Force` skips the question).

## Windows Firewall

A per-user process cannot add firewall rules, so neither can the installer.

- **Listening.** The first time Seamdesk listens, Windows may ask whether to allow it. Allow **Private networks only**
  (not Public). Without the rule, other machines can't connect in; Seamdesk can still dial out to them, and two Seamdesk
  machines connect as long as one side can be reached.
- **Dialling out.** Stock Windows allows all outbound connections. A PC set to block outbound by default (all three
  profiles `DefaultOutboundAction Block`, plus per-program allow rules) refuses Seamdesk until an administrator adds an
  outbound allow rule for `%LOCALAPPDATA%\Programs\Seamdesk\Seamdesk.exe`.
- **Checking it.** `Seamdesk.exe --dial <host> <port>` makes one TCP connect from that exe and exits with 0 connected,
  1 refused, 2 access denied, 3 other (a line also goes to `%LOCALAPPDATA%\Seamdesk\seamdesk.log`). *Refused* means the
  network answered, so the firewall let the packet out; *access denied* (10013) is the firewall. Use another machine's
  LAN address: dialling the PC's own address doesn't leave the PC and proves nothing.

Example, a Windows 11 PC whose firewall blocks outbound by default: the published self-contained `Seamdesk.exe`, run from
a user folder with no firewall rule, got *refused* on its own LAN IP and on 127.0.0.1 (both stay on the PC), but
*access denied* (10013) dialling the router (say `192.0.2.1`). Programs with allow rules reached the router in the
same session: the block is the PC's deny-by-default outbound policy, not something Seamdesk does.

## Troubleshooting

- **No tray icon after starting Seamdesk**: check `%LOCALAPPDATA%\Seamdesk\seamdesk.log`. Every start writes a `start: <step>`
  line per step; the last one is where it stopped. A start stuck for 20 s says so in a message and closes.
