using System.IO;
using System.Text.Json;

namespace OmenGamingShell;

public static class LibraryPreferencesStore
{
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static string PathName => Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "OmenGamingShell", "library-preferences.json");

    public static void Apply(IReadOnlyList<GameEntry> games)
    {
        var data = Load();
        foreach (var game in games)
        {
            var key = Key(game);
            game.IsFavorite = data.Favorites.Contains(key);
            game.IsHidden = data.Hidden.Contains(key);
            if (data.PerformanceProfiles.TryGetValue(key, out var profile)) game.PerformanceProfile = profile;
        }
    }

    public static void SetFavorite(GameEntry game, bool value) => Update(game, value, true);
    public static void SetHidden(GameEntry game, bool value) => Update(game, value, false);
    public static void SetPerformanceProfile(GameEntry game, string profile)
    {
        lock (Sync)
        {
            var data = Load();
            data.PerformanceProfiles[Key(game)] = profile;
            Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
            File.WriteAllText(PathName, JsonSerializer.Serialize(data, Options));
        }
    }

    private static void Update(GameEntry game, bool value, bool favorite)
    {
        lock (Sync)
        {
            var data = Load();
            var set = favorite ? data.Favorites : data.Hidden;
            if (value) set.Add(Key(game)); else set.Remove(Key(game));
            Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
            File.WriteAllText(PathName, JsonSerializer.Serialize(data, Options));
        }
    }

    private static Preferences Load()
    {
        try
        {
            if (!File.Exists(PathName)) return new();
            return JsonSerializer.Deserialize<Preferences>(File.ReadAllText(PathName), Options) ?? new();
        }
        catch { return new(); }
    }

    private static string Key(GameEntry game) => string.IsNullOrWhiteSpace(game.Target) ? game.Name.Trim() : game.Target.Trim();
    private sealed class Preferences
    {
        public HashSet<string> Favorites { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Hidden { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> PerformanceProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
