# Removes Seamdesk for the current user: its files, Start menu shortcut and run-at-sign-in value, and puts Mouse Without
# Borders' autostart back as install.ps1 recorded it. Settings and keys in -DataDir are kept unless -KeepSettings:$false;
# then it asks first (-Force skips the question).
param(
    [string]$Root = (Join-Path $env:LOCALAPPDATA 'Programs\Seamdesk'),
    [string]$RunName = 'Seamdesk',
    [string]$DataDir = (Join-Path $env:LOCALAPPDATA 'Seamdesk'),
    [string]$StartMenuDir = (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'),
    [string]$RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run',
    [hashtable]$Mwb = @{},
    [bool]$KeepSettings = $true,
    [switch]$Force,
    [int]$RetrySeconds = 10
)
$ErrorActionPreference = 'Stop'

$exe = Join-Path $Root 'Seamdesk.exe'
$running = @(Get-Process Seamdesk -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })
if ($running) {
    $running | Stop-Process -Force
    $running | Wait-Process -Timeout 10
}

# Run the restore from a copy: it lives in $Root, which is about to be deleted.
$restore = Join-Path $env:TEMP "mwb-autostart-$PID.ps1"
if (Test-Path (Join-Path $Root 'mwb-autostart.ps1')) {
    Copy-Item (Join-Path $Root 'mwb-autostart.ps1') $restore
    & $restore -Action Restore -BackupPath (Join-Path $DataDir 'mwb-autostart-backup.json') -Mwb $Mwb
    Remove-Item $restore
}

if (Test-Path $RunKey) { Remove-ItemProperty -Path $RunKey -Name $RunName -ErrorAction SilentlyContinue }
Remove-Item (Join-Path $StartMenuDir 'Seamdesk.lnk') -ErrorAction SilentlyContinue
if (Test-Path $Root) {
    # The running script sits in $Root; Windows lets its own file go once it has been read.
    # Retry: the stopped Seamdesk's DLLs can stay locked for a moment.
    $deadline = (Get-Date).AddSeconds($RetrySeconds)
    while ($true) {
        try { Remove-Item $Root -Recurse -Force; break }
        catch {
            if ((Get-Date) -gt $deadline) {
                throw "Could not remove $Root, so some files are deleted and some are not. $($_.Exception.Message) Close whatever holds that file and run uninstall.ps1 again: running it again is safe."
            }
            Start-Sleep -Milliseconds 500
        }
    }
}

if (-not $KeepSettings -and (Test-Path $DataDir)) {
    if ($Force -or (Read-Host "Delete Seamdesk's settings and keys in $DataDir? They cannot be recovered. [y/N]") -match '^y') {
        Remove-Item $DataDir -Recurse -Force
        Write-Host "Deleted $DataDir."
    }
    else { Write-Host "Kept $DataDir." }
}
elseif (Test-Path $DataDir) { Write-Host "Kept settings and keys in $DataDir." }
Write-Host 'Seamdesk is uninstalled.'
