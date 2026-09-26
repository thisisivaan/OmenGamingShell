using System.Management;

namespace OmenGamingShell;

public sealed class OmenKeyWatcher : IDisposable
{
    public const int OmenButtonEventData = 8613;
    public const int OmenButtonEventId = 29;

    private const string Scope = @"\\.\root\wmi";
    private const string Query = "SELECT * FROM hpqBEvnt";

    private ManagementEventWatcher? _watcher;
    private EventArrivedEventHandler? _handler;
    private bool _disposed;

    public bool IsListening => _watcher is not null;

    public bool TryStart(Action onPressed)
    {
        if (_watcher is not null) return true;
        try
        {
            var watcher = new ManagementEventWatcher(new ManagementScope(Scope), new EventQuery(Query));
            EventArrivedEventHandler handler = (_, args) =>
            {
                try
                {
                    if (IsOmenButton(args.NewEvent)) onPressed();
                }
                catch { }
            };
            watcher.EventArrived += handler;
            watcher.Start();
            _watcher = watcher;
            _handler = handler;
            return true;
        }
        catch
        {
            Dispose();
            return false;
        }
    }

    private static bool IsOmenButton(ManagementBaseObject received)
    {
        var properties = received.Properties;
        if (properties is null) return false;
        try
        {
            if (!TryReadUInt32(properties, "EventData", out var data)) return false;
            if (data != OmenButtonEventData) return false;
            return !TryReadUInt32(properties, "EventID", out var id) || id == OmenButtonEventId;
        }
        catch { return false; }
    }

    private static bool TryReadUInt32(PropertyDataCollection properties, string name, out uint value)
    {
        value = 0;
        var property = properties[name];
        if (property is null || property.Value is null) return false;
        value = Convert.ToUInt32(property.Value);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var watcher = _watcher;
        _watcher = null;
        if (watcher is null) return;
        try
        {
            if (_handler is not null) watcher.EventArrived -= _handler;
            watcher.Stop();
            watcher.Dispose();
        }
        catch { }
        _handler = null;
    }
}
