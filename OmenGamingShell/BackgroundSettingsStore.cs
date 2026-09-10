using System.IO;
using System.Text.Json;

namespace OmenGamingShell;

public sealed class BackgroundSettings
{
    public string? CustomBackgroundPath { get; set; }
    public bool UseCustomBackground { get; set; }
}

public static class BackgroundSettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OmenGamingShell", "background-settings.json");

    public static BackgroundSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new BackgroundSettings();
            var settings = JsonSerializer.Deserialize<BackgroundSettings>(File.ReadAllText(SettingsPath), Options)
                           ?? new BackgroundSettings();
            if (settings.UseCustomBackground &&
                (string.IsNullOrWhiteSpace(settings.CustomBackgroundPath) ||
                 !File.Exists(settings.CustomBackgroundPath)))
            {
                settings.UseCustomBackground = false;
            }
            return settings;
        }
        catch
        {
            return new BackgroundSettings();
        }
    }

    public static void Save(BackgroundSettings settings)
    {
        var directory = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, Options));
    }
}
