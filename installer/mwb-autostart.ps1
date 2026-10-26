# Turns Mouse Without Borders' (MWB) autostart off for this user, or puts it back, and records exactly what it changed.
#   -Action Disable   find what a per-user process can switch off, switch it off, write the backup
#   -Action Restore   undo exactly what the backup says, then delete the backup
# Used by install.ps1, uninstall.ps1 and the tray's "Go back to Mouse Without Borders". Windows PowerShell 5.1 compatible.
#
# What it can and cannot touch (docs/release.md):
#   changes (per user)    a Run value in HKCU, a shortcut in the user's Startup folder, PowerToys' "Mouse Without Borders"
#                         module switch in %LOCALAPPDATA%\Microsoft\PowerToys\settings.json
#   reports, leaves alone MWB's Windows service, a Run value in HKLM (both need admin)
#   never touches         MWB's files, its settings, its security key, its processes
# -Mwb overrides where it looks (RunKey, MachineRunKey, StartupDir, PowerToysSettings, ServiceFilter); the tests point it
# at fixtures so a test run never touches this PC's real MWB.
param(
    [Parameter(Mandatory)][ValidateSet('Disable', 'Restore')][string]$Action,
    [string]$BackupPath = (Join-Path $env:LOCALAPPDATA 'Seamdesk\mwb-autostart-backup.json'),
    [hashtable]$Mwb = @{},
    # Restore only: also remove Seamdesk's own run-at-sign-in value, so Seamdesk and MWB don't both start at the next sign-in.
    [string]$RemoveSeamdeskRun,
    [string]$SeamdeskRunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
)
$ErrorActionPreference = 'Stop'

$at = @{
    RunKey            = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    MachineRunKey     = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run'
    StartupDir        = [Environment]::GetFolderPath('Startup')
    PowerToysSettings = Join-Path $env:LOCALAPPDATA 'Microsoft\PowerToys\settings.json'
    ServiceFilter     = 'MouseWithoutBorders*'
}
foreach ($k in $Mwb.Keys) { $at[$k] = $Mwb[$k] }

$mwbPattern = 'mouse\s*without\s*borders|MouseWithoutBorders'
$stash = Join-Path (Split-Path $BackupPath) 'mwb-autostart-backup'
$ptEnabled = '("MouseWithoutBorders"\s*:\s*)(true|false)'

function Read-Backup {
    if (Test-Path $BackupPath) { @((Get-Content $BackupPath -Raw | ConvertFrom-Json).changes) } else { @() }
}

function Write-Backup($changes) {
    New-Item -ItemType Directory -Force (Split-Path $BackupPath) | Out-Null
    $doc = [ordered]@{ version = 1; savedUtc = (Get-Date).ToUniversalTime().ToString('o'); changes = @($changes) }
    [IO.File]::WriteAllText($BackupPath, ($doc | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding $false))
}

function Get-RunValues($key) {
    if (-not (Test-Path $key)) { return }
    $item = Get-Item $key
    foreach ($name in $item.GetValueNames()) {
        $data = $item.GetValue($name, $null, 'DoNotExpandEnvironmentNames')
        if ("$name $data" -match $mwbPattern) {
            [pscustomobject]@{ Name = $name; Data = [string]$data; Kind = [string]$item.GetValueKind($name) }
        }
    }
}

function Get-StartupShortcuts {
    if (-not (Test-Path $at.StartupDir)) { return }
    $shell = New-Object -ComObject WScript.Shell
    foreach ($f in Get-ChildItem $at.StartupDir -Filter *.lnk) {
        if ("$($f.Name) $($shell.CreateShortcut($f.FullName).TargetPath)" -match $mwbPattern) { $f }
    }
}

if ($Action -eq 'Disable') {
    $changes = @(Read-Backup) # an earlier run's record stays: the thing it switched off may still be off
    $done = @($changes | ForEach-Object { "$($_.type)|$($_.key)|$($_.name)" })

    foreach ($v in @(Get-RunValues $at.RunKey)) {
        if ($done -contains "run|$($at.RunKey)|$($v.Name)") { continue }
        $changes += [pscustomobject]@{ type = 'run'; key = $at.RunKey; name = $v.Name; data = $v.Data; kind = $v.Kind }
        Write-Backup $changes # backup first, so a failure below never loses what was there
        Remove-ItemProperty -Path $at.RunKey -Name $v.Name
        Write-Host "MWB autostart: removed Run value '$($v.Name)' ($($at.RunKey))"
    }

    foreach ($f in @(Get-StartupShortcuts)) {
        $kept = Join-Path $stash $f.Name
        if ($done -contains "startup|$($at.StartupDir)|$($f.Name)") { continue }
        New-Item -ItemType Directory -Force $stash | Out-Null
        Move-Item $f.FullName $kept -Force
        $changes += [pscustomobject]@{ type = 'startup'; key = $at.StartupDir; name = $f.Name; data = $kept; kind = 'file' }
        Write-Backup $changes
        Write-Host "MWB autostart: moved Startup shortcut '$($f.Name)' out of the Startup folder"
    }

    $pt = $at.PowerToysSettings
    if ((Test-Path $pt) -and -not ($done -contains "powertoys|$pt|MouseWithoutBorders")) {
        $text = [IO.File]::ReadAllText($pt)
        $m = [regex]::Matches($text, $ptEnabled)
        if ($m.Count -eq 1 -and $m[0].Groups[2].Value -eq 'true') {
            $changes += [pscustomobject]@{ type = 'powertoys'; key = $pt; name = 'MouseWithoutBorders'; data = 'true'; kind = 'enabled' }
            Write-Backup $changes
            [IO.File]::WriteAllText($pt, [regex]::Replace($text, $ptEnabled, '${1}false'), (New-Object Text.UTF8Encoding $false))
            Write-Host "MWB autostart: turned off PowerToys' Mouse Without Borders module ($pt)"
        }
        elseif ($m.Count -eq 1) { Write-Host "MWB autostart: PowerToys' Mouse Without Borders module is already off" }
        elseif ($m.Count -gt 1) { Write-Host "MWB autostart: could not tell which PowerToys setting is MWB's; left $pt alone" }
    }

    # What needs admin: say so, change nothing.
    foreach ($v in @(Get-RunValues $at.MachineRunKey)) {
        Write-Host "MWB autostart: NOT changed, needs admin: Run value '$($v.Name)' in $($at.MachineRunKey). Remove it as administrator if MWB should not start."
    }
    foreach ($s in @(Get-Service -Name $at.ServiceFilter -ErrorAction SilentlyContinue)) {
        Write-Host "MWB autostart: NOT changed, needs admin: Windows service '$($s.Name)' ($($s.StartType), $($s.Status)). Seamdesk leaves it; stop it as administrator if you want it gone."
    }
    if (-not $changes) { Write-Host 'MWB autostart: nothing per-user to switch off.' }
    return
}

# Restore
foreach ($c in @(Read-Backup)) {
    switch ($c.type) {
        'run' {
            if (-not (Test-Path $c.key)) { New-Item -Path $c.key -Force | Out-Null }
            if ($null -ne (Get-Item $c.key).GetValue($c.name, $null)) {
                Write-Host "MWB autostart: Run value '$($c.name)' is already there; left as is"
            }
            else {
                New-ItemProperty -Path $c.key -Name $c.name -Value $c.data -PropertyType $c.kind | Out-Null
                Write-Host "MWB autostart: restored Run value '$($c.name)'"
            }
        }
        'startup' {
            $back = Join-Path $c.key $c.name
            if (-not (Test-Path $c.data)) { Write-Host "MWB autostart: the saved shortcut '$($c.name)' is gone; nothing to put back" }
            elseif (Test-Path $back) { Write-Host "MWB autostart: Startup shortcut '$($c.name)' is already there; left as is" }
            else { Move-Item $c.data $back; Write-Host "MWB autostart: put Startup shortcut '$($c.name)' back" }
        }
        'powertoys' {
            $text = if (Test-Path $c.key) { [IO.File]::ReadAllText($c.key) } else { '' }
            $m = [regex]::Matches($text, $ptEnabled)
            if ($m.Count -eq 1 -and $m[0].Groups[2].Value -eq 'false') {
                [IO.File]::WriteAllText($c.key, [regex]::Replace($text, $ptEnabled, '${1}true'), (New-Object Text.UTF8Encoding $false))
                Write-Host "MWB autostart: turned PowerToys' Mouse Without Borders module back on"
            }
            else { Write-Host 'MWB autostart: PowerToys MWB module is not off any more; left as is' }
        }
    }
}
if (Test-Path $BackupPath) { Remove-Item $BackupPath }
if (Test-Path $stash) { Remove-Item $stash -Recurse -Force }
if ($RemoveSeamdeskRun -and (Test-Path $SeamdeskRunKey)) {
    Remove-ItemProperty -Path $SeamdeskRunKey -Name $RemoveSeamdeskRun -ErrorAction SilentlyContinue
}
