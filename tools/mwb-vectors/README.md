# mwb-vectors

Checks Seamdesk's MWB link (`src/Seamdesk.Mwb`) against Mouse Without Borders' own code, and writes the test vectors in
`tests/Seamdesk.Mwb.Tests/vectors.json`. It is not in `Seamdesk.slnx` and nothing from MWB is committed: `extract.sh` copies
MWB's code out of a PowerToys checkout each time, into `mwb/` (git-ignored); those files stay under PowerToys' MIT
licence (Copyright (c) Microsoft Corporation, see `THIRD-PARTY-NOTICES.txt`).

```sh
for m in old current; do tools/mwb-vectors/extract.sh <powertoys checkout> $m; done
dotnet build tools/mwb-vectors -p:Mwb=old
dotnet tools/mwb-vectors/bin/Debug/net10.0/MwbVectors.dll tests/Seamdesk.Mwb.Tests/vectors.json
dotnet build tools/mwb-vectors -p:Mwb=current
dotnet tools/mwb-vectors/bin/Debug/net10.0/MwbVectors.dll tests/Seamdesk.Mwb.Tests/vectors-current.json
```

Each run prints `ok: wrote ...`, or `FAILED:` with the check that failed (and writes nothing).

## Which MWB

- **old** (`vectors.json`): the pre-0.101 wire format, PowerToys commit `a9e1f0f20^`, the parent of "Use per-connection
  random salt and IV" (#48742). The encryption, key derivation, package layout, handshake and clipboard code there are
  the same as in the first PowerToys import of MWB (`29eebe16a`, 2023), apart from API renames
  (`git log 29eebe16a~1..a9e1f0f20^`).
- **current** (`vectors-current.json`): PowerToys `48c4bad07` (October 2026). From `a9e1f0f20` to there, the only change
  to the code extracted here is `Core/Encryption.cs`: `a9e1f0f20` (a random 16-byte salt and 16-byte IV sent in the
  clear at the start of each stream) and `a64c400b7` (PBKDF2 from 50000 to 100000 iterations). Both first shipped in
  `v0.101.2211.0`, so every 0.101 release speaks this format. `5c9fddcdd` changed how the key is stored, not the key the
  wire uses (`Class/Setting.cs:612-626`).
## What is MWB's own code here, and what is not

Compiled unchanged (whole files): `Core/Encryption.cs`, `DATA.cs`, `Package.cs`, `PackageType.cs`, `MOUSEDATA.cs`,
`KEYBDDATA.cs`, `ID.cs`, `ClipboardPostAction.cs`, `PackageMonitor.cs`, `MyRectangle.cs`, `Exceptions/*Exception.cs`;
for current also `Class/PairingKeyCodec.cs` and `IPairingKeyCodec.cs`, which its `Encryption.IsKeyValid` calls.
Compiled unchanged (single members, cut out by `extract.sh`): `TcpSk` (the encrypted streams and their random first
block), `SocketStuff.TcpSendData`, `TcpReceiveData`, `ProcessReceivedDataEx`, `TcpSend`; `Common.SendPackage`,
`SendHeartBeat`, `SendNextMachine`, `SendHello`, `SendByeBye`, `SendOrReceiveARandomDataBlockPerInitialIV`, `GetBytes*`;
`MachineStuff.ConvertToUniversalValue`; `Clipboard.SendClipboardDataUsingTCP`, `ReceiveClipboardDataUsingTCP`,
`SetClipboardData`.

Not MWB's code:
- `Stubs.cs`: logging, tooltips, settings and clipboard/image classes stubbed out, and `Common.SkSend` reduced to
  numbering the package (as MWB's does) and sending it on the one test socket.
- The handshake in `Program.cs` (`MwbHandshake`, `Answer`) is ported line for line from `MainTCPRoutine`
  (`Class/SocketStuff.cs:1269-1295`, `:1371-1385`, `:1409-1442`, the same file at both commits), which is too entangled
  with MWB's UI and socket list to compile alone. Its sends and receives are MWB's own `TcpSend` and `TcpReceiveData`.
- The controller's packages (`MwbMouse`, `MwbKey`, `MwbHide`) are built field by field as `Core/Event.cs:97-116`,
  `:263-266` and `:214` build them (in MWB they are filled from its low-level hooks); they are sent by MWB's own
  `SkSend` and `TcpSend`. The coordinates are chosen by the harness, as a controller in either of MWB's modes sends
  them: absolute 0..65535, or relative offset by 100000.
- MWB's receiver (`Core/Receiver.cs`), its hooks and its input injection are Windows-only and not run. What MWB does
  with each decoded package (a relative move, the edge test, `NextMachine`, `HideMouse`) is read from the code and cited
  in `docs/design.md`.

## What it checks

For each format, over loopback, through a relay that records both directions:
1. The handshake both ways: Seamdesk (on Auto) finds the format from MWB's stream start, MWB trusts Seamdesk's answer, Seamdesk
   trusts MWB's, and each learns the other's id and name.
2. Seamdesk drives MWB, each package decoded by MWB's `TcpReceiveData`: the first heartbeat; the arrival as an absolute
   move; relative moves (±100000); every button down and up (X buttons with XBUTTON1/2); vertical and horizontal wheel;
   keys with and without the extended flag; text through MWB's own clipboard decoder; `HideMouse` on hand-back and on
   snap-back.
3. MWB to Seamdesk, from MWB's own senders: `Hello` (Seamdesk answers with a heartbeat), a heartbeat, a `ClipboardAsk` (Seamdesk
   sends nothing back), its clipboard text and RTF from `SendClipboardDataUsingTCP` with the buffer's spare zeros (Seamdesk,
   set to take it, surfaces the text only), `NextMachine` for leaving its top edge (Seamdesk hands the cursor to the PC at
   the pixel its desk gives, and hides the peer's cursor), and `ByeBye` (Seamdesk closes the link).
4. MWB drives Seamdesk: 18 controller packages (the arrival, relative and absolute moves, every button, both wheels, an
   absolute click, keys) each become the frames written in the harness; Seamdesk's hand-back `NextMachine`, decoded by MWB,
   names MWB as the next machine at the pixel Seamdesk's desk chose; MWB's `HideMouse` is ignored after the hand-back and
   ends control after a second arrival. Seamdesk surfaces nothing else.

The vectors hold the key, MWB's AES key (old: and IV; current: per stream, from each stream's salt), the magic number,
both captured streams with MWB's own decryption of each, the packages MWB decoded for each input frame, the
controller packages with the frames they became, and the hand-back. The test key is made up.
