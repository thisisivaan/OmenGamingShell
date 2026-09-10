using System.IO;
using System.Text.Json;

namespace OmenGamingShell;

public sealed class TodoEntry
{
    public string Text { get; set; } = string.Empty;
    public bool Done { get; set; }
}

public static class TodoStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static string PathName => Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "OmenGamingShell", "todo-list.json");

    public static List<TodoEntry> Load()
    {
        try
        {
            return File.Exists(PathName)
                ? JsonSerializer.Deserialize<List<TodoEntry>>(File.ReadAllText(PathName), Options) ?? new()
                : new();
        }
        catch { return new(); }
    }

    public static void Save(IReadOnlyList<TodoEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
            File.WriteAllText(PathName, JsonSerializer.Serialize(entries, Options));
        }
        catch { }
    }
}
