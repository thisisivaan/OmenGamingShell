using System.IO;
using System.Text.Json;

namespace OmenGamingShell;

public static class GameMetadataCache
{
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static string MetadataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OmenGamingShell", "Metadata");

    private static string IndexPath => Path.Combine(MetadataFolder, "games-metadata.json");

    public static void Apply(IReadOnlyList<GameEntry> games)
    {
        var records = Load();
        foreach (var game in games)
        {
            if (!records.TryGetValue(GameKey(game), out var record)) continue;
            if (!string.IsNullOrWhiteSpace(record.Cover) && File.Exists(record.Cover))
                game.Cover = record.Cover;
            if (record.BackgroundVersion == 2 && !string.IsNullOrWhiteSpace(record.Background) &&
                File.Exists(record.Background) &&
                (record.Background.EndsWith("_2560x1440.jpg", StringComparison.OrdinalIgnoreCase) ||
                 record.Background.EndsWith("_cover_2560x1440.jpg", StringComparison.OrdinalIgnoreCase)))
                game.Background = record.Background;
            game.Description = record.Description;
            game.Genres = record.Genres;
            game.Developer = record.Developer;
            game.Publisher = record.Publisher;
            game.ReleaseDate = record.ReleaseDate;
            game.Platforms = record.Platforms;
            game.Rating = record.Rating;
        }
    }

    public static void Save(IReadOnlyList<GameEntry> games)
    {
        lock (Sync)
        {
            var records = Load();
            foreach (var game in games)
            {
                var hasCover = !string.IsNullOrWhiteSpace(game.Cover) && File.Exists(game.Cover);
                var hasBackground = !string.IsNullOrWhiteSpace(game.Background) && File.Exists(game.Background);
                if (!hasCover && !hasBackground) continue;
                records[GameKey(game)] = new CachedGameMetadata
                {
                    Name = game.Name,
                    Target = game.Target,
                    Cover = game.Cover,
                    Background = game.Background,
                    BackgroundVersion = 2,
                    Description = game.Description,
                    Genres = game.Genres,
                    Developer = game.Developer,
                    Publisher = game.Publisher,
                    ReleaseDate = game.ReleaseDate,
                    Platforms = game.Platforms,
                    Rating = game.Rating
                };
            }

            Directory.CreateDirectory(MetadataFolder);
            var temporaryPath = IndexPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(records, Options));
            File.Move(temporaryPath, IndexPath, true);
        }
    }

    private static Dictionary<string, CachedGameMetadata> Load()
    {
        try
        {
            if (!File.Exists(IndexPath)) return new(StringComparer.OrdinalIgnoreCase);
            var records = JsonSerializer.Deserialize<Dictionary<string, CachedGameMetadata>>(
                File.ReadAllText(IndexPath), Options);
            return records is null
                ? new(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, CachedGameMetadata>(records, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string GameKey(GameEntry game) =>
        string.IsNullOrWhiteSpace(game.Target) ? game.Name.Trim() : game.Target.Trim();

    private sealed class CachedGameMetadata
    {
        public string Name { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public string? Cover { get; set; }
        public string? Background { get; set; }
        public int BackgroundVersion { get; set; }
        public string? Description { get; set; }
        public string? Genres { get; set; }
        public string? Developer { get; set; }
        public string? Publisher { get; set; }
        public string? ReleaseDate { get; set; }
        public string? Platforms { get; set; }
        public string? Rating { get; set; }
    }
}
