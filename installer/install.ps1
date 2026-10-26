# Installs or upgrades Seamdesk for the current user. No admin, no MSI. Run it from the unzipped release folder:
#   powershell -ExecutionPolicy Bypass -File install.ps1
# Puts the app in -Root, a Start menu shortcut, and a run-at-sign-in value in HKCU\...\Run. Settings and keys in -DataDir
# are never touched. Turns off Mouse Without Borders' autostart where a per-user process can (mwb-autostart.ps1) and
# records it, so the tray's "Go back to Mouse Without Borders" and uninstall.ps1 can put it back.
# The other parameters are for the tests, which install into a scratch folder under another Run name.
param(
    [string]$Root = (Join-Path $env:LOCALAPPDATA 'Programs\Seamdesk'),
    [string]$RunName = 'Seamdesk',
    [string]$DataDir = (Join-Path $env:LOCALAPPDATA 'Seamdesk'),
    [string]$StartMenuDir = (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'),
    [string]$RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run',
    # An install of Glide, the project's first name, to move over (see below).
    [string]$OldRoot = (Join-Path $env:LOCALAPPDATA 'Programs\Glide'),
    [string]$OldRunName = 'Glide',
    [string]$OldDataDir = (Join-Path $env:LOCALAPPDATA 'Glide'),
    [hashtable]$Mwb = @{},
    [switch]$NoStart,
    [int]$RetrySeconds = 10
)
$ErrorActionPreference = 'Stop'

$src = if (Test-Path (Join-Path $PSScriptRoot 'app\Seamdesk.exe')) { Join-Path $PSScriptRoot 'app' } else { $PSScriptRoot }
if (-not (Test-Path (Join-Path $src 'Seamdesk.exe'))) { throw "Seamdesk.exe not found next to install.ps1 or in .\app. Unzip the whole release first." }

function Stop-From($name, $path) {
    $running = @(Get-Process $name -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $path })
    if ($running) {
        $running | Stop-Process -Force
        $running | Wait-Process -Timeout 10
    }
}

# Seamdesk was called Glide before. Once: stop that Glide, move its settings, keys and logs to $DataDir (DPAPI still reads
# them: same Windows account), and remove its files, Run value and Start menu shortcut. A Glide data folder next to an
# existing Seamdesk one is left alone.
Stop-From Glide (Join-Path $OldRoot 'Glide.exe')
if ((Test-Path $OldDataDir) -and -not (Test-Path $DataDir)) {
    Move-Item $OldDataDir $DataDir
    foreach ($log in 'glide.log', 'glide.old.log') {
        if (Test-Path (Join-Path $DataDir $log)) { Rename-Item (Join-Path $DataDir $log) ($log -replace '^glide', 'seamdesk') }
    }
    Write-Host "Moved Glide's settings and keys from $OldDataDir to $DataDir."
}
elseif (Test-Path $OldDataDir) { Write-Host "Left $OldDataDir alone: $DataDir already exists." }
if (Test-Path $RunKey) { Remove-ItemProperty -Path $RunKey -Name $OldRunName -ErrorAction SilentlyContinue }
Remove-Item (Join-Path $StartMenuDir 'Glide.lnk') -ErrorAction SilentlyContinue
# Its DLLs can stay locked for a moment after it stops; a lock that outlasts the retries leaves the folder for the user.
$deadline = (Get-Date).AddSeconds($RetrySeconds)
while (Test-Path $OldRoot) {
    try { Remove-Item $OldRoot -Recurse -Force }
    catch {
        if ((Get-Date) -gt $deadline) { Write-Host "Could not remove all of the old Glide in $OldRoot ($($_.Exception.Message)); delete that folder by hand."; break }
        Start-Sleep -Milliseconds 500
    }
}

# Upgrade in place: stop the Seamdesk that runs from $Root (and only that one), replace the files, start it again.
$exe = Join-Path $Root 'Seamdesk.exe'
Stop-From Seamdesk $exe
# A stopped process's DLLs can stay locked for a moment (or longer: virus scanner, an open Explorer window), so retry.
if (Test-Path $Root) {
    $deadline = (Get-Date).AddSeconds($RetrySeconds)
    while ($true) {
        try { Get-ChildItem $Root | Remove-Item -Recurse -Force; break }
        catch {
            if ((Get-Date) -gt $deadline) {
                throw "Could not remove the old files in $Root, so some are deleted and some are not. $($_.Exception.Message) Close whatever holds that file and run install.ps1 again: running it again is safe, your settings and keys are not touched."
            }
            Start-Sleep -Milliseconds 500
        }
    }
}
New-Item -ItemType Directory -Force $Root | Out-Null
Copy-Item (Join-Path $src '*') $Root -Recurse -Force
foreach ($f in 'install.ps1', 'uninstall.ps1', 'mwb-autostart.ps1') {
    Copy-Item (Join-Path $PSScriptRoot $f) $Root -Force
}

New-Item -ItemType Directory -Force $DataDir | Out-Null
if (-not (Test-Path $RunKey)) { New-Item -Path $RunKey -Force | Out-Null }
Set-ItemProperty -Path $RunKey -Name $RunName -Value "`"$exe`""

$lnk = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $StartMenuDir 'Seamdesk.lnk'))
$lnk.TargetPath = $exe
$lnk.WorkingDirectory = $Root
$lnk.Save()

& (Join-Path $Root 'mwb-autostart.ps1') -Action Disable -BackupPath (Join-Path $DataDir 'mwb-autostart-backup.json') -Mwb $Mwb

if (-not $NoStart) { Start-Process $exe }

Write-Host ''
Write-Host "Seamdesk is installed in $Root and starts at sign-in."
Write-Host 'Next:'
Write-Host '  1. Seamdesk is in the tray (the notification area, maybe behind the ^ arrow). Click it, then Settings.'
Write-Host '  2. Under Mouse Without Borders machines, add the laptop and type its security key.'
Write-Host '  3. Arrange the layout of the screens.'
Write-Host 'Windows may ask once to allow Seamdesk through the firewall: allow Private networks only.'
Write-Host 'To go back to Mouse Without Borders: tray menu > Go back to Mouse Without Borders. To remove Seamdesk: uninstall.ps1.'
