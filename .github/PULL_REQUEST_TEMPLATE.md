## What and why

<!-- One change per pull request. What does it do, and why? Link the issue if there is one. -->

## Tests

<!-- For each new or changed test: what it protects, which regression makes it fail, and why nothing else already
catches that (and its row in docs/design.md). For a bug fix: did the test fail on the old code? -->

## Checked

- [ ] `dotnet test Seamdesk.slnx`
- [ ] Windows checks, if this touches what they cover (`tools/smoke.ps1`, `tools/mwb-check.md`, `tests/installer/Installer.Tests.ps1`, `tools/Shots`)
- [ ] `docs/design.md` and `CHANGELOG.md` updated where needed
- [ ] No host names, addresses, user names or keys in code, tests, logs or screenshots
