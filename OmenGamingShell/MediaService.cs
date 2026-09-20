using Windows.Media.Control;
using Windows.Storage.Streams;

namespace OmenGamingShell;

public sealed record MediaInfo(string Title, string Artist, string AppName, bool IsPlaying, TimeSpan Position, TimeSpan Duration, IRandomAccessStreamReference? Thumbnail);

public static class MediaService
{
    private static readonly object Sync = new();
    private static Task<GlobalSystemMediaTransportControlsSessionManager>? _managerTask;
    private static GlobalSystemMediaTransportControlsSession? _session;
    private static GlobalSystemMediaTransportControlsSession? _subscribedSession;
    private static Action? _onChanged;

    public static Task<GlobalSystemMediaTransportControlsSessionManager> GetManagerAsync()
    {
        lock (Sync)
        {
            _managerTask ??= InitAsync();
            return _managerTask;
        }
    }

    private static async Task<GlobalSystemMediaTransportControlsSessionManager> InitAsync()
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _session = manager.GetCurrentSession();
        AttachSessionEvents(_session);
        manager.CurrentSessionChanged += (_, _) =>
        {
            lock (Sync)
            {
                _session = manager.GetCurrentSession();
                AttachSessionEvents(_session);
            }
            _onChanged?.Invoke();
        };
        return manager;
    }

    private static void AttachSessionEvents(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_subscribedSession is not null)
        {
            _subscribedSession.MediaPropertiesChanged -= OnSessionChanged;
            _subscribedSession.TimelinePropertiesChanged -= OnSessionChanged;
            _subscribedSession.PlaybackInfoChanged -= OnSessionChanged;
        }
        _subscribedSession = session;
        if (session is null) return;
        session.MediaPropertiesChanged += OnSessionChanged;
        session.TimelinePropertiesChanged += OnSessionChanged;
        session.PlaybackInfoChanged += OnSessionChanged;
    }

    private static void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => _onChanged?.Invoke();
    private static void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) => _onChanged?.Invoke();
    private static void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => _onChanged?.Invoke();

    public static void Start(Action onChanged)
    {
        _onChanged = onChanged;
        _ = GetManagerAsync();
    }

    private static async Task<GlobalSystemMediaTransportControlsSession?> GetSessionAsync()
    {
        var manager = await GetManagerAsync();
        return manager.GetCurrentSession();
    }

    public static async Task<MediaInfo?> GetCurrentMediaAsync()
    {
        try
        {
            var session = await GetSessionAsync();
            if (session is null) return null;
            var mediaProperties = await session.TryGetMediaPropertiesAsync();
            var info = session.GetTimelineProperties();
            var isPlaying = session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var pos = info.Position;
            var dur = info.EndTime - info.StartTime;
            return new MediaInfo(
                mediaProperties.Title ?? "Unknown",
                mediaProperties.Artist ?? "Unknown",
                session.SourceAppUserModelId ?? "",
                isPlaying,
                pos,
                dur,
                mediaProperties.Thumbnail
            );
        }
        catch { return null; }
    }

    public static async Task TogglePlayPauseAsync()
    {
        try
        {
            var session = await GetSessionAsync();
            if (session is null) return;
            if (session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                await session.TryPauseAsync();
            else
                await session.TryPlayAsync();
        }
        catch { }
    }

    public static async Task NextAsync()
    {
        try
        {
            var session = await GetSessionAsync();
            if (session is not null) await session.TrySkipNextAsync();
        }
        catch { }
    }

    public static async Task PreviousAsync()
    {
        try
        {
            var session = await GetSessionAsync();
            if (session is not null) await session.TrySkipPreviousAsync();
        }
        catch { }
    }
}