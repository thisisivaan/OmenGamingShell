using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace OmenGamingShell;

public static class ApplicationScanner
{
    private static readonly string[] ExcludedNames =
    {
        "uninstall", "readme", "help", "documentation", "license", "release notes",
        "website", "manual", "repair", "update", "setup", "runtime", "redistributable",
        "driver", "framework", "sdk", "service", "helper", "component", "language pack",
        "webview", "plugin", "codec", "online services", "host", "experience pack"
    };

    public static IReadOnlyList<GameEntry> Scan()
    {
        var menus = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
        };
        var applications = new List<GameEntry>();
        foreach (var menu in menus.Where(Directory.Exists))
        {
            IEnumerable<string> shortcuts;
            try { shortcuts = Directory.EnumerateFiles(menu, "*.lnk", SearchOption.AllDirectories); }
            catch { continue; }
            foreach (var shortcut in shortcuts)
            {
                var name = Path.GetFileNameWithoutExtension(shortcut);
                if (string.IsNullOrWhiteSpace(name) || ExcludedNames.Any(excluded =>
                        name.Contains(excluded, StringComparison.OrdinalIgnoreCase))) continue;
                applications.Add(new GameEntry
                {
                    Name = name, Target = shortcut, Source = "Windows", IsApplication = true,
                    ApplicationCategory = Categorize(name, shortcut), ApplicationIconPath = shortcut
                });
            }
        }
        applications.AddRange(ScanWindowsStartApps());
        applications.AddRange(ScanAppxApplications());
        var result = applications.GroupBy(app => app.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).OrderBy(app => app.Name).ToList();
        ApplicationMenuStore.Apply(result);
        return result;
    }

    private static IReadOnlyList<GameEntry> ScanRegisteredApplications()
    {
        var applications = new List<GameEntry>();
        var locations = new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Registry64),
            (RegistryHive.CurrentUser, RegistryView.Registry32)
        };
        foreach (var (hive, view) in locations)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var keyName in uninstall.GetSubKeyNames())
                {
                    using var key = uninstall.OpenSubKey(keyName);
                    var name = key?.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(name) || Convert.ToInt32(key?.GetValue("SystemComponent") ?? 0) == 1 ||
                        ExcludedNames.Any(excluded => name.Contains(excluded, StringComparison.OrdinalIgnoreCase))) continue;
                    var target = ResolveRegisteredTarget(key);
                    if (string.IsNullOrWhiteSpace(target)) continue;
                    applications.Add(new GameEntry
                    {
                        Name = name.Trim(), Target = target, Source = "Windows", IsApplication = true,
                        ApplicationCategory = Categorize(name, target), ApplicationIconPath = target
                    });
                }
            }
            catch { }
        }
        return applications;
    }

    private static string? ResolveRegisteredTarget(RegistryKey? key)
    {
        if (key is null) return null;
        var displayIcon = Environment.ExpandEnvironmentVariables((key.GetValue("DisplayIcon") as string ?? string.Empty).Trim().Trim('"'));
        var comma = displayIcon.LastIndexOf(',');
        if (comma > 1) displayIcon = displayIcon[..comma].Trim().Trim('"');
        if (displayIcon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(displayIcon)) return displayIcon;
        var folder = Environment.ExpandEnvironmentVariables((key.GetValue("InstallLocation") as string ?? string.Empty).Trim().Trim('"'));
        if (!Directory.Exists(folder)) return null;
        try
        {
            return Directory.EnumerateFiles(folder, "*.exe", SearchOption.TopDirectoryOnly)
                .Where(path => !ExcludedNames.Any(excluded => Path.GetFileNameWithoutExtension(path).Contains(excluded, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(path => new FileInfo(path).Length).FirstOrDefault();
        }
        catch { return null; }
    }

    private static IReadOnlyList<GameEntry> ScanWindowsStartApps()
    {
        var applications = new List<GameEntry>();
        try
        {
            using var process = Process.Start(new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                Arguments = "-NoProfile -NonInteractive -Command \"Get-StartApps | Select-Object Name,AppID | ConvertTo-Json -Compress\""
            });
            if (process is null) return applications;
            var json = process.StandardOutput.ReadToEnd();
            process.WaitForExit(8000);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(json)) return applications;
            using var document = JsonDocument.Parse(json);
            var entries = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToArray()
                : new[] { document.RootElement };
            foreach (var entry in entries)
            {
                var name = entry.TryGetProperty("Name", out var nameValue) ? nameValue.GetString() : null;
                var appId = entry.TryGetProperty("AppID", out var idValue) ? idValue.GetString() : null;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(appId) ||
                    ExcludedNames.Any(excluded => name.Contains(excluded, StringComparison.OrdinalIgnoreCase))) continue;
                applications.Add(new GameEntry
                {
                    Name = name, Target = $"shell:AppsFolder\\{appId}", Source = "Windows",
                    IsApplication = true, ApplicationCategory = Categorize(name, appId),
                    ApplicationIconPath = $"shell:AppsFolder\\{appId}"
                });
            }
        }
        catch { }
        return applications;
    }

    private static IReadOnlyList<GameEntry> ScanAppxApplications()
    {
        var applications = new List<GameEntry>();
        try
        {
            using var process = Process.Start(new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                Arguments = "-NoProfile -NonInteractive -Command \"Get-AppxPackage | Where-Object { -not $_.IsFramework -and -not $_.IsResourcePackage -and $_.Name -notlike 'Microsoft.*' } | ForEach-Object { $p=$_; try { $m=Get-AppxPackageManifest $_; foreach($a in $m.Package.Applications.Application){ [pscustomobject]@{Name=$p.Name;DisplayName=$a.VisualElements.DisplayName;Family=$p.PackageFamilyName;AppId=$a.Id;Location=$p.InstallLocation;Logo=$a.VisualElements.Square44x44Logo;AppListEntry=$a.VisualElements.AppListEntry} } } catch {} } | ConvertTo-Json -Compress\""
            });
            if (process is null) return applications;
            var json = process.StandardOutput.ReadToEnd();
            process.WaitForExit(15000);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(json)) return applications;
            using var document = JsonDocument.Parse(json);
            var entries = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToArray() : new[] { document.RootElement };
            foreach (var entry in entries)
            {
                var packageName = entry.TryGetProperty("Name", out var nameValue) ? nameValue.GetString() : null;
                var manifestName = entry.TryGetProperty("DisplayName", out var displayValue) ? displayValue.GetString() : null;
                var family = entry.TryGetProperty("Family", out var familyValue) ? familyValue.GetString() : null;
                var appId = entry.TryGetProperty("AppId", out var appIdValue) ? appIdValue.GetString() : null;
                var location = entry.TryGetProperty("Location", out var locationValue) ? locationValue.GetString() : null;
                var logo = entry.TryGetProperty("Logo", out var logoValue) ? logoValue.GetString() : null;
                var appListEntry = entry.TryGetProperty("AppListEntry", out var listValue) ? listValue.GetString() : null;
                if (string.IsNullOrWhiteSpace(packageName) || string.IsNullOrWhiteSpace(family) || string.IsNullOrWhiteSpace(appId)) continue;
                if (string.Equals(appListEntry, "none", StringComparison.OrdinalIgnoreCase)) continue;
                var displayName = !string.IsNullOrWhiteSpace(manifestName) && !manifestName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)
                    ? manifestName : FriendlyPackageName(packageName);
                if (ExcludedNames.Any(excluded => displayName.Contains(excluded, StringComparison.OrdinalIgnoreCase) ||
                                                  packageName.Contains(excluded, StringComparison.OrdinalIgnoreCase))) continue;
                var target = $"shell:AppsFolder\\{family}!{appId}";
                applications.Add(new GameEntry
                {
                    Name = displayName, Target = target, Source = "Microsoft Store", IsApplication = true,
                    ApplicationCategory = Categorize(displayName, packageName),
                    ApplicationIconPath = ResolvePackageLogo(location, logo) ?? target
                });
            }
        }
        catch { }
        return applications;
    }

    private static string FriendlyPackageName(string value)
    {
        var name = value.Contains('.') ? value[(value.LastIndexOf('.') + 1)..] : value;
        return Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ").Trim();
    }

    private static string? ResolvePackageLogo(string? location, string? relativeLogo)
    {
        if (string.IsNullOrWhiteSpace(location) || string.IsNullOrWhiteSpace(relativeLogo)) return null;
        var exact = Path.Combine(location, relativeLogo.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(exact)) return exact;
        var folder = Path.GetDirectoryName(exact);
        var stem = Path.GetFileNameWithoutExtension(exact);
        if (!Directory.Exists(folder)) return null;
        try
        {
            return Directory.EnumerateFiles(folder, $"{stem}*.png")
                .OrderByDescending(path => path.Contains("targetsize-256", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(path => path.Contains("unplated", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(path => path.Contains("scale-400", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(path => new FileInfo(path).Length).FirstOrDefault();
        }
        catch { return null; }
    }

    private static string Categorize(string name, string path)
    {
        var value = $"{name} {path}".ToLowerInvariant();
        if (ContainsAny(value, "discord", "teams", "slack", "telegram", "whatsapp", "zoom", "skype")) return "Communication";
        if (ContainsAny(value, "spotify", "music", "itunes", "foobar", "audacity")) return "Music";
        if (ContainsAny(value, "chrome", "firefox", "edge", "opera", "brave", "vivaldi", "browser")) return "Browsers";
        if (ContainsAny(value, "netflix", "youtube", "twitch", "vlc", "media player", "plex", "obs")) return "Streaming";
        if (ContainsAny(value, "photoshop", "illustrator", "premiere", "blender", "resolve", "studio", "paint", "creative")) return "Creative";
        if (ContainsAny(value, "steam", "epic games", "xbox", "gog", "ubisoft", "battle.net", "battlenet", "riot client", "rockstar games", "ea app", "game launcher")) return "Games";
        return "Utilities";
    }

    private static bool ContainsAny(string value, params string[] terms) => terms.Any(value.Contains);
}
