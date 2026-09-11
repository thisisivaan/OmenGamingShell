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
