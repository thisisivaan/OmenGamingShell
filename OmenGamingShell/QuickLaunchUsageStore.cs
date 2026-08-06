using System.IO;
using System.Text.Json;

namespace OmenGamingShell;

public static class QuickLaunchUsageStore
{
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OmenGamingShell", "quick-launch-usage.json");
    public static Dictionary<string, int> Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(FilePath)) ?? new(StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase); }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }
    public static void Record(string target)
    {
        var usage = Load(); usage[target] = usage.GetValueOrDefault(target) + 1;
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(usage, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string PinsFilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OmenGamingShell", "quick-launch-pins.json");
    public static HashSet<string> LoadPins()
    {
        try { return File.Exists(PinsFilePath) ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(PinsFilePath)) ?? new(StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase); }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }
    public static void Pin(string target)
    {
        var pins = LoadPins(); pins.Add(target);
        Directory.CreateDirectory(Path.GetDirectoryName(PinsFilePath)!);
        File.WriteAllText(PinsFilePath, JsonSerializer.Serialize(pins, new JsonSerializerOptions { WriteIndented = true }));
    }
    public static void Unpin(string target)
    {
        var pins = LoadPins(); pins.Remove(target);
        Directory.CreateDirectory(Path.GetDirectoryName(PinsFilePath)!);
        File.WriteAllText(PinsFilePath, JsonSerializer.Serialize(pins, new JsonSerializerOptions { WriteIndented = true }));
    }
}
