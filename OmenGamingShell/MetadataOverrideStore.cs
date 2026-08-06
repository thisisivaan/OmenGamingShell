using System.IO;
using System.Text.Json;

namespace OmenGamingShell;

public static class MetadataOverrideStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static string PathName => Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "OmenGamingShell", "Metadata", "metadata-overrides.json");

    public static void Apply(IReadOnlyList<GameEntry> games)
    {
        var overrides = Load();
        foreach (var game in games)
            if (overrides.TryGetValue(Key(game), out var item)) Apply(game, item);
    }

    public static void Save(GameEntry game)
    {
        var overrides = Load();
        overrides[Key(game)] = new OverrideData
        {
            Name = game.Name, Description = game.Description, Genres = game.Genres,
            Developer = game.Developer, Publisher = game.Publisher, ReleaseDate = game.ReleaseDate,
            Platforms = game.Platforms, Cover = game.Cover, Background = game.Background
        };
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        File.WriteAllText(PathName, JsonSerializer.Serialize(overrides, Options));
        game.HasMetadataOverride = true;
    }

    private static Dictionary<string, OverrideData> Load()
    {
        try
        {
            if (!File.Exists(PathName)) return new(StringComparer.OrdinalIgnoreCase);
            var data = JsonSerializer.Deserialize<Dictionary<string, OverrideData>>(File.ReadAllText(PathName), Options);
            return data is null ? new(StringComparer.OrdinalIgnoreCase) : new(data, StringComparer.OrdinalIgnoreCase);
        }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }

    private static void Apply(GameEntry game, OverrideData item)
    {
        game.Name = item.Name ?? game.Name; game.Description = item.Description; game.Genres = item.Genres;
        game.Developer = item.Developer; game.Publisher = item.Publisher; game.ReleaseDate = item.ReleaseDate;
        game.Platforms = item.Platforms;
        if (!string.IsNullOrWhiteSpace(item.Cover) && File.Exists(item.Cover)) game.Cover = item.Cover;
        if (!string.IsNullOrWhiteSpace(item.Background) && File.Exists(item.Background)) game.Background = item.Background;
        game.HasMetadataOverride = true;
    }

    private static string Key(GameEntry game) => string.IsNullOrWhiteSpace(game.Target) ? game.Name.Trim() : game.Target.Trim();
    private sealed class OverrideData
    {
        public string? Name { get; set; } public string? Description { get; set; } public string? Genres { get; set; }
        public string? Developer { get; set; } public string? Publisher { get; set; } public string? ReleaseDate { get; set; }
        public string? Platforms { get; set; } public string? Cover { get; set; } public string? Background { get; set; }
    }
}
