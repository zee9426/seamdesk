# Security policy

## Supported versions

Only the latest release gets security fixes. Seamdesk is pre-1.0: a fix ships as a new release, not as a patch to an
older one.

## Reporting a vulnerability

Please report privately through GitHub: on this repository's **Security** tab, choose **Report a vulnerability**
(GitHub's private vulnerability reporting). Don't open a public issue, pull request or discussion for it.

Include what you can: the Seamdesk version (Settings > About), Windows build, what an attacker needs (same LAN, a paired
machine, a local account...), steps or a proof of concept, and what happens. Remove host names, addresses and any
Mouse Without Borders security key from logs before you attach them (`seamdesk.log` never holds a key).

What to expect:

- an acknowledgement within 7 days;
- a fix, or a status update with a plan, within 30 days;
- the fix published with a GitHub Security Advisory (and a CVE when warranted), crediting you unless you'd rather not
  be named.

## Scope

In scope, measured against the threat model in [docs/security.md](docs/security.md):

- pairing (the 8-digit code and J-PAKE) and pinning: pairing without the code, or connecting as a paired machine
  without its key;
- the TLS 1.3 channel and the frame codec: reading or injecting input, clipboard text or layout on the wire;
- the listener's source filter and anything reachable before authentication;
- input injection and the low-level hooks: input reaching this machine from anyone but the paired machine holding the
  cursor, or Seamdesk leaking keystrokes;
- storage of the identity and Mouse Without Borders keys (DPAPI, per user), and secrets in logs;
- the installer, the uninstaller and the autostart they set up.

Out of scope:

- weaknesses of Mouse Without Borders' own protocol, which Seamdesk speaks only in its opt-in "legacy security" mode (the
  shared key; see docs/security.md);
- what a machine you paired can do by design: type, click and move on this machine, and send the layout and, if you
  allow it, clipboard text;
- attacks that need local administrator rights, or code already running as your Windows account;
- denial of service by a host on your LAN (it can always drop or flood your traffic);
- antivirus or SmartScreen warnings about an unsigned build that uses keyboard and mouse hooks.
