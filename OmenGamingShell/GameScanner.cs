using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OmenGamingShell;

public static partial class GameScanner
{
    public static IReadOnlyList<GameEntry> ScanInstalledGames(IProgress<int>? progress = null)
    {
        var games = new List<GameEntry>();
        ScanSafely(games, ScanSteam);
        progress?.Report(18);
        ScanSafely(games, ScanEpicGames);
        progress?.Report(28);
        ScanSafely(games, ScanStandaloneGameFolders);
        progress?.Report(43);
        ScanSafely(games, ScanGameShortcuts);
        progress?.Report(53);
        ScanSafely(games, ScanRegisteredGames);
        progress?.Report(62);
        return games;
    }

    private static void ScanSafely(List<GameEntry> games, Func<IEnumerable<GameEntry>> scanner)
    {
        try
        {
            games.AddRange(scanner());
        }
        catch
        {
            // A missing or partially installed launcher should not prevent shell startup.
        }
    }

    private static IEnumerable<GameEntry> ScanSteam()
    {
        var steamPath = FindSteamPath();
        if (steamPath is null) yield break;

        foreach (var library in FindSteamLibraries(steamPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var steamApps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamApps)) continue;

            foreach (var manifest in Directory.EnumerateFiles(steamApps, "appmanifest_*.acf"))
            {
                string contents;
                try { contents = File.ReadAllText(manifest); }
                catch { continue; }

                var appId = VdfValue(contents, "appid");
                var name = VdfValue(contents, "name");
                var installDirectory = VdfValue(contents, "installdir");
                if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(name)) continue;

                yield return new GameEntry
                {
                    Name = name,
                    Target = $"steam://rungameid/{appId}",
                    Source = "Steam",
                    Cover = FindSteamCover(steamPath, appId),
                    WorkingDirectory = string.IsNullOrWhiteSpace(installDirectory)
                        ? null
                        : Path.Combine(steamApps, "common", installDirectory)
                };
            }
        }
    }

    private static string? FindSteamPath()
    {
        var locations = new[]
        {
            (RegistryHive.CurrentUser, RegistryView.Default, @"Software\Valve\Steam", "SteamPath"),
            (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Valve\Steam", "InstallPath")
        };

        foreach (var (hive, view, subKey, valueName) in locations)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(subKey);
                if (key?.GetValue(valueName) is string path && Directory.Exists(path)) return path;
            }
            catch { }
        }

        return null;
    }

    private static IEnumerable<string> FindSteamLibraries(string steamPath)
    {
        yield return steamPath;
        var libraryFile = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(libraryFile)) yield break;

        string contents;
        try { contents = File.ReadAllText(libraryFile); }
        catch { yield break; }

        foreach (Match match in SteamLibraryPathRegex().Matches(contents))
        {
            var path = match.Groups[1].Value.Replace("\\\\", "\\");
            if (Directory.Exists(path)) yield return path;
        }
    }

    private static string? VdfValue(string contents, string key)
    {
        var match = Regex.Match(contents, $"\\\"{Regex.Escape(key)}\\\"\\s+\\\"([^\\\"]*)\\\"",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? FindSteamCover(string steamPath, string appId)
    {
        var folder = Path.Combine(steamPath, "appcache", "librarycache", appId);
        if (!Directory.Exists(folder)) return null;
        try
        {
            var source = Directory.EnumerateFiles(folder, "library_600x900*.jpg", SearchOption.AllDirectories)
                       .OrderBy(path => path.Length)
                       .FirstOrDefault()
                   ?? Directory.EnumerateFiles(folder, "library_capsule*.jpg", SearchOption.AllDirectories)
                       .OrderBy(path => path.Length)
                       .FirstOrDefault()
                   ?? Directory.EnumerateFiles(folder, "header*.jpg", SearchOption.AllDirectories)
                       .OrderBy(path => path.Length)
                       .FirstOrDefault();
            return source is null ? null : ArtworkCache.NormalizeCover(source, $"steam-{appId}");
        }
        catch { return null; }
    }

    private static IEnumerable<GameEntry> ScanEpicGames()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var manifests = Path.Combine(programData, "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifests)) yield break;

        foreach (var file in Directory.EnumerateFiles(manifests, "*.item"))
        {
            JsonDocument document;
            try { document = JsonDocument.Parse(File.ReadAllText(file)); }
            catch { continue; }

            using (document)
            {
                var root = document.RootElement;
                var name = JsonString(root, "DisplayName");
                var appName = JsonString(root, "AppName");
                var catalogNamespace = JsonString(root, "CatalogNamespace");
                var catalogItemId = JsonString(root, "CatalogItemId");
                var installLocation = JsonString(root, "InstallLocation");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(appName)) continue;

                var target = !string.IsNullOrWhiteSpace(catalogNamespace) && !string.IsNullOrWhiteSpace(catalogItemId)
                    ? $"com.epicgames.launcher://apps/{Uri.EscapeDataString(catalogNamespace)}%3A{Uri.EscapeDataString(catalogItemId)}%3A{Uri.EscapeDataString(appName)}?action=launch&silent=true"
                    : $"com.epicgames.launcher://apps/{Uri.EscapeDataString(appName)}?action=launch&silent=true";

                yield return new GameEntry
                {
                    Name = name,
                    Target = target,
                    Source = "Epic Games",
                    WorkingDirectory = Directory.Exists(installLocation) ? installLocation : null
                };
            }
        }
    }

    private static string? JsonString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IEnumerable<GameEntry> ScanStandaloneGameFolders()
    {
        var roots = new List<string>();
        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.IsReady))
        {
            roots.Add(Path.Combine(drive.RootDirectory.FullName, "Games"));
            roots.Add(Path.Combine(drive.RootDirectory.FullName, "XboxGames"));
            roots.Add(Path.Combine(drive.RootDirectory.FullName, "Riot Games"));
        }

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> gameFolders;
            try { gameFolders = Directory.EnumerateDirectories(root).ToList(); }
            catch { continue; }

            foreach (var folder in gameFolders)
            {
                var executable = FindLikelyGameExecutable(folder);
                if (executable is null) continue;

                yield return new GameEntry
                {
                    Name = GetExecutableGameName(executable, Path.GetFileName(folder)),
                    Target = executable,
                    Source = root.EndsWith("XboxGames", StringComparison.OrdinalIgnoreCase) ? "Xbox" : "Local",
                    WorkingDirectory = Path.GetDirectoryName(executable)
                };
            }
        }
    }

    private static IEnumerable<GameEntry> ScanGameShortcuts()
    {
        var startMenus = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
        };
        var trustedGroups = new[]
        {
            "Steam", "GOG.com", "GOG Galaxy", "Ubisoft", "EA Games", "Electronic Arts",
            "Battle.net", "Blizzard", "Riot Games", "Rockstar Games", "Xbox"
        };

        foreach (var menu in startMenus.Where(Directory.Exists))
        {
            IEnumerable<string> shortcuts;
            try { shortcuts = Directory.EnumerateFiles(menu, "*.lnk", SearchOption.AllDirectories).ToList(); }
            catch { continue; }

            foreach (var shortcut in shortcuts)
            {
                if (!trustedGroups.Any(group => shortcut.Contains($"{Path.DirectorySeparatorChar}{group}{Path.DirectorySeparatorChar}",
                        StringComparison.OrdinalIgnoreCase))) continue;
                var name = Path.GetFileNameWithoutExtension(shortcut);
                if (IsUtilityName(name)) continue;

                yield return new GameEntry
                {
                    Name = CleanGameName(name),
                    Target = shortcut,
                    Source = StoreFromText(shortcut)
                };
            }
        }
    }

    private static IEnumerable<GameEntry> ScanRegisteredGames()
    {
        var publisherMarkers = new[]
        {
            "Ubisoft", "Electronic Arts", "EA ", "GOG", "CD PROJEKT", "Blizzard", "Activision",
            "Riot Games", "Rockstar Games", "Bethesda", "2K", "SEGA", "Capcom", "Bandai Namco"
        };
        var registryLocations = new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.CurrentUser, RegistryView.Default, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")
        };

        foreach (var (hive, view, path) in registryLocations)
        {
            using var baseKey = OpenRegistryBase(hive, view);
            using var uninstall = baseKey?.OpenSubKey(path);
            if (uninstall is null) continue;

            foreach (var subKeyName in uninstall.GetSubKeyNames())
            {
                using var entry = uninstall.OpenSubKey(subKeyName);
                var name = entry?.GetValue("DisplayName") as string;
                var publisher = entry?.GetValue("Publisher") as string;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(publisher)) continue;
                if (!publisherMarkers.Any(marker => publisher.Contains(marker, StringComparison.OrdinalIgnoreCase))) continue;
                if (IsUtilityName(name)) continue;

                var location = entry?.GetValue("InstallLocation") as string;
                var displayIcon = CleanDisplayIcon(entry?.GetValue("DisplayIcon") as string);
                var executable = File.Exists(displayIcon)
                    ? displayIcon
                    : Directory.Exists(location) ? FindLikelyGameExecutable(location) : null;
                if (executable is null) continue;

                yield return new GameEntry
                {
                    Name = CleanGameName(name),
                    Target = executable,
                    Source = StoreFromText(publisher),
                    WorkingDirectory = Path.GetDirectoryName(executable)
                };
            }
        }
    }

    private static RegistryKey? OpenRegistryBase(RegistryHive hive, RegistryView view)
    {
        try { return RegistryKey.OpenBaseKey(hive, view); }
        catch { return null; }
    }

    private static string? FindLikelyGameExecutable(string gameFolder)
    {
        var candidates = EnumerateExecutables(gameFolder, 3)
            .Where(path => !IsUtilityName(Path.GetFileNameWithoutExtension(path)))
            .Select(path => (Path: path, Score: ScoreExecutable(path, gameFolder)))
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Path.Length)
            .ToList();
        return candidates.FirstOrDefault().Path;
    }

    private static IEnumerable<string> EnumerateExecutables(string root, int maxDepth)
    {
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((root, 0));
        while (pending.Count > 0)
        {
            var (current, depth) = pending.Dequeue();
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(current, "*.exe").ToList(); }
            catch { files = Array.Empty<string>(); }
            foreach (var file in files) yield return file;

            if (depth >= maxDepth) continue;
            IEnumerable<string> folders;
            try { folders = Directory.EnumerateDirectories(current).ToList(); }
            catch { folders = Array.Empty<string>(); }
            foreach (var folder in folders.Where(folder => !IsIgnoredFolder(folder)))
                pending.Enqueue((folder, depth + 1));
        }
    }

    private static int ScoreExecutable(string executable, string gameFolder)
    {
        var score = 0;
        var fileName = NormalizeName(Path.GetFileNameWithoutExtension(executable));
        var folderName = NormalizeName(Path.GetFileName(gameFolder));
        if (fileName == folderName) score += 100;
        else if (fileName.Contains(folderName, StringComparison.OrdinalIgnoreCase) ||
                 folderName.Contains(fileName, StringComparison.OrdinalIgnoreCase)) score += 55;
        if (executable.Contains("Win64", StringComparison.OrdinalIgnoreCase) ||
            executable.Contains("Binaries", StringComparison.OrdinalIgnoreCase)) score += 25;
        try
        {
            var size = new FileInfo(executable).Length;
            if (size > 10_000_000) score += 20;
            else if (size > 1_000_000) score += 8;
        }
        catch { }
        return score;
    }

    private static bool IsIgnoredFolder(string path) =>
        new[] { "redist", "redistributable", "support", "installer", "uninstall", "crash", "prereq" }
            .Any(marker => Path.GetFileName(path).Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool IsUtilityName(string name) =>
        IsStorefrontLauncher(name) || new[]
        {
            "unins", "uninstall", "setup", "install", "crash", "report", "redist", "prereq",
            "launcher", "updater", "update", "repair", "eac", "easyanticheat", "unitycrashhandler"
        }.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool IsStorefrontLauncher(string name)
    {
        var normalized = NormalizeName(name);
        return new[]
        {
            "steam", "epicgames", "epicgameslauncher", "goggalaxy", "ubisoftconnect",
            "eaapp", "battlenet", "rockstargameslauncher", "riotclient", "xbox"
        }.Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }

    private static string CleanGameName(string name) =>
        Regex.Replace(name, @"\s*\((?:x64|x86|64-bit|32-bit)\)\s*$", string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();

    private static string GetExecutableGameName(string executable, string fallback)
    {
        try
        {
            var productName = FileVersionInfo.GetVersionInfo(executable).ProductName?.Trim();
            if (!string.IsNullOrWhiteSpace(productName) && productName.Length >= 3 &&
                !new[] { "unity player", "unreal engine", "game", "launcher" }
                    .Contains(productName.ToLowerInvariant(), StringComparer.Ordinal))
                return CleanGameName(productName);
        }
        catch { }
        return CleanGameName(fallback);
    }

    private static string NormalizeName(string name) =>
        Regex.Replace(name, "[^a-z0-9]", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string StoreFromText(string value)
    {
        if (value.Contains("Steam", StringComparison.OrdinalIgnoreCase)) return "Steam";
        if (value.Contains("Epic", StringComparison.OrdinalIgnoreCase)) return "Epic Games";
        if (value.Contains("Xbox", StringComparison.OrdinalIgnoreCase) || value.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)) return "Xbox";
        if (value.Contains("GOG", StringComparison.OrdinalIgnoreCase) || value.Contains("CD PROJEKT", StringComparison.OrdinalIgnoreCase)) return "GOG";
        if (value.Contains("Ubisoft", StringComparison.OrdinalIgnoreCase)) return "Ubisoft Connect";
        if (value.Contains("Electronic Arts", StringComparison.OrdinalIgnoreCase) || value.Contains("EA Games", StringComparison.OrdinalIgnoreCase)) return "EA app";
        if (value.Contains("Blizzard", StringComparison.OrdinalIgnoreCase) || value.Contains("Battle.net", StringComparison.OrdinalIgnoreCase) || value.Contains("Activision", StringComparison.OrdinalIgnoreCase)) return "Battle.net";
        if (value.Contains("Riot", StringComparison.OrdinalIgnoreCase)) return "Riot Games";
        if (value.Contains("Rockstar", StringComparison.OrdinalIgnoreCase)) return "Rockstar Games";
        return "Local";
    }

    private static string? CleanDisplayIcon(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var cleaned = value.Trim().Trim('"');
        var iconIndex = cleaned.LastIndexOf(',');
        if (iconIndex > 0 && int.TryParse(cleaned[(iconIndex + 1)..], out _)) cleaned = cleaned[..iconIndex];
        return cleaned.Trim('"');
    }

    [GeneratedRegex("\\\"path\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SteamLibraryPathRegex();
}
