using Windows.Media.Control;
using Windows.Storage.Streams;

namespace OmenGamingShell;

public sealed record MediaInfo(string Title, string Artist, string AppName, bool IsPlaying, TimeSpan Position, TimeSpan Duration, IRandomAccessStreamReference? Thumbnail);

public static class MediaService
{
    private static GlobalSystemMediaTransportControlsSessionManager? _manager;
    private static GlobalSystemMediaTransportControlsSession? _session;
    private static Action? _onChanged;

    private static async Task<GlobalSystemMediaTransportControlsSessionManager> GetManagerAsync()
    {
        if (_manager is not null) return _manager;
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.CurrentSessionChanged += OnCurrentSessionChanged;
        _session = _manager.GetCurrentSession();
        AttachSessionEvents();
        return _manager;
    }

    private static void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        _session = sender?.GetCurrentSession();
        AttachSessionEvents();
        _onChanged?.Invoke();
    }

    private static void AttachSessionEvents()
    {
        if (_session is null) return;
        _session.MediaPropertiesChanged += OnSessionChanged;
        _session.TimelinePropertiesChanged += OnSessionChanged;
        _session.PlaybackInfoChanged += OnSessionChanged;
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
        return _session ?? manager.GetCurrentSession();
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
