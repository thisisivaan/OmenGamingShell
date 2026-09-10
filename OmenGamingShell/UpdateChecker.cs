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
                            $"-WaitPid {processId} -InstallExe \"{installExe}\" -NewExe \"{newExe}\" -UpdateDir \"{updateDir}\"";
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
    [switch]$Elevated
)
$log = Join-Path $UpdateDir 'update.log'
function Write-Log([string]$m) {
    try { Add-Content -LiteralPath $log -Value ("[{0}] {1}" -f (Get-Date -Format o), $m) } catch {}
}
Write-Log "Started. Install=$InstallExe New=$NewExe"
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin -and -not $Elevated) {
    Write-Log 'Requesting elevation.'
    try {
        $relaunch = "-NoProfile -ExecutionPolicy Bypass -File `"{0}`" -WaitPid {1} -InstallExe `"{2}`" -NewExe `"{3}`" -UpdateDir `"{4}`" -Elevated" -f $MyInvocation.MyCommand.Path, $WaitPid, $InstallExe, $NewExe, $UpdateDir
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
try {
    Copy-Item -LiteralPath $NewExe -Destination $InstallExe -Force
    Write-Log "Replaced: $InstallExe"
} catch {
    Write-Log "Copy failed: $($_.Exception.Message)"
    Remove-Item -LiteralPath $UpdateDir -Recurse -Force -ErrorAction SilentlyContinue
    exit 1
}
Start-Sleep -Milliseconds 500
Remove-Item -LiteralPath $UpdateDir -Recurse -Force -ErrorAction SilentlyContinue
if (Test-Path -LiteralPath $InstallExe) {
    Start-Process -FilePath $InstallExe -WorkingDirectory (Split-Path -Parent $InstallExe)
    Write-Log 'Launched updated shell.'
} else {
    Write-Log 'Install exe not found after update.'
}
exit 0
""";
}