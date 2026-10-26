# Builds the release zip: a self-contained win-x64 publish of Seamdesk.App with the licence and notices, plus the installer
# scripts, and its SHA-256.
#   powershell -ExecutionPolicy Bypass -File tools\release.ps1 [-Version 0.5.0] [-Out <dir>]
# Output: <Out>\Seamdesk-<Version>-win-x64.zip and <Out>\Seamdesk-<Version>-win-x64.zip.sha256. Uses $env:DOTNET_ROOT, else dotnet on
# PATH. Don't commit the zip (docs/release.md).
param(
    [string]$Version = ((Select-String -Path (Join-Path $PSScriptRoot '..\CHANGELOG.md') -Pattern '^## \[([\d.]+)\]' | Select-Object -First 1).Matches[0].Groups[1].Value),
    [string]$Out = (Join-Path $PSScriptRoot '..\release')
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dotnet = if ($env:DOTNET_ROOT -and (Test-Path "$env:DOTNET_ROOT\dotnet.exe")) { "$env:DOTNET_ROOT\dotnet.exe" } else { 'dotnet' }

$name = "Seamdesk-$Version-win-x64"
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path
$stage = Join-Path $Out $name
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

& $dotnet publish (Join-Path $repo 'src\Seamdesk.App') -p:PublishProfile=win-x64 -p:RestoreLockedMode=true -o (Join-Path $stage 'app')
if ($LASTEXITCODE) { throw "publish failed ($LASTEXITCODE)" }
Copy-Item (Join-Path $repo 'installer\*.ps1') $stage

# The licence and notices travel with every copy (MIT), in app\ so install.ps1 installs them too: Seamdesk's, its
# components', and the .NET runtime's own from the runtime pack the publish used (its version is in the runtimeconfig).
$app = Join-Path $stage 'app'
Copy-Item (Join-Path $repo 'LICENSE'), (Join-Path $repo 'THIRD-PARTY-NOTICES.txt') $app
$runtime = ((Get-Content (Join-Path $app 'Seamdesk.runtimeconfig.json') -Raw | ConvertFrom-Json).runtimeOptions.includedFrameworks |
    Where-Object { $_.name -eq 'Microsoft.NETCore.App' }).version
$packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$notices = Join-Path $packages "microsoft.netcore.app.runtime.win-x64\$runtime\THIRD-PARTY-NOTICES.TXT"
if (-not (Test-Path $notices)) { throw "The .NET runtime's notices are not at $notices." }
Copy-Item $notices (Join-Path $app 'dotnet-THIRD-PARTY-NOTICES.txt')

$zip = Join-Path $Out "$name.zip"
if (Test-Path $zip) { Remove-Item $zip }
# Not Compress-Archive or CreateFromDirectory: on Windows PowerShell 5.1 both write backslashes into entry names.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$z = [IO.Compression.ZipFile]::Open($zip, 'Create')
try {
    foreach ($f in Get-ChildItem $stage -Recurse -File) {
        $entry = $f.FullName.Substring($Out.Length + 1).Replace('\', '/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z, $f.FullName, $entry, 'Optimal')
    }
}
finally { $z.Dispose() }
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
# "<hash> *<file>" is what sha256sum -c reads.
[IO.File]::WriteAllText("$zip.sha256", "$hash *$name.zip`n")
Write-Host "$zip"
Write-Host "SHA-256 $hash"
