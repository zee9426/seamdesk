# Security

What Seamdesk protects, how, and what it doesn't. The design and its tests are in [design.md](design.md) ("Security
model", "MWB compatibility mode"). To report a vulnerability, see [SECURITY.md](../SECURITY.md).

## Threat model

**Goal:** an attacker on the same network, who can see, inject and replay traffic, can't inject input into your
machines, read your keystrokes or clipboard, or pair a machine of their own.

**Trusted:** your Windows account on each machine, and every machine you paired. A paired machine is meant to type and
click on yours; that is the product.

## Between Seamdesk machines

- **Identity.** Each install makes its own key pair (ECDSA P-256) and self-signed certificate on first run. The private
  key is kept with DPAPI for your Windows account (`%LOCALAPPDATA%\Seamdesk\identity.dpapi`). There is no shared key.
- **Pairing.** One machine shows an 8-digit code: single use, valid 2 minutes, dead after 3 tries. The other machine's
  user types it. The two run J-PAKE (a password-authenticated key exchange: BouncyCastle, the NIST 3072-bit group,
  SHA-256) with the code inside TLS 1.3, and the key confirmation is bound to both certificates. A man in the middle,
  even one who knows the code, has to show his own certificates, and the confirmation fails on both sides. The code
  can't be guessed offline from captured traffic; each try is one online guess (3 of 100,000,000). On success each
  machine pins the other's public key hash (SPKI SHA-256). Pairing again under the same machine name replaces the old
  pin, so the old key stops being trusted.
- **Connection.** TLS 1.3 only, both sides present certificates, and each is checked against the pins straight after
  the handshake; an unpinned key is refused before any input frame is read. Input, layout and clipboard frames exist
  only on a connection that passed that check. Frames are length-checked (64 KiB at most) and parsed strictly.
- **Listener.** One TCP port (15200), accepting only loopback, private (RFC 1918), link-local and IPv6 unique local
  sources; anything else is closed before TLS. At most 4 connections from one address, and 16 in all, can be in their
  10-second handshake at once; more are closed unread. Every refused connection is logged with its source and reason
  (repeats counted). No discovery broadcasts: peers are dialled by the name or address stored at pairing.
- **Input.** Only the paired machine that holds the cursor can inject input, and Seamdesk's own hook ignores what Seamdesk
  injected. Seamdesk runs as your user (`asInvoker`), so Windows (UIPI) keeps it from typing into elevated windows, the
  UAC prompt or the lock screen.
- **Revoking.** Unpair (or remove) a machine: its pin is forgotten, its link is closed, and its next connection is
  refused.

## Mouse Without Borders compatibility ("legacy security")

Off by default, per machine. When you turn it on for a machine, Seamdesk speaks Mouse Without Borders' protocol to it,
protected only by MWB's single shared security key: AES-256-CBC with keys derived from that key by PBKDF2 (the
pre-0.101 format uses a fixed salt and IV; PowerToys 0.101 and later use a per-stream salt and IV). Anyone who has
the key, or brute-forces a weak one offline from captured traffic, can read and inject input on that link. That is no
weaker than MWB itself, and the settings window says so. Seamdesk narrows it: it dials only the address you configured,
accepts a connection only from that address (and a private one), takes that machine's input only if you turned on
"Let ... control this PC", takes its clipboard only if you chose both ways, keeps the key with DPAPI, and never logs it.

## What Seamdesk doesn't protect against

- **Your own Windows account.** Code running as you can read the identity and MWB keys (DPAPI is per account), read
  `settings.json`, and use the keyboard hook like any other program.
- **A machine you paired.** It can move, click and type on your machine whenever it hands itself the cursor, change the
  shared layout, and put text on your clipboard if you chose both ways for it. Unpair a machine you no longer trust.
- **Legacy security links** (above).
- **Denial of service.** Anyone on your network can drop or flood your traffic. The handshake limits above stop one
  host from holding every slot, not many hosts (or one with many addresses) from filling them.

## Known limits

- **The source filter is not authentication.** A router that forwards a port and rewrites the source (NAT hairpin), or
  a VPN that puts its clients behind a LAN address, makes outside traffic look local. The pins are what keep such a
  host out; don't forward Seamdesk's port.
- **Some details are visible before authentication.** An unpaired host that connects learns Seamdesk's version (in the
  refusal), and while a pairing code is shown, the machine's name.
- **Not yet run end to end on real hardware:** two Seamdesk PCs, lock and UAC switching, sleep and resume, and a real MWB
  peer are proven by unit tests and by MWB's own code (design.md, "Open questions"). Three or more machines are
  untested.
- **Unsigned releases.** SmartScreen warns, and antivirus heuristics may flag a program with a global keyboard hook and
  input injection. Check the SHA-256 published with each release before you run it.
- **Logs.** `%LOCALAPPDATA%\Seamdesk\seamdesk.log` holds machine names, addresses and link events; never keys, keystrokes or
  clipboard text. Remove names and addresses before sharing it.
- **Dependencies.** The release bundles the .NET runtime and BouncyCastle, pinned by `packages.lock.json`; a .NET
  security update needs a new Seamdesk release.
