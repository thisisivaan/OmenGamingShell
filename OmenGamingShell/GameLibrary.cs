using System.Text.Json;
using System.IO;

namespace OmenGamingShell;

public static class GameLibrary
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static IReadOnlyList<GameEntry> Load(IProgress<int>? progress = null)
    {
        var games = new List<GameEntry>();
        games.AddRange(LoadConfigured());
        progress?.Report(5);
        games.AddRange(GameScanner.ScanInstalledGames(progress));

        var result = games
            .Where(game => !string.IsNullOrWhiteSpace(game.Name) && !string.IsNullOrWhiteSpace(game.Target))
            .GroupBy(game => game.Target.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        GameMetadataCache.Apply(result);
        MetadataOverrideStore.Apply(result);
        PlayHistoryStore.Apply(result);
        LibraryPreferencesStore.Apply(result);
        result = result.OrderByDescending(game => game.IsFavorite)
            .ThenByDescending(game => game.LastPlayedUtc)
            .ThenBy(game => game.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        progress?.Report(65);
        return result;
    }

    private static IReadOnlyList<GameEntry> LoadConfigured()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "games.json");
        if (!File.Exists(path))
            return Array.Empty<GameEntry>();

        try
        {
            return JsonSerializer.Deserialize<List<GameEntry>>(File.ReadAllText(path), Options)
                   ?? new List<GameEntry>();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Invalid game library: {path}", exception);
        }
    }
}
