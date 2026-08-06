param(
    [Parameter(Mandatory = $true)]
    [string]$PublishedExecutable
)

$resolvedExecutable = (Resolve-Path -LiteralPath $PublishedExecutable -ErrorAction Stop).Path
if ([IO.Path]::GetFileName($resolvedExecutable) -ne 'OmenGamingShell.exe') {
    throw 'Select the published OmenGamingShell.exe file.'
}

$winlogonKey = 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Winlogon'
New-Item -Path $winlogonKey -Force | Out-Null
Set-ItemProperty -Path $winlogonKey -Name Shell -Value ('"' + $resolvedExecutable + '"')

Write-Host 'Omen Gaming Shell is registered for the current user.'
Write-Host 'Sign out and sign back in to activate it.'
