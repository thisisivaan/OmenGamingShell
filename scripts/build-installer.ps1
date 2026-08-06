param(
    [string]$DotNetPath = 'dotnet',
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$OutputDirectory = "$PSScriptRoot\..\build\installer-release"
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$shellProject = Join-Path $repoRoot 'OmenGamingShell\OmenGamingShell.csproj'
$installerProject = Join-Path $repoRoot 'OmenGamingShell.Installer\OmenGamingShell.Installer.csproj'
$payloadFolder = Join-Path $repoRoot 'OmenGamingShell.Installer\Payload\app'
$payloadZip = Join-Path $repoRoot 'OmenGamingShell.Installer\Payload\OmenGamingShell.zip'
$staging = Join-Path ([System.IO.Path]::GetTempPath()) "omen-shell-payload-$([Guid]::NewGuid().ToString('N'))"

function Invoke-DotNet {
    param([string[]]$Arguments)
    & $DotNetPath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}

try {
    Write-Host 'Publishing Omen Gaming Shell (self-contained, single file)...'
    Invoke-DotNet @('publish', $shellProject, '-c', $Configuration, '-r', $Runtime,
        '--self-contained', 'true', '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true', '-o', $staging)

    Write-Host 'Refreshing installer payload...'
    if (Test-Path $payloadFolder) { Remove-Item -LiteralPath $payloadFolder -Recurse -Force }
    New-Item -ItemType Directory -Path $payloadFolder | Out-Null
    Copy-Item -LiteralPath (Join-Path $staging 'OmenGamingShell.exe') -Destination $payloadFolder
    Copy-Item -LiteralPath (Join-Path $staging 'games.json') -Destination $payloadFolder
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
    if (Test-Path $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}
