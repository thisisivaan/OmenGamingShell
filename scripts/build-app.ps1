param(
    [string]$DotNetPath = 'dotnet',
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64'
)

# Builds THE app exe. This is the only place OmenGamingShell.exe is produced, and
# it always lands in the repo root so there is exactly one copy on disk.
#
# Publishing to a temp folder and copying just the exe (plus its .pdb) keeps the
# root clean - the single-file bundle carries its own native libraries.
#
#   .\scripts\build-app.ps1
#
# The installer script calls this for you; you never need to run both.

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$shellProject = Join-Path $repoRoot 'OmenGamingShell\OmenGamingShell.csproj'
$outputExe = Join-Path $repoRoot 'OmenGamingShell.exe'
$outputPdb = Join-Path $repoRoot 'OmenGamingShell.pdb'
$staging = Join-Path ([System.IO.Path]::GetTempPath()) "omen-app-$([Guid]::NewGuid().ToString('N'))"

try {
    Write-Host 'Publishing Omen Gaming Shell (self-contained, single file)...'
    & $DotNetPath publish $shellProject -c $Configuration -r $Runtime `
        --self-contained 'true' -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true -o $staging
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

    Copy-Item -LiteralPath (Join-Path $staging 'OmenGamingShell.exe') -Destination $outputExe -Force
    $stagedPdb = Join-Path $staging 'OmenGamingShell.pdb'
    if (Test-Path $stagedPdb) { Copy-Item -LiteralPath $stagedPdb -Destination $outputPdb -Force }

    Write-Host "App exe rebuilt in main dir: $outputExe"
}
finally {
    if (Test-Path $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}
