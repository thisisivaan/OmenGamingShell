using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace OmenGamingShell;

public static class InstalledAppsCatalog
{
    private static readonly object Sync = new();
    private static IReadOnlyList<AppEntry>? _cache;
    private static DateTime _lastRefreshUtc = DateTime.MinValue;
    private static Task? _building;

    private const int CacheMinutes = 30;
    private static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OmenGamingShell", "installed-apps-cache.json");

    public static IReadOnlyList<AppEntry> GetApps()
    {
        lock (Sync)
        {
            if (_cache is null)
            {
                _cache = LoadFromDisk();
                if (_cache is not null) _lastRefreshUtc = DateTime.UtcNow;
            }

            var fresh = (DateTime.UtcNow - _lastRefreshUtc).TotalMinutes < CacheMinutes;
            if (fresh || _building is not null)
                return _cache ?? System.Array.Empty<AppEntry>();

            _building = Task.Run(() =>
            {
                var apps = Build();
                lock (Sync)
                {
                    _cache = apps;
                    _lastRefreshUtc = DateTime.UtcNow;
                    _building = null;
                }
                SaveToDisk(apps);
            });
            return _cache ?? System.Array.Empty<AppEntry>();
        }
    }

    public static void WarmUp()
    {
        lock (Sync)
        {
            if (_building is not null) return;
        }
        _ = GetApps();
    }

    private static List<AppEntry>? LoadFromDisk()
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            var raw = File.ReadAllText(CachePath);
            return JsonSerializer.Deserialize<List<AppEntry>>(raw, JsonOptions)?.ToList();
        }
        catch { return null; }
    }

    private static void SaveToDisk(IReadOnlyList<AppEntry> apps)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(apps, JsonOptions));
        }
        catch { }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static List<AppEntry> Build()
    {
        var apps = new List<AppEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ScanStartMenuShortcuts(apps, seen);
        ScanUwpApps(apps, seen);
        apps.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return apps;
    }

    private static void ScanStartMenuShortcuts(List<AppEntry> apps, HashSet<string> seen)
    {
        foreach (var folder in AllStartMenuFolders())
        {
            IEnumerable<string> shortcuts;
            try { shortcuts = Directory.EnumerateFiles(folder, "*.lnk", SearchOption.AllDirectories).ToList(); }
            catch { continue; }
            foreach (var shortcut in shortcuts)
            {
                var name = Path.GetFileNameWithoutExtension(shortcut);
                if (string.IsNullOrWhiteSpace(name) || IsUtility(name)) continue;
                if (!seen.Add(name)) continue;
                var target = Path.GetFullPath(shortcut);
                apps.Add(new AppEntry { Name = name, Target = target, Icon = ExpandVariables(target) });
            }
        }
    }

    private static void ScanUwpApps(List<AppEntry> apps, HashSet<string> seen)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell",
                "-NoProfile -NonInteractive -Command \"Get-StartApps | ForEach-Object { Write-Output ($_.AppID + [char]9 + $_.Name) }\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process is null) return;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(4000);
            foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = line.IndexOf('\t');
                if (separator <= 0) continue;
                var appId = line[..separator].Trim();
                var name = line[(separator + 1)..].Trim();
                if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(name)) continue;
                if (IsUtility(name)) continue;
                if (name.Equals("OMEN Gaming Shell", StringComparison.OrdinalIgnoreCase)) continue;
                if (!seen.Add(name)) continue;
                apps.Add(new AppEntry
                {
                    Name = name,
                    Target = appId,
                    Icon = $"shell:AppsFolder\\{appId}",
                    IsUwp = true
                });
            }
        }
        catch { }
    }

    private static IEnumerable<string> AllStartMenuFolders()
    {
        var folders = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
        };
        return folders.Where(folder => !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder)).Distinct();
    }

    private static string ExpandVariables(string path)
    {
        try { return Environment.ExpandEnvironmentVariables(path); }
        catch { return path; }
    }

    private static bool IsUtility(string name)
    {
        return name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("updater", StringComparison.OrdinalIgnoreCase) ||
               name.Contains(" readme", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(" readme", StringComparison.OrdinalIgnoreCase) ||
               name.Contains(" license", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(" license", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("license terms", StringComparison.OrdinalIgnoreCase);
    }
}