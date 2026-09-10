using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace OmenGamingShell;

public sealed record ClipboardEntry(string Text, DateTime Time);

public static class ClipboardHistory
{
    private static readonly ObservableCollection<ClipboardEntry> _items = new();
    public static IReadOnlyCollection<ClipboardEntry> Items => _items;
    public static event Action? Updated;

    private static string _lastText = "";
    private static DispatcherTimer? _timer;
    private static DispatcherTimer? _saveTimer;
    private static bool _dirty;

    public static void Start(Dispatcher dispatcher)
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            if (_dirty) { Save(); _dirty = false; }
        };
        Load();
    }

    private static void Poll()
    {
        try
        {
            if (!Clipboard.ContainsText()) return;
            var text = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text) || text == _lastText) return;
            _lastText = text;
            _items.Insert(0, new ClipboardEntry(text, DateTime.Now));
            if (_items.Count > 30) _items.RemoveAt(_items.Count - 1);
            _dirty = true;
            _saveTimer?.Stop();
            _saveTimer?.Start();
            Updated?.Invoke();
        }
        catch { }
    }

    public static void Copy(string text)
    {
        try
        {
            Clipboard.SetText(text);
            _lastText = text;
            _items.Insert(0, new ClipboardEntry(text, DateTime.Now));
            if (_items.Count > 30) _items.RemoveAt(_items.Count - 1);
            _dirty = true;
            _saveTimer?.Stop();
            _saveTimer?.Start();
            Updated?.Invoke();
        }
        catch { }
    }

    private static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OmenGamingShell", "clipboard-history.json");

    private static void Load()
    {
        _items.Clear();
        try
        {
            if (!File.Exists(StorePath)) return;
            var list = JsonSerializer.Deserialize<List<ClipboardEntry>>(File.ReadAllText(StorePath));
            if (list is not null) foreach (var e in list.Take(30)) _items.Add(e);
        }
        catch { }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(_items.ToList(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
