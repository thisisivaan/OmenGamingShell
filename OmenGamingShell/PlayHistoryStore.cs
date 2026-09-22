using System.IO;
using System.Text.Json;

namespace OmenGamingShell;

public static class PlayHistoryStore
{
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static string PathName => Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "OmenGamingShell", "play-history.json");

    public static void Apply(IReadOnlyList<GameEntry> games)
    {
        var history = Load();
        foreach (var game in games)
        {
            if (!history.TryGetValue(Key(game), out var item)) continue;
            game.TotalPlayTimeSeconds = item.TotalPlayTimeSeconds;
            game.LastPlayedUtc = item.LastPlayedUtc;
        }
    }

    public static void Record(GameEntry game, TimeSpan duration)
    {
        lock (Sync)
        {
            var history = Load();
            var key = Key(game);
            history.TryGetValue(key, out var item);
            item ??= new PlayHistory();
            item.TotalPlayTimeSeconds += Math.Max(0, (long)duration.TotalSeconds);
            item.LastPlayedUtc = DateTime.UtcNow;
            history[key] = item;
            Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
            File.WriteAllText(PathName, JsonSerializer.Serialize(history, Options));
            game.TotalPlayTimeSeconds = item.TotalPlayTimeSeconds;
            game.LastPlayedUtc = item.LastPlayedUtc;
        }
    }

    // Removes the tracked play history entry for a game (used on uninstall).
    public static void Remove(GameEntry game)
    {
        lock (Sync)
        {
            var history = Load();
            if (!history.Remove(Key(game))) return;
            Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
            File.WriteAllText(PathName, JsonSerializer.Serialize(history, Options));
            game.TotalPlayTimeSeconds = 0;
            game.LastPlayedUtc = null;
        }
    }

    private static Dictionary<string, PlayHistory> Load()
    {
        try
        {
            if (!File.Exists(PathName)) return new(StringComparer.OrdinalIgnoreCase);
            var result = JsonSerializer.Deserialize<Dictionary<string, PlayHistory>>(
                File.ReadAllText(PathName), Options);
            return result is null ? new(StringComparer.OrdinalIgnoreCase) :
                new(result, StringComparer.OrdinalIgnoreCase);
        }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }

    private static string Key(GameEntry game) => string.IsNullOrWhiteSpace(game.Target)
        ? game.Name.Trim() : game.Target.Trim();

    private sealed class PlayHistory
    {
        public long TotalPlayTimeSeconds { get; set; }
        public DateTime? LastPlayedUtc { get; set; }
    }
}
