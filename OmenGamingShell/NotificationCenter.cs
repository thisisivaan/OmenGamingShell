using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;

namespace OmenGamingShell;

public sealed record NotificationEntry(DateTime Time, string Title, string Message, string Icon);

public static class NotificationCenter
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OmenGamingShell", "notifications.json");
    private static readonly ObservableCollection<NotificationEntry> _items = new();
    public static IReadOnlyCollection<NotificationEntry> Items => _items;
    public static event Action? Updated;

    public static void Push(string title, string message, string icon = "\uE7BA")
    {
        _items.Insert(0, new NotificationEntry(DateTime.Now, title, message, icon));
        if (_items.Count > 50) _items.RemoveAt(_items.Count - 1);
        Save();
        Updated?.Invoke();
    }

    public static void Load()
    {
        _items.Clear();
        try
        {
            if (!File.Exists(LogPath)) return;
            var list = JsonSerializer.Deserialize<List<NotificationEntryDto>>(File.ReadAllText(LogPath));
            if (list is null) return;
            foreach (var d in list.Take(50))
                _items.Add(new NotificationEntry(d.Time, d.Title, d.Message, d.Icon));
        }
        catch { }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            var list = _items.Select(n => new NotificationEntryDto { Time = n.Time, Title = n.Title, Message = n.Message, Icon = n.Icon }).ToList();
            File.WriteAllText(LogPath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public static void Clear() { _items.Clear(); Save(); Updated?.Invoke(); }

    private sealed class NotificationEntryDto
    {
        public DateTime Time { get; set; }
        public string Title { get; set; } = "";
        public string Message { get; set; } = "";
        public string Icon { get; set; } = "";
    }
}
