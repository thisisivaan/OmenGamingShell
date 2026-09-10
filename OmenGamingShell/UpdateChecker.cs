using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace OmenGamingShell;

public sealed record UpdateReleaseInfo(string Version, string AssetUrl, string Notes, string HtmlUrl);

public static class UpdateChecker
{
    public const string Repository = "thisisivaan/OmenGamingShell";
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/thisisivaan/OmenGamingShell/releases/latest";
    private const string AssetName = "OmenGamingShell.exe";

    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OmenGamingShell/1.0");
        client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        return client;
    }

    public static async Task<UpdateReleaseInfo?> FetchLatestReleaseAsync()
    {
        try
        {
            using var response = await Client.GetAsync(LatestReleaseApiUrl);
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            var root = document.RootElement;
            var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(tag)) return null;
            var version = tag.TrimStart('v', 'V');
            string? assetUrl = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.TryGetProperty("name", out var name) && name.GetString() == AssetName &&
                        asset.TryGetProperty("browser_download_url", out var url) && url.GetString() is not null)
                    {
                        assetUrl = url.GetString();
                        break;
                    }
                }
            }
            if (string.IsNullOrWhiteSpace(assetUrl)) return null;
            var notes = root.TryGetProperty("body", out var notesElement) ? notesElement.GetString() ?? string.Empty : string.Empty;
            var htmlUrl = root.TryGetProperty("html_url", out var htmlElement) ? htmlElement.GetString() ?? string.Empty : string.Empty;
            return new UpdateReleaseInfo(version, assetUrl!, notes, htmlUrl);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool IsUpdateAvailable(string currentVersion, UpdateReleaseInfo release)
    {
        return Version.TryParse(currentVersion, out var current) &&
               Version.TryParse(release.Version, out var remote) &&
               remote > current;
    }

    public static bool IsVersionBlocked(string version)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OmenGamingShell", "blocked-versions.json");
            if (!File.Exists(path)) return false;
            var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path));
            return list?.Contains(version) == true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static async Task<bool> DownloadAsync(string url, string destinationPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode) return false;
            await using var source = await response.Content.ReadAsStreamAsync();
            await using var target = File.Create(destinationPath);
            await source.CopyToAsync(target);
            return new FileInfo(destinationPath).Length > 1_000_000;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static string? InstallExePath =>
        Process.GetCurrentProcess().MainModule?.FileName ?? Environment.ProcessPath;

    public static async Task<bool> DownloadAndApplyAsync(UpdateReleaseInfo release)
    {
        var installExe = InstallExePath;
        if (string.IsNullOrWhiteSpace(installExe)) return false;
        var updateDir = Path.Combine(Path.GetTempPath(), "OmenGamingShell", "update");
        try
        {
            var newExe = Path.Combine(updateDir, "OmenGamingShell.exe");
            if (!await DownloadAsync(release.AssetUrl, newExe)) return false;

            var scriptPath = Path.Combine(updateDir, "apply-update.ps1");
            await File.WriteAllTextAsync(scriptPath, BuildApplyScript());
            var processId = Environment.ProcessId;
            var arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" " +
                            $"-WaitPid {processId} -InstallExe \"{installExe}\" -NewExe \"{newExe}\" " +
                            $"-UpdateDir \"{updateDir}\" -Version \"{release.Version}\"";
            Process.Start(new ProcessStartInfo("powershell.exe", arguments) { UseShellExecute = true });
            return true;
        }
        catch (Exception)
        {
            try { if (Directory.Exists(updateDir)) Directory.Delete(updateDir, true); } catch { }
            return false;
        }
    }

    private static string BuildApplyScript() => """
param(
    [int]$WaitPid,
    [string]$InstallExe,
    [string]$NewExe,
    [string]$UpdateDir,
    [string]$Version,
    [switch]$Elevated
)
$log = Join-Path $UpdateDir 'update.log'
function Write-Log([string]$m) {
    try { Add-Content -LiteralPath $log -Value ("[{0}] {1}" -f (Get-Date -Format o), $m) } catch {}
}
Write-Log "Started. Install=$InstallExe New=$NewExe Version=$Version"
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin -and -not $Elevated) {
    Write-Log 'Requesting elevation.'
    try {
        $relaunch = "-NoProfile -ExecutionPolicy Bypass -File `"{0}`" -WaitPid {1} -InstallExe `"{2}`" -NewExe `"{3}`" -UpdateDir `"{4}`" -Version `"{5}`" -Elevated" -f $MyInvocation.MyCommand.Path, $WaitPid, $InstallExe, $NewExe, $UpdateDir, $Version
        Start-Process -FilePath 'powershell.exe' -ArgumentList $relaunch -Verb RunAs
        Write-Log 'Elevation started.'
    } catch {
        Write-Log "Elevation failed: $($_.Exception.Message)"
    }
    exit 0
}
Write-Log 'Waiting for the shell to exit...'
while (Get-Process -Id $WaitPid -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 500 }
Start-Sleep -Seconds 2

$backupExe = "$InstallExe.bak"
function Backup-CurrentExe {
    try {
        Copy-Item -LiteralPath $InstallExe -Destination $backupExe -Force
        Write-Log "Backed up current exe: $backupExe"
        return $true
    } catch {
        Write-Log "Backup failed: $($_.Exception.Message)"
        return $false
    }
}
function Record-BlockedVersion([string]$v) {
    if ([string]::IsNullOrWhiteSpace($v)) { return }
    $blockPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'OmenGamingShell\blocked-versions.json'
    try {
        $dir = Split-Path -Parent $blockPath
        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        $blocked = @()
        if (Test-Path -LiteralPath $blockPath) {
            try { $blocked = @(Get-Content -LiteralPath $blockPath -Raw | ConvertFrom-Json) } catch { $blocked = @() }
        }
        if ($blocked -notcontains $v) { $blocked += $v }
        $blocked | ConvertTo-Json | Set-Content -LiteralPath $blockPath
        Write-Log "Recorded blocked version $v"
    } catch {
        Write-Log "Block-list write failed: $($_.Exception.Message)"
    }
}

if (-not (Backup-CurrentExe)) {
    Remove-Item -LiteralPath $UpdateDir -Recurse -Force -ErrorAction SilentlyContinue
    exit 1
}

try {
    Copy-Item -LiteralPath $NewExe -Destination $InstallExe -Force
    Write-Log "Replaced: $InstallExe"
} catch {
    Write-Log "Copy failed: $($_.Exception.Message)"
    try { if (Test-Path -LiteralPath $backupExe) { Copy-Item -LiteralPath $backupExe -Destination $InstallExe -Force; Write-Log 'Restored backup after copy failure.' } } catch {}
    Remove-Item -LiteralPath $UpdateDir -Recurse -Force -ErrorAction SilentlyContinue
    exit 1
}

Start-Sleep -Milliseconds 500
$proc = Start-Process -FilePath $InstallExe -WorkingDirectory (Split-Path -Parent $InstallExe) -PassThru
Write-Log "Launched updated shell (pid $($proc.Id)). Watching for 30s..."
$deadline = (Get-Date).AddSeconds(30)
while (-not $proc.HasExited -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
if ($proc.HasExited) {
    Write-Log "Updated shell exited prematurely (exit $($proc.ExitCode)). Rolling back."
    Record-BlockedVersion $Version
    try {
        Copy-Item -LiteralPath $backupExe -Destination $InstallExe -Force
        Write-Log "Restored previous version: $InstallExe"
        Start-Process -FilePath $InstallExe -WorkingDirectory (Split-Path -Parent $InstallExe)
        Write-Log 'Launched previous version.'
    } catch {
        Write-Log "Rollback failed: $($_.Exception.Message)"
    }
    Remove-Item -LiteralPath $UpdateDir -Recurse -Force -ErrorAction SilentlyContinue
    exit 1
}
Remove-Item -LiteralPath $backupExe -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $UpdateDir -Recurse -Force -ErrorAction SilentlyContinue
Write-Log 'Update applied successfully.'
exit 0
""";
}