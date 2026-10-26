# Round-trip tests of installer\install.ps1, uninstall.ps1 and mwb-autostart.ps1 (Windows PowerShell 5.1, any Windows PC):
#   powershell -ExecutionPolicy Bypass -File tests\installer\Installer.Tests.ps1
# Everything happens in a scratch folder under %TEMP% and under HKCU:\Software\SeamdeskTest. Installs use -RunName SeamdeskTest and
# the MWB lookups (-Mwb) and the old Glide install (-OldRoot, -OldRunName, -OldDataDir) point at fixtures there, so this PC's
# real Seamdesk, Glide, Run key and Mouse Without Borders are never changed.
# The "Seamdesk.exe" and "Glide.exe" are copies of ping.exe, which lives long enough to be stopped by an upgrade.
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$tmp = Join-Path $env:TEMP "seamdesk-installer-test-$PID"
$fix = 'HKCU:\Software\SeamdeskTest'
$realRun = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$failed = 0
function Check($ok, $what) { if ($ok) { Write-Host "ok    $what" } else { Write-Host "FAIL  $what"; $script:failed++ } }
function RunValues { $k = Get-Item $realRun; ($k.GetValueNames() | Where-Object { $_ -notin 'SeamdeskTest', 'SeamdeskTestGlide' } | Sort-Object | ForEach-Object { "$_=$($k.GetValue($_, $null, 'DoNotExpandEnvironmentNames'))" }) -join '|' }
function Hash($p) { (Get-FileHash $p).Hash }

$realBefore = RunValues
try {
    Remove-Item $fix -Recurse -ErrorAction SilentlyContinue
    New-Item -Path "$fix\MwbRun", "$fix\MwbMachineRun" -Force | Out-Null
    $mwbExe = '%ProgramFiles%\Microsoft Garage\Mouse without Borders\MouseWithoutBorders.exe'
    New-ItemProperty "$fix\MwbRun" -Name 'Mouse without Borders' -Value "`"$mwbExe`"" -PropertyType ExpandString | Out-Null
    New-ItemProperty "$fix\MwbRun" -Name 'Other app' -Value 'C:\other.exe' -PropertyType String | Out-Null
    New-ItemProperty "$fix\MwbMachineRun" -Name 'MouseWithoutBorders' -Value 'C:\machine.exe' -PropertyType String | Out-Null

    $startup = Join-Path $tmp 'startup'; $menu = Join-Path $tmp 'menu'; $data = Join-Path $tmp 'data'; $root = Join-Path $tmp 'Programs\Seamdesk'
    New-Item -ItemType Directory -Force $startup, $menu | Out-Null
    $sc = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $startup 'Mouse without Borders.lnk'))
    $sc.TargetPath = 'C:\Program Files\Microsoft Garage\Mouse without Borders\MouseWithoutBorders.exe'; $sc.Save()
    $scHash = Hash (Join-Path $startup 'Mouse without Borders.lnk')
    $pt = Join-Path $tmp 'PowerToys-settings.json'
    $ptText = "{`r`n  `"enabled`": {`r`n    `"FancyZones`": true,`r`n    `"MouseWithoutBorders`": true`r`n  }`r`n}"
    [IO.File]::WriteAllText($pt, $ptText)
    $mwb = @{ RunKey = "$fix\MwbRun"; MachineRunKey = "$fix\MwbMachineRun"; StartupDir = $startup; PowerToysSettings = $pt; ServiceFilter = 'SeamdeskTestNoSuchService*' }
    $common = @{ Root = $root; RunName = 'SeamdeskTest'; DataDir = $data; StartMenuDir = $menu; RunKey = $realRun; Mwb = $mwb }
    # The project's first name: an install of Glide, which install.ps1 moves over. Every install below gets these, never the
    # defaults (the real %LOCALAPPDATA%\Glide).
    $oldRoot = Join-Path $tmp 'Programs\Glide'; $oldData = Join-Path $tmp 'GlideData'
    $old = @{ OldRoot = $oldRoot; OldRunName = 'SeamdeskTestGlide'; OldDataDir = $oldData }
    function Install { & "$pkg\install.ps1" @common @old -NoStart @args | Out-Host }

    # A release folder: the three scripts and app\Seamdesk.exe, as in the zip.
    $pkg = Join-Path $tmp 'pkg'
    New-Item -ItemType Directory -Force "$pkg\app" | Out-Null
    Copy-Item "$repo\installer\*.ps1" $pkg
    Copy-Item "$env:SystemRoot\System32\PING.EXE" "$pkg\app\Seamdesk.exe"
    Set-Content "$pkg\app\v1.txt" 'v1'

    # A running Glide with its settings, identity, MWB key and log, Run value and Start menu shortcut.
    New-Item -ItemType Directory -Force $oldRoot, $oldData | Out-Null
    Copy-Item "$env:SystemRoot\System32\PING.EXE" "$oldRoot\Glide.exe"
    [IO.File]::WriteAllText("$oldData\settings.json", '{"keep":"me"}')
    Set-Content "$oldData\identity.dpapi" 'id'; Set-Content "$oldData\mwb-LAPTOP.key" 'key'
    Set-Content "$oldData\glide.log" 'log'; Set-Content "$oldData\glide.old.log" 'older'
    New-ItemProperty $realRun -Name SeamdeskTestGlide -Value "`"$oldRoot\Glide.exe`"" -PropertyType String -Force | Out-Null
    $sc = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $menu 'Glide.lnk')); $sc.TargetPath = "$oldRoot\Glide.exe"; $sc.Save()
    $glide = Start-Process "$oldRoot\Glide.exe" -ArgumentList '-n 120 127.0.0.1' -PassThru -WindowStyle Hidden

    # --- install over Glide
    Install
    Check ($glide.WaitForExit(5000)) 'migrate: the running Glide was stopped'
    Check (((Get-Content -ErrorAction SilentlyContinue "$data\settings.json" -Raw) -eq '{"keep":"me"}') -and (Test-Path "$data\identity.dpapi") -and (Test-Path "$data\mwb-LAPTOP.key")) "migrate: Glide's settings, identity and MWB keys are in the new data folder"
    Check (((Get-Content -ErrorAction SilentlyContinue "$data\seamdesk.log") -eq 'log') -and ((Get-Content -ErrorAction SilentlyContinue "$data\seamdesk.old.log") -eq 'older')) "migrate: Glide's logs carry on as seamdesk.log"
    Check (-not (Test-Path $oldData) -and -not (Test-Path $oldRoot)) "migrate: Glide's data folder moved, its program folder removed"
    Check ($null -eq (Get-ItemProperty $realRun).SeamdeskTestGlide -and -not (Test-Path "$menu\Glide.lnk")) "migrate: Glide's Run value and Start menu shortcut removed"
    Check ((Test-Path "$root\Seamdesk.exe") -and (Test-Path "$root\v1.txt") -and (Test-Path "$root\uninstall.ps1") -and (Test-Path "$root\mwb-autostart.ps1")) 'install: app and scripts are in the install root'
    Check ((Get-ItemProperty $realRun).SeamdeskTest -eq "`"$root\Seamdesk.exe`"") 'install: run-at-sign-in value points at the installed exe'
    Check (Test-Path "$menu\Seamdesk.lnk") 'install: Start menu shortcut'
    $k = Get-Item "$fix\MwbRun"
    Check ($k.GetValueNames() -notcontains 'Mouse without Borders' -and $k.GetValueNames() -contains 'Other app') "install: MWB's Run value is gone, an unrelated one stays"
    Check (-not (Test-Path (Join-Path $startup 'Mouse without Borders.lnk'))) 'install: MWB Startup shortcut moved out'
    Check ((Get-Content $pt -Raw) -match '"MouseWithoutBorders": false' -and (Get-Content $pt -Raw) -match '"FancyZones": true') 'install: only the PowerToys MWB switch turned off'
    Check ((Get-Item "$fix\MwbMachineRun").GetValue('MouseWithoutBorders') -eq 'C:\machine.exe') 'install: the machine-wide Run value (needs admin) is left alone'
    $backup = Get-Content "$data\mwb-autostart-backup.json" -Raw | ConvertFrom-Json
    Check (@($backup.changes).Count -eq 3) 'install: backup lists exactly the 3 per-user changes'

    # --- upgrade: a running Seamdesk from the root is stopped, files replaced, settings and backup kept
    $proc = Start-Process "$root\Seamdesk.exe" -ArgumentList '-n 120 127.0.0.1' -PassThru -WindowStyle Hidden
    Remove-Item "$pkg\app\v1.txt"; Set-Content "$pkg\app\v2.txt" 'v2'
    $backupBefore = Hash "$data\mwb-autostart-backup.json"
    Install
    Check ($proc.WaitForExit(5000)) 'upgrade: the running Seamdesk was stopped'
    Check ((Test-Path "$root\v2.txt") -and -not (Test-Path "$root\v1.txt")) 'upgrade: files replaced, stale file gone'
    Check ((Get-Content "$data\settings.json" -Raw) -eq '{"keep":"me"}') 'upgrade: settings kept'
    Check ((Hash "$data\mwb-autostart-backup.json") -eq $backupBefore) 'upgrade: the MWB backup is not overwritten by a second run'
    New-Item -ItemType Directory -Force $oldData | Out-Null; Set-Content "$oldData\settings.json" 'older'
    Install
    Check (((Get-Content "$oldData\settings.json") -eq 'older') -and ((Get-Content "$data\settings.json" -Raw) -eq '{"keep":"me"}')) 'migrate: a Glide data folder next to an existing Seamdesk one is left alone'
    Remove-Item $oldData -Recurse

    # --- a file in the root stays locked for a moment after Seamdesk stops (the 0.7.0 -> 0.7.2 upgrade failed on Accessibility.dll):
    # the upgrade waits it out; if the lock outlasts the retry window it says which file and that re-running is safe.
    $holder = Join-Path $tmp 'hold.ps1'
    Set-Content $holder 'param($f, $secs) $h = [IO.File]::Open($f, "Open", "Read", "Read"); Set-Content "$f.held" 1; Start-Sleep $secs; $h.Dispose()'
    function HoldFile($f, $secs) {
        Remove-Item "$f.held" -ErrorAction SilentlyContinue
        $p = Start-Process powershell -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$holder`"", "`"$f`"", $secs -PassThru -WindowStyle Hidden
        while (-not (Test-Path "$f.held")) { Start-Sleep -Milliseconds 100 }
        $p
    }
    $locked = "$root\Accessibility.dll"
    Set-Content $locked 'dll'
    $p = HoldFile $locked 3
    Install
    Check ((Test-Path "$root\v2.txt") -and -not (Test-Path $locked)) 'upgrade: a file locked for 3 s is removed once it is free'
    $p.WaitForExit()
    Remove-Item "$locked.held" -ErrorAction SilentlyContinue
    Set-Content $locked 'dll'
    $p = HoldFile $locked 8
    $msg = ''
    try { Install -RetrySeconds 2 } catch { $msg = $_.Exception.Message }
    Check (($msg -like '*Accessibility.dll*') -and ($msg -like '*again*safe*') -and ($msg -like '*some are deleted*')) 'upgrade: a lock that outlasts the retries names the file and says re-running is safe'
    $p.WaitForExit()
    Remove-Item "$locked.held" -ErrorAction SilentlyContinue
    Install
    Check ((Test-Path "$root\v2.txt") -and -not (Test-Path $locked)) 'upgrade: running it again after that succeeds'

    # --- go back (what the tray runs): MWB autostart restored exactly, Seamdesk's own Run value removed, Seamdesk files stay
    & "$root\mwb-autostart.ps1" -Action Restore -BackupPath "$data\mwb-autostart-backup.json" -Mwb $mwb -RemoveSeamdeskRun SeamdeskTest -SeamdeskRunKey $realRun | Out-Host
    $k = Get-Item "$fix\MwbRun"
    Check (($k.GetValue('Mouse without Borders', $null, 'DoNotExpandEnvironmentNames') -eq "`"$mwbExe`"") -and ($k.GetValueKind('Mouse without Borders') -eq 'ExpandString')) 'go back: Run value restored with its data and type'
    Check ((Hash (Join-Path $startup 'Mouse without Borders.lnk')) -eq $scHash) 'go back: Startup shortcut back, byte for byte'
    Check ([IO.File]::ReadAllText($pt) -eq $ptText) 'go back: PowerToys settings file identical to the original'
    Check (-not (Test-Path "$data\mwb-autostart-backup.json")) 'go back: backup consumed'
    Check ($null -eq (Get-ItemProperty $realRun).SeamdeskTest) "go back: Seamdesk's Run value removed"

    # --- uninstall (default: keep settings), then with -KeepSettings:$false -Force
    Install
    & "$root\uninstall.ps1" @common | Out-Host
    Check (-not (Test-Path $root) -and -not (Test-Path "$menu\Seamdesk.lnk") -and $null -eq (Get-ItemProperty $realRun).SeamdeskTest) 'uninstall: files, shortcut and Run value removed'
    Check ((Get-Item "$fix\MwbRun").GetValueNames() -contains 'Mouse without Borders' -and (Test-Path (Join-Path $startup 'Mouse without Borders.lnk')) -and ([IO.File]::ReadAllText($pt) -eq $ptText)) 'uninstall: MWB autostart restored from the backup'
    Check (Test-Path "$data\settings.json") 'uninstall: settings kept by default'
    Install
    & "$root\uninstall.ps1" @common -KeepSettings:$false -Force | Out-Host
    Check (-not (Test-Path $data)) 'uninstall -KeepSettings:$false -Force: settings deleted'
    Check ((RunValues) -eq $realBefore) "this PC's real Run key is exactly as before"
}
finally {
    Get-Process Seamdesk, Glide -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$tmp*" } | Stop-Process -Force
    Remove-ItemProperty $realRun -Name SeamdeskTest, SeamdeskTestGlide -ErrorAction SilentlyContinue
    Remove-Item $fix -Recurse -ErrorAction SilentlyContinue
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
if ($failed) { Write-Host "$failed FAILED"; exit 1 }
Write-Host 'all passed'
