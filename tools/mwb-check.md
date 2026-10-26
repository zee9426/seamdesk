# Check: Seamdesk with a laptop that runs Mouse Without Borders

Both directions: the PC driving the laptop (part 3), then the laptop driving the PC (part 4). About 5 minutes of
trying, plus 10 minutes of setup the first time. The laptop is not changed: its Mouse Without Borders
(MWB) keeps running as it is. Only the PC switches from MWB to Seamdesk, and back at the end.

You need: the PC's own desktop (not ssh), a build (`dotnet build Seamdesk.slnx`, as for `tools/smoke.ps1`), and the laptop
next to you. Keep the laptop on **one screen** for this check (lid open with no monitor, or lid closed on one monitor).

## 1. Note three things on the laptop (share none of them)

1. Its **computer name** (Settings > System > About, "Device name").
2. Its screen's **resolution** (Settings > System > Display), for example 2560 x 1440.
3. In MWB's settings on the laptop: the **security key** (you will type it into Seamdesk; never send it to anyone), and in
   the machine layout, **which side of the laptop the PC is on** (left, right, above or below). The laptop's MWB uses
   that side to hand the cursor back, and to cross to the PC. Also note its version (MWB's About: standalone MWB, or
   PowerToys and its version) and whether **Move mouse relatively** is on.

## 2. Set up Seamdesk on the PC

1. Start Seamdesk: in PowerShell from the repo, with the same `dotnet` as `smoke.ps1`:
   `dotnet src\Seamdesk.App\bin\Debug\net10.0-windows\Seamdesk.dll`
   Mouse Without Borders is still running on the PC, so Seamdesk says so: choose **Quit Mouse Without Borders and start
   Seamdesk**. (If PowerToys is installed, make sure its Mouse Without Borders module is off.)
2. Click Seamdesk's tray icon to open its settings. On **Machines**, next to "A machine running Mouse Without Borders",
   choose **Add**: type a name for the laptop (say `laptop`), its computer name (or IP address), and the **security
   key**. Seamdesk keeps the key encrypted for your Windows account and never shows it again. If Windows Firewall asks
   about Seamdesk, allow **private networks** only.
3. On the laptop's card, leave **Mouse Without Borders version** on Automatic, and set **Copy and paste** to "This PC
   to laptop". Leave **Let laptop control this PC** off for now.
4. On **Layout**, pick the laptop. Under "laptop's screens", set Screen 1's size to the laptop's resolution. Then drag
   the laptop on the desk to where it sits, on the side the laptop's MWB expects the PC (for example, under the PC
   monitor whose bottom edge leads to it). "Where the cursor crosses" should list that edge. Choose **Save**.
5. Within a few seconds the laptop's card on **Machines** should say **Connected**, and the version row should say
   which version it found ("In use: before 0.101" for standalone MWB or PowerToys up to 0.100, "In use: 0.101 or later"
   otherwise). Check that it matches what you noted. If it says the key doesn't match, choose **Change key** and type it
   again. If it doesn't connect, read `seamdesk.log` (below) before changing anything else.

### Reading seamdesk.log

`%LOCALAPPDATA%\Seamdesk\seamdesk.log` gets one line per step of each MWB link, each starting `mwb laptop:` (the name you gave
it). It never holds the key, decrypted packages, what you type or the clipboard. A line that repeats before the link
comes up is written the 1st, 10th, 100th and 1000th time, then with `(N times)`.

Seamdesk dialling the laptop, in order:

| Line | Means |
|---|---|
| `dialled 192.0.2.10:15101: connected` | TCP reached the laptop's MWB. |
| `dialled 192.0.2.10:15101: TimedOut (...)`, `ConnectionRefused (...)`, `AccessDenied (...)` | TCP didn't get there: the laptop is off or asleep, its MWB isn't running, or a firewall blocks it (`AccessDenied`: this PC's own outbound rule). |
| `handshake: read the 80-byte stream start; it opens as the old format, standalone MWB 2.x key` | The laptop runs standalone MWB and the key matches. `PowerToys key (0.100 or earlier)` and `the current format (PowerToys 0.101 or later)` are the other two good outcomes. |
| `handshake: read the 80-byte stream start; it opens as nothing known: ...` | The key differs (or an MWB Seamdesk doesn't know). Type the key again; check the laptop's MWB uses the same one. |
| `refused: <reason>` | Seamdesk closed the link; the reason is the one on the laptop's card (key mismatch, a version you excluded on **Machines**, Auto refusing to fall back). |
| `refused: X closed the connection after N of the 80 bytes that start its stream` | The far end closed before its stream start: MWB writes that before reading anything, so this is not MWB on port 15101, or MWB restarting. |
| `refused: no complete handshake from X in time (N bytes read)` | The laptop accepted but didn't finish in 10 s. |
| `handshake: trusted after reading N bytes; it calls itself NAME, id N; answered 10 of its handshakes` | Connected. `NAME` is the laptop's computer name as its MWB sends it. |
| `closed: <reason>` | The link ended: `closed by Seamdesk` (Seamdesk replaced or stopped it), `X said goodbye` or `X closed the connection` (the laptop), or a socket error. |

The laptop's MWB also dials this PC:

| Line | Means |
|---|---|
| `refused a connection from 192.0.2.10: incoming control is off for this machine ("Let this machine control Seamdesk")` | Expected while control is off. The laptop's MWB shows "Unable to read data from the transport connection" and dials again; Seamdesk's own link to it is not affected. |
| `mwb: refused a connection from ...: not the address of any Mouse Without Borders machine in Seamdesk's settings` | A machine Seamdesk doesn't know, or the laptop at an address its name no longer resolves to. |
| `accepted a connection from 192.0.2.10`, then the handshake lines, then `the link it dialled is in use` (or `dropped: Seamdesk's own link to it is kept`) | Control is on and the laptop's link came up. Only one link per machine is kept. |

## 3. The PC drives the laptop (2 minutes)

Write down pass or fail, with a word on anything odd:

- a. Push the cursor off the PC monitor's edge towards the laptop: it appears on the laptop, about where it left.
- b. Move around the laptop: the speed feels like the PC's own; reach all four corners.
- c. On the laptop: click, right-click, scroll, and type in Notepad (letters, arrows, Shift, Ctrl+A).
- d. Copy some text on the PC, cross to the laptop, paste: the PC's text appears.
- e. Copy different text on the laptop, come back to the PC, paste: the **PC's** old text must appear, not the laptop's.
- f. Move off the laptop's edge towards the PC: the cursor comes back to the PC, at the matching spot.
- g. Go to the laptop again and press **Ctrl+Alt+Home** (or **Ctrl+Alt+H**): the cursor comes back to the PC; the laptop's cursor hides.

## 4. The laptop drives the PC (2 minutes)

On the laptop's card, turn **Let laptop control this PC** on. Within a few seconds it should say **Connected** again.

- h. On the **laptop's** own touchpad or mouse, move off the edge towards the PC: the cursor appears on the PC.
- i. Move around the PC's monitors, click, scroll, and type in Notepad on the PC with the laptop's keyboard.
- j. Move back towards the laptop. With **Move mouse relatively** on (on the laptop), the cursor goes back where the PC's
  monitor meets the laptop on Seamdesk's layout. With it off, the laptop's MWB decides when and where: note what happens.
- k. Copy text on the laptop, cross to the PC, paste: with "This PC to laptop" the **PC's** text must appear, not the
  laptop's. Then set **Copy and paste** to "Both ways", copy new text on the laptop, cross, paste: now the laptop's
  text appears.
- l. While the laptop drives the PC, press **Ctrl+Alt+Home** (or **Ctrl+Alt+H**) on the **PC's** keyboard: the laptop gets its cursor back.
- m. Turn **Let laptop control this PC** off and try h again: the cursor must stay on the laptop.

## 5. Put MWB back on the PC

1. From Seamdesk's tray menu choose **Go back to Mouse Without Borders**: Seamdesk quits and starts Mouse Without Borders.
   (If the item is missing, Seamdesk didn't find it installed: quit Seamdesk and start MWB from the Start menu.)
2. Check that the PC drives the laptop through MWB as before.
3. Optional, before step 1: on **Machines**, turn the laptop's **Legacy security** off, so Seamdesk won't connect to it if
   started again.

Nothing on the laptop changes in any of this: its MWB keeps running, and goes on working with the PC's MWB.

## 6. Report (for example in an MWB compatibility issue)

- The pass/fail list (a to m) and the laptop's status and version row on the **Machines** page.
- `%LOCALAPPDATA%\Seamdesk\seamdesk.log` (it holds no key).
- The laptop's MWB version (MWB's About), whether its "Move mouse relatively" option is on, and which side of the laptop
  the PC is in MWB's layout.
- If the laptop updates PowerToys past 0.100 later, the version row changes to "0.101 or later" by itself; Seamdesk never
  goes back to the older format on its own (it says so on the card instead).
- Whether a firewall prompt appeared, and what you chose.

Never share the security key or the `mwb-laptop.key` file.
