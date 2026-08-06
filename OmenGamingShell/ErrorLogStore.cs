using System.IO;
using System.Text.Json;

namespace OmenGamingShell;

public sealed class ShellErrorEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime LoggedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? FixedUtc { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Source { get; set; } = "User report";
    public string? Details { get; set; }
    public string LoggedDisplay => LoggedUtc.ToLocalTime().ToString("dd MMM yyyy  HH:mm");
}

public static class ErrorLogStore
{
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public static string FolderPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OmenGamingShell", "Diagnostics");
    public static string LoggedFilePath => Path.Combine(FolderPath, "errors-logged.json");
    public static string FixedFilePath => Path.Combine(FolderPath, "fixed-errors.json");

    public static IReadOnlyList<ShellErrorEntry> LoadLogged() => Load(LoggedFilePath)
        .OrderByDescending(entry => entry.LoggedUtc).ToList();

    public static void Log(string message, string source = "User report", string? details = null)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        lock (Sync)
        {
            var entries = Load(LoggedFilePath).ToList();
            entries.Add(new ShellErrorEntry { Message = message.Trim(), Source = source, Details = details });
            Save(LoggedFilePath, entries);
        }
    }

    public static void MarkFixed(Guid id)
    {
        lock (Sync)
        {
            var logged = Load(LoggedFilePath).ToList();
            var entry = logged.FirstOrDefault(item => item.Id == id);
            if (entry is null) return;
            logged.Remove(entry);
            entry.FixedUtc = DateTime.UtcNow;
            var fixedEntries = Load(FixedFilePath).ToList();
            fixedEntries.Add(entry);
            Save(LoggedFilePath, logged);
            Save(FixedFilePath, fixedEntries);
        }
    }

    private static IReadOnlyList<ShellErrorEntry> Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<List<ShellErrorEntry>>(File.ReadAllText(path)) ?? new() : new(); }
        catch { return Array.Empty<ShellErrorEntry>(); }
    }

    private static void Save(string path, IReadOnlyList<ShellErrorEntry> entries)
    {
        Directory.CreateDirectory(FolderPath);
        File.WriteAllText(path, JsonSerializer.Serialize(entries, Options));
    }
}
