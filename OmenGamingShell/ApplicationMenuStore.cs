using System.IO;
using System.Text.Json;

namespace OmenGamingShell;

public static class ApplicationMenuStore
{
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OmenGamingShell", "application-menu.json");

    public static void Apply(IReadOnlyList<GameEntry> applications)
    {
        var settings = Load();
        foreach (var app in applications)
        {
            app.IsInApplicationMenu = settings.Added.Contains(app.Target) ||
                                      (!settings.Removed.Contains(app.Target) && IsGenerallyUseful(app));
        }
    }

    public static void SetVisible(GameEntry app, bool visible)
    {
        var settings = Load();
        if (visible) { settings.Removed.Remove(app.Target); settings.Added.Add(app.Target); }
        else { settings.Added.Remove(app.Target); settings.Removed.Add(app.Target); }
        app.IsInApplicationMenu = visible;
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static bool IsGenerallyUseful(GameEntry app)
    {
        if (app.ApplicationCategory is "Communication" or "Music" or "Browsers" or "Streaming" or "Creative" or "Games") return true;
        var name = app.Name.ToLowerInvariant();
        var common = new[] { "calculator", "notepad", "terminal", "file explorer", "photos", "camera", "clock",
            "paint", "snipping", "store", "mail", "calendar", "office", "word", "excel", "powerpoint",
            "settings", "7-zip", "winrar", "adobe", "pdf" };
        return common.Any(name.Contains);
    }

    private static MenuSettings Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<MenuSettings>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch { return new(); }
    }

    private sealed class MenuSettings
    {
        public HashSet<string> Added { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Removed { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
