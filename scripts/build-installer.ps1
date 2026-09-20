param(
    [string]$DotNetPath = 'dotnet',
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$OutputDirectory = "$PSScriptRoot\..\build\installer-release"
)

# Builds the installer from THE app exe in the repo root.
#
# The app exe is not built here - build-app.ps1 does that, and this script calls it
# first so the installer can never ship a stale exe. Staging only exists to create
# the payload zip; the loose staged copy is deleted afterwards, leaving the
# repo-root exe as the only copy of the app.

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$buildAppScript = Join-Path $PSScriptRoot 'build-app.ps1'
$appExe = Join-Path $repoRoot 'OmenGamingShell.exe'
$appGames = Join-Path $repoRoot 'games.json'
$installerProject = Join-Path $repoRoot 'OmenGamingShell.Installer\OmenGamingShell.Installer.csproj'
$payloadFolder = Join-Path $repoRoot 'OmenGamingShell.Installer\Payload\app'
$payloadZip = Join-Path $repoRoot 'OmenGamingShell.Installer\Payload\OmenGamingShell.zip'

function Invoke-DotNet {
    param([string[]]$Arguments)
    & $DotNetPath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}

Write-Host 'Refreshing the app exe in the main dir...'
& $buildAppScript -DotNetPath $DotNetPath -Configuration $Configuration -Runtime $Runtime
if (-not (Test-Path $appExe)) { throw "App exe missing after build: $appExe" }

try {
    Write-Host 'Staging installer payload from the main-dir exe...'
    if (Test-Path $payloadFolder) { Remove-Item -LiteralPath $payloadFolder -Recurse -Force }
    New-Item -ItemType Directory -Path $payloadFolder | Out-Null
    Copy-Item -LiteralPath $appExe -Destination $payloadFolder
    Copy-Item -LiteralPath $appGames -Destination $payloadFolder
    if (Test-Path $payloadZip) { Remove-Item -LiteralPath $payloadZip -Force }
    Compress-Archive -Path "$payloadFolder\*" -DestinationPath $payloadZip -CompressionLevel Optimal

    Write-Host 'Publishing installer (self-contained, single file)...'
    Invoke-DotNet @('publish', $installerProject, '-c', $Configuration, '-r', $Runtime,
        '--self-contained', 'true', '-p:PublishSingleFile=true', '-o', $OutputDirectory)

    $installerExe = Join-Path $OutputDirectory 'OmenGamingShellInstaller.exe'
    $setupExe = Join-Path $OutputDirectory 'OMEN Gaming Shell Setup.exe'
    Copy-Item -LiteralPath $installerExe -Destination $setupExe -Force
    Write-Host "Installer ready: $setupExe"
}
finally {
    # The payload is embedded into the installer at build time, so the staged copy is
    # removed straight away. The zip stays: the installer project embeds it by path,
    # and it is an archive, not a second loose exe.
    if (Test-Path $payloadFolder) { Remove-Item -LiteralPath $payloadFolder -Recurse -Force }
}
