# Interactive smoke test of Seamdesk's Windows host. Takes about 3 s; a watchdog stops it at 30 s.
#
# What it does, on this PC's real monitors, with a fake peer below the monitor whose bottom edge is highest:
#   1. pushes the cursor down off that edge with relative moves: it must cross to the peer at the same x, park at
#      the primary's centre and be hidden; reports whether the hook's point is clipped at the edge;
#   2. moves while parked: the deltas forwarded must equal the hook's deltas, and the real cursor must not move;
#   3. hands the cursor back: it must reappear, visible, at the hand-back point;
#   4. drives this PC as if from the peer: absolute moves to every monitor's corners and centre, and one past the
#      top-left, must land on exactly the pixel Seamdesk.Core computes; a push past the bottom edge hands it to the peer;
#   5. reports how far a relative +20 px SendInput moves the cursor;
# then puts the cursor back. During step 2 (well under a second) the real mouse and keyboard are swallowed.
#
# Needs: the PC's own desktop (not ssh), Mouse Without Borders quit, hands off the mouse, and a build:
#   dotnet build Seamdesk.slnx
# Uses $env:DOTNET_ROOT, else dotnet on PATH.
$dll = Join-Path $PSScriptRoot 'Smoke\bin\Debug\net10.0-windows\Smoke.dll'
if (-not (Test-Path $dll)) { Write-Host "Build first: dotnet build Seamdesk.slnx"; exit 2 }
$dotnet = if ($env:DOTNET_ROOT -and (Test-Path "$env:DOTNET_ROOT\dotnet.exe")) { "$env:DOTNET_ROOT\dotnet.exe" } else { 'dotnet' }
$p = Start-Process -FilePath $dotnet -ArgumentList "`"$dll`" $args" -NoNewWindow -PassThru
$null = $p.Handle # without it ExitCode stays empty
if (-not $p.WaitForExit(35000)) { $p.Kill(); Write-Host "FAIL  smoke hung; killed (hooks die with the process)"; exit 3 }
exit $p.ExitCode
