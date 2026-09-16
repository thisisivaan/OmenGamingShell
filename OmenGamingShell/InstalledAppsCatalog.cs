using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace OmenGamingShell;

public static class InstalledAppsCatalog
{
    private static readonly object Sync = new();
    private static List<AppEntry>? _cache;
    private static DateTime _lastRefreshUtc = DateTime.MinValue;

    public static IReadOnlyList<AppEntry> GetApps()
    {
        lock (Sync)
        {
            if (_cache is not null && (DateTime.UtcNow - _lastRefreshUtc).TotalMinutes < 10)
                return _cache;
            _cache = Build();
            _lastRefreshUtc = DateTime.UtcNow;
            return _cache;
        }
    }

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
                apps.Add(new AppEntry { Name = name, Target = target, Icon = target });
            }
        }
    }

    private static void ScanUwpApps(List<AppEntry> apps, HashSet<string> seen)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell",
                "-NoProfile -NonInteractive -Command \"Get-StartApps | ForEach-Object { Write-Output ($_.AppID + '|' + $_.Name) }\"")
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
                var separator = line.IndexOf('|');
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