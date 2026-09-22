using System.IO;

namespace OmenGamingShell;

public sealed class GameEntry
{
    public string Name { get; set; } = "Unnamed game";
    public string Target { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string? WorkingDirectory { get; set; }
    public string? Cover { get; set; }
    public string? Background { get; set; }
    public string? Description { get; set; }
    public string? Genres { get; set; }
    public string? Developer { get; set; }
    public string? Publisher { get; set; }
    public string? ReleaseDate { get; set; }
    public string? Platforms { get; set; }
    public string? Rating { get; set; }
    public string Source { get; set; } = "Manual";
    public long TotalPlayTimeSeconds { get; set; }
    public DateTime? LastPlayedUtc { get; set; }
    public bool IsRunning { get; set; }
    public bool IsFavorite { get; set; }
    public bool IsHidden { get; set; }
    public string PerformanceProfile { get; set; } = "Balanced";
    public bool HasMetadataOverride { get; set; }
    public string FavoriteActionLabel => IsFavorite ? "REMOVE FROM FAVORITES" : "ADD TO FAVORITES";
    public string HiddenActionLabel => IsHidden ? "UNHIDE GAME" : "HIDE GAME";

    // Uninstall is offered when we can actually do something: delete a real local
    // install folder, or delegate to a store's own uninstaller link.
    public bool CanUninstall => !string.IsNullOrWhiteSpace(UninstallFolder) || !string.IsNullOrWhiteSpace(StoreUninstallTarget);

    public string UninstallActionLabel => "UNINSTALL GAME";

    // The local folder that would be deleted by an in-shell uninstall. Null when the
    // game is store-managed (Steam/Epic) and hand-off to the store is preferred.
    public string? UninstallFolder
    {
        get
        {
            var target = Target;
            if (string.IsNullOrWhiteSpace(target)) return null;
            if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && !uri.IsFile) return null; // store protocol
            if (target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return null; // shortcut only
            return FindGameRoot(target);
        }
    }

    // A deep link into the owning store that will drive the uninstall from there
    // (Steam / Epic). Present only for store games; local games use UninstallFolder.
    public string? StoreUninstallTarget
    {
        get
        {
            var target = Target;
            if (string.IsNullOrWhiteSpace(target)) return null;
            if (target.StartsWith("steam://rungameid/", StringComparison.OrdinalIgnoreCase))
            {
                var appId = target[(target.LastIndexOf('/') + 1)..].Trim();
                if (appId.Length > 0 && int.TryParse(appId, out _)) return $"steam://uninstall/{appId}";
                return null;
            }
            if (target.StartsWith("com.epicgames.launcher://apps/", StringComparison.OrdinalIgnoreCase))
            {
                var queryIndex = target.IndexOf('?');
                var appToken = queryIndex > 0 ? target[..queryIndex] : target;
                appToken = appToken[(appToken.LastIndexOf('/') + 1)..].Trim();
                if (appToken.Length > 0) return $"com.epicgames.launcher://apps/{appToken}?action=uninstall";
            }
            return null;
        }
    }

    // Walks up from an executable to the folder directly under a local game root
    // (X:\Games, X:\XboxGames, X:\Riot Games), which is the folder to remove. Falls
    // back to the executable's own directory when the game was never seen under a root.
    private string? FindGameRoot(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return null;
        var current = new DirectoryInfo(Path.GetDirectoryName(executable) ?? executable);
        while (current is not null)
        {
            var parent = current.Parent;
            if (parent is not null && IsLocalRoot(parent.Name))
                return current.FullName;
            current = parent;
        }
        var fallback = Path.GetDirectoryName(executable);
        if (string.IsNullOrWhiteSpace(fallback)) return null;
        if (string.Equals(fallback.TrimEnd(Path.DirectorySeparatorChar),
                          Path.GetPathRoot(fallback)?.TrimEnd(Path.DirectorySeparatorChar),
                          StringComparison.OrdinalIgnoreCase))
            return null; // never treat a drive root as a game folder
        return Directory.Exists(fallback) ? fallback : null;
    }

    private static bool IsLocalRoot(string name) =>
        name.Equals("Games", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("XboxGames", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Riot Games", StringComparison.OrdinalIgnoreCase);

    public string StoreName
    {
        get
        {
            var source = Source ?? string.Empty;
            if (source.Contains("Steam", StringComparison.OrdinalIgnoreCase)) return "Steam";
            if (source.Contains("Epic", StringComparison.OrdinalIgnoreCase)) return "Epic Games";
            if (source.Contains("Xbox", StringComparison.OrdinalIgnoreCase) ||
                source.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)) return "Xbox";
            if (source.Contains("GOG", StringComparison.OrdinalIgnoreCase) ||
                source.Contains("CD PROJEKT", StringComparison.OrdinalIgnoreCase)) return "GOG";
            if (source.Contains("Ubisoft", StringComparison.OrdinalIgnoreCase)) return "Ubisoft Connect";
            if (source.Contains("Electronic Arts", StringComparison.OrdinalIgnoreCase) ||
                source.StartsWith("EA", StringComparison.OrdinalIgnoreCase)) return "EA app";
            if (source.Contains("Blizzard", StringComparison.OrdinalIgnoreCase) ||
                source.Contains("Battle.net", StringComparison.OrdinalIgnoreCase) ||
                source.Contains("Activision", StringComparison.OrdinalIgnoreCase)) return "Battle.net";
            if (source.Contains("Riot", StringComparison.OrdinalIgnoreCase)) return "Riot Games";
            if (source.Contains("Rockstar", StringComparison.OrdinalIgnoreCase)) return "Rockstar Games";
            return "Local / Windows";
        }
    }

}
