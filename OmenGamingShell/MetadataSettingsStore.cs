using System.IO;
using System.Text.Json;

namespace OmenGamingShell;

public static class MetadataSettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OmenGamingShell", "metadata-sources.json");

    public static MetadataSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new MetadataSettings();
            var settings = JsonSerializer.Deserialize<MetadataSettings>(File.ReadAllText(SettingsPath), Options)
                           ?? new MetadataSettings();
            settings.Sources.RemoveAll(source => source.Id == "local-steam");
            var migrated = false;
            foreach (var source in settings.Sources)
            {
                if (!string.IsNullOrWhiteSpace(source.BaseUrl)) continue;
                if (source.Id.Equals("steamgriddb", StringComparison.OrdinalIgnoreCase) ||
                    source.Name.Equals("SteamGridDB", StringComparison.OrdinalIgnoreCase))
                {
                    source.BaseUrl = "https://www.steamgriddb.com/api/v2";
                    migrated = true;
                }
            }
            if (settings.PrimarySourceId == "local-steam" ||
                settings.Sources.All(source => source.Id != settings.PrimarySourceId))
            {
                settings.PrimarySourceId = settings.Sources.FirstOrDefault()?.Id ?? string.Empty;
                migrated = true;
            }
            if (migrated) Save(settings);
            return settings;
        }
        catch
        {
            return new MetadataSettings();
        }
    }

    public static void Save(MetadataSettings settings)
    {
        var directory = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, Options));
    }
}
