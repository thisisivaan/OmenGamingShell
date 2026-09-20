using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;
using csdl;
using csdl.Enums;

namespace OmenGamingShell;

public enum DownloadStatus
{
    Searching,
    Matching,
    Queued,
    Downloading,
    Completed,
    Failed,
    Paused
}

public class DownloadItem : INotifyPropertyChanged
{
    private string _name = "";
    private string _statusText = "Queued";
    private string _statusIcon = "\xE945";
    private string _statusColor = "#909090";
    private double _progressWidth;
    private DownloadStatus _status = DownloadStatus.Queued;
    private string? _coverUrl;
    private string _speedText = "";
    private string _etaText = "";
    private string _sizeText = "";
    private string _progressText = "0%";
    private bool _isPaused;
    private string? _downloadDir;
    private string? _magnetUri;
    public csdl.TorrentManager? TorrentManager { get; set; }

    public string Name { get => _name; set { _name = value; OnPropertyChanged(); } }
    public string StatusText { get => _statusText; set { _statusText = value; OnPropertyChanged(); } }
    public string StatusIcon { get => _statusIcon; set { _statusIcon = value; OnPropertyChanged(); } }
    public string StatusColor { get => _statusColor; set { _statusColor = value; OnPropertyChanged(); } }
    public double ProgressWidth { get => _progressWidth; set { _progressWidth = value; OnPropertyChanged(); } }
    public string SpeedText { get => _speedText; set { _speedText = value; OnPropertyChanged(); } }
    public string EtaText { get => _etaText; set { _etaText = value; OnPropertyChanged(); } }
    public string SizeText { get => _sizeText; set { _sizeText = value; OnPropertyChanged(); } }
    public string ProgressText { get => _progressText; set { _progressText = value; OnPropertyChanged(); } }
    public string? CoverUrl { get => _coverUrl; set { _coverUrl = value; OnPropertyChanged(); } }
    public string? DownloadDir { get => _downloadDir; set { _downloadDir = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanOpenFolder)); } }
    public string? MagnetUri { get => _magnetUri; set { _magnetUri = value; } }
    public int RetryCount { get; set; }

    public bool IsPaused
    {
        get => _isPaused;
        set { _isPaused = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanPause)); OnPropertyChanged(nameof(CanResume)); }
    }

    public bool CanPause => _status == DownloadStatus.Downloading && !IsPaused;
    public bool CanResume => _status == DownloadStatus.Paused || (_status == DownloadStatus.Downloading && IsPaused);
    public bool CanCancel => _status is DownloadStatus.Downloading or DownloadStatus.Queued or DownloadStatus.Searching or DownloadStatus.Matching or DownloadStatus.Paused;
    public bool CanOpenFolder => _status == DownloadStatus.Completed && !string.IsNullOrWhiteSpace(_downloadDir);

    public DownloadStatus Status
    {
        get => _status;
        set
        {
            _status = value;
            switch (value)
            {
                case DownloadStatus.Searching:
                    StatusText = "Searching FitGirl Repacks..."; StatusIcon = "\xE721"; StatusColor = "#9B6BFF"; break;
                case DownloadStatus.Matching:
                    StatusText = "Verifying match..."; StatusIcon = "\xE73E"; StatusColor = "#9B6BFF"; break;
                case DownloadStatus.Queued:
                    StatusText = "Queued"; StatusIcon = "\xE945"; StatusColor = "#909090"; break;
                case DownloadStatus.Downloading:
                    StatusIcon = "\xE895"; StatusColor = "#E8B83C"; break;
                case DownloadStatus.Paused:
                    StatusText = "Paused"; StatusIcon = "\xE769"; StatusColor = "#909090"; break;
                case DownloadStatus.Completed:
                    StatusText = "Completed"; StatusIcon = "\xE73E"; StatusColor = "#24C486"; break;
                case DownloadStatus.Failed:
                    StatusIcon = "\xEA39"; StatusColor = "#FF4444"; break;
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanPause));
            OnPropertyChanged(nameof(CanResume));
            OnPropertyChanged(nameof(CanCancel));
            OnPropertyChanged(nameof(CanOpenFolder));
        }
    }

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class DownloadCenterService
{
    private static readonly List<DownloadItem> _downloads = new();
    private static readonly object _lock = new();
    private static Dispatcher? _dispatcher;
    private static System.Windows.Threading.DispatcherTimer? _watchdogTimer;

    public static event Action? DownloadsChanged;
    public static event Action<string>? NotificationRequested;

    public static void Init(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        if (_watchdogTimer is null)
        {
            _watchdogTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(30)
            };
            _watchdogTimer.Tick += (_, _) => WatchdogTick();
            _watchdogTimer.Start();
        }
    }

    private static void WatchdogTick()
    {
        List<DownloadItem> toRetry;
        lock (_lock)
        {
            toRetry = _downloads
                .Where(d => d.Status == DownloadStatus.Failed && d.MagnetUri is not null && d.RetryCount < 5)
                .ToList();
        }
        foreach (var item in toRetry)
        {
            item.RetryCount++;
            TorrentDownloadService.Log($"[{item.Name}] Auto-retrying after failure (attempt {item.RetryCount}/5)");
            item.Status = DownloadStatus.Queued;
            item.StatusText = "Retrying...";
            _ = StartDownloadAsync(item, item.MagnetUri!);
        }
    }

    public static IReadOnlyList<DownloadItem> GetDownloads()
    {
        lock (_lock) return _downloads.ToArray();
    }

    public static DownloadItem AddDownload(string name, string? coverUrl = null)
    {
        var item = new DownloadItem { Name = name, CoverUrl = coverUrl, Status = DownloadStatus.Queued };
        lock (_lock) _downloads.Add(item);
        Notify();
        return item;
    }

    public static void UpdateProgress(DownloadItem item, double progress01, string? statusText = null)
    {
        item.ProgressWidth = progress01 * 420;
        item.ProgressText = $"{(int)(progress01 * 100)}%";
        if (statusText != null) item.StatusText = statusText;
        if (item.Status != DownloadStatus.Downloading)
            item.Status = DownloadStatus.Downloading;
    }

    public static void UpdateSpeed(DownloadItem item, string speedText, string etaText, string sizeText)
    {
        item.SpeedText = speedText;
        item.EtaText = etaText;
        item.SizeText = sizeText;
        if (item.Status == DownloadStatus.Downloading)
            item.StatusText = $"{speedText}  •  ETA {etaText}";
    }

    public static void Complete(DownloadItem item)
    {
        item.Status = DownloadStatus.Completed;
        item.ProgressWidth = 420;
        item.ProgressText = "100%";
        item.SpeedText = "";
        item.EtaText = "";
        RemoveState(item.Name);
        Notify();
    }

    public static void Fail(DownloadItem item, string? reason = null)
    {
        item.Status = DownloadStatus.Failed;
        item.StatusText = reason ?? "Failed";
        item.SpeedText = "";
        item.EtaText = "";
        SaveState();
        Notify();
    }

    public static void Remove(DownloadItem item)
    {
        lock (_lock) _downloads.Remove(item);
        Notify();
    }

    public static int ActiveCount
    {
        get { lock (_lock) return _downloads.Count(d => d.Status is DownloadStatus.Downloading or DownloadStatus.Queued); }
    }

    public static async Task StartDownloadAsync(DownloadItem item, string magnetUri, string? torrentFileUrl = null)
    {
        ReleaseDuplicateManagers(item, magnetUri);

        var downloadDir = Path.Combine(
            TorrentDownloadService.GetDownloadPath(),
            SanitizeFileName(item.Name));
        Directory.CreateDirectory(downloadDir);
        item.DownloadDir = downloadDir;
        item.MagnetUri = magnetUri;

        item.Status = DownloadStatus.Downloading;
        item.StatusText = "Connecting...";
        SaveState();

        try
        {
            TorrentManager? manager = null;

            if (!string.IsNullOrEmpty(torrentFileUrl))
            {
                try
                {
                    item.StatusText = "Downloading .torrent file...";
                    Notify();
                    var torrentData = await FitGirlScrapingService.DownloadTorrentFileAsync(torrentFileUrl);
                    if (torrentData is not null && torrentData.Length > 100)
                    {
                        manager = await TorrentDownloadService.StartDownloadFromTorrentFileAsync(
                            torrentData, downloadDir,
                            mgr =>
                            {
                                item.TorrentManager = mgr;
                                item.Status = DownloadStatus.Downloading;
                            });
                    }
                }
                catch { }
            }

            if (manager is null)
            {
                var infoHash = ExtractInfoHash(magnetUri);
                if (infoHash is not null)
                {
                    item.StatusText = "Fetching .torrent from cache...";
                    Notify();
                    var success = await TorrentDownloadService.TryDownloadTorrentFileAsync(
                        infoHash, downloadDir,
                        mgr =>
                        {
                            item.TorrentManager = mgr;
                            item.Status = DownloadStatus.Downloading;
                        });
                    if (!success) manager = null;
                    else manager = item.TorrentManager;
                }
            }

            if (manager is null)
            {
                manager = await TorrentDownloadService.StartDownloadAsync(
                    magnetUri, downloadDir,
                    mgr =>
                    {
                        item.TorrentManager = mgr;
                        item.Status = DownloadStatus.Downloading;
                    });
            }

            var progressTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };

            progressTimer.Tick += (_, _) =>
            {
                try
                {
                    if (item.TorrentManager is null || item.IsPaused) return;
                    var (progress, speed, downloaded, _, peers, _) =
                        TorrentDownloadService.GetStats(item.TorrentManager);

                    var status = item.TorrentManager.GetCurrentStatus();
                    var state = status.State;
                    item.ProgressWidth = progress * 420;
                    item.ProgressText = $"{(int)(progress * 100)}%";
                    item.SpeedText = TorrentDownloadService.FormatSpeed(speed);
                    item.SizeText = TorrentDownloadService.FormatSize(downloaded);

                    if (speed > 0)
                    {
                        var remainingBytes = progress > 0.001 ? (long)((1.0 - progress) / progress * downloaded) : 0;
                        var eta = remainingBytes > 0 ? TimeSpan.FromSeconds((double)remainingBytes / speed) : TimeSpan.Zero;
                        item.EtaText = TorrentDownloadService.FormatEta(eta);
                        item.StatusText = $"{item.SpeedText}  \u2022  ETA {item.EtaText}  \u2022  {peers} peers";
                    }
                    else
                    {
                        item.StatusText = $"Finding peers... ({peers} connected)";
                    }

                    if (state == TorrentState.Seeding ||
                        state == TorrentState.Finished)
                    {
                        progressTimer.Stop();
                        item.ProgressWidth = 420;
                        item.ProgressText = "100%";
                        item.Status = DownloadStatus.Completed;
                        item.StatusText = "Downloaded";
                        item.SpeedText = "";
                        item.EtaText = "";

                        // Stop seeding the moment the download completes, so the
                        // finished torrent stops competing for upload bandwidth with
                        // whatever is downloading next.
                        if (item.TorrentManager is not null)
                            TorrentDownloadService.StopSeeding(item.TorrentManager);

                        RemoveState(item.Name);
                        FireNotification($"{item.Name} downloaded");
                        Notify();
                    }
                }
                catch { }
            };

            progressTimer.Start();
            Notify();
        }
        catch (Exception ex)
        {
            Fail(item, $"Error: {ex.Message}");
        }
    }

    // Only one manager may exist per torrent. Without this, re-clicking download
    // after a stall, or the watchdog retry, leaves the previous manager attached:
    // both then connect to the same peers and split the available speed between
    // them, which looks exactly like "this app downloads slower than qBittorrent".
    private static void ReleaseDuplicateManagers(DownloadItem candidate, string magnetUri)
    {
        var infoHash = ExtractInfoHash(magnetUri);
        if (infoHash is null) return;

        List<DownloadItem> duplicates;
        lock (_lock)
        {
            duplicates = _downloads
                .Where(d => !ReferenceEquals(d, candidate) && d.TorrentManager is not null)
                .Where(d => string.Equals(ExtractInfoHash(d.MagnetUri ?? string.Empty), infoHash,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        foreach (var duplicate in duplicates)
        {
            TorrentDownloadService.Log(
                $"[{duplicate.Name}] Releasing the previous manager for this torrent so it stops competing");
            TorrentDownloadService.ReleaseTorrent(duplicate.TorrentManager!);
            duplicate.TorrentManager = null;
            lock (_lock) _downloads.Remove(duplicate);
            Notify();
        }
    }

    public static void PauseDownload(DownloadItem item)
    {
        if (item.TorrentManager is not null)
        {
            TorrentDownloadService.PauseDownload(item.TorrentManager);
            item.IsPaused = true;
            item.Status = DownloadStatus.Paused;
            item.StatusText = "Paused";
            item.SpeedText = "";
            item.EtaText = "";
            Notify();
        }
    }

    public static void ResumeDownload(DownloadItem item)
    {
        if (item.TorrentManager is not null)
        {
            TorrentDownloadService.ResumeDownload(item.TorrentManager);
            item.IsPaused = false;
            item.Status = DownloadStatus.Downloading;
            Notify();
        }
    }

    // Hands the finished download to Explorer and stops there. The shell does not
    // run, install, or delete anything inside the download folder.
    public static void OpenDownloadFolder(DownloadItem item)
    {
        var dir = item.DownloadDir;
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir)
            {
                UseShellExecute = true,
            });
        }
        catch { }
    }

    public static void CancelDownload(DownloadItem item)
    {
        if (item.TorrentManager is not null)
        {
            TorrentDownloadService.CancelDownload(item.TorrentManager);
            item.TorrentManager = null;
        }
        if (item.DownloadDir is not null)
        {
            try { Directory.Delete(item.DownloadDir, true); } catch { }
        }
        RemoveState(item.Name);
        Remove(item);
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }

    private static string? ExtractInfoHash(string magnetUri)
    {
        var match = System.Text.RegularExpressions.Regex.Match(magnetUri,
            @"btih:([a-fA-F0-9]{40}|[a-fA-F0-9]{32})",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string StateFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OmenGamingShell", "active_downloads.json");

    private static void SaveState()
    {
        try
        {
            List<DownloadState> states;
            lock (_lock)
            {
                states = _downloads
                    .Where(d => (d.Status is DownloadStatus.Downloading or DownloadStatus.Paused or DownloadStatus.Queued
                                 or DownloadStatus.Searching or DownloadStatus.Matching) ||
                                // A failure is only worth persisting if it can be retried later,
                                // which needs a magnet. Search-stage failures have none, so they
                                // would sit in the state file forever as un-resumable junk.
                                (d.Status == DownloadStatus.Failed && !string.IsNullOrWhiteSpace(d.MagnetUri)))
                    .Select(d => new DownloadState
                    {
                        Name = d.Name,
                        CoverUrl = d.CoverUrl,
                        MagnetUri = d.MagnetUri,
                        DownloadDir = d.DownloadDir,
                        RetryCount = d.RetryCount,
                    })
                    .ToList();
            }

            var folder = Path.GetDirectoryName(StateFilePath)!;
            Directory.CreateDirectory(folder);
            File.WriteAllText(StateFilePath, JsonSerializer.Serialize(states, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public static List<DownloadState> LoadState()
    {
        try
        {
            if (!File.Exists(StateFilePath)) return new();
            var json = File.ReadAllText(StateFilePath);
            return JsonSerializer.Deserialize<List<DownloadState>>(json) ?? new();
        }
        catch { return new(); }
    }

    private static void RemoveState(string name)
    {
        try
        {
            if (!File.Exists(StateFilePath)) return;
            var json = File.ReadAllText(StateFilePath);
            var states = JsonSerializer.Deserialize<List<DownloadState>>(json) ?? new();
            states.RemoveAll(s => s.Name == name);
            File.WriteAllText(StateFilePath, JsonSerializer.Serialize(states, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private static void Notify()
    {
        if (_dispatcher != null)
            _dispatcher.BeginInvoke(() => DownloadsChanged?.Invoke());
        else
            DownloadsChanged?.Invoke();
    }

    private static void FireNotification(string message)
    {
        if (_dispatcher != null)
            _dispatcher.BeginInvoke(() => NotificationRequested?.Invoke(message));
        else
            NotificationRequested?.Invoke(message);
    }
}

public class DownloadState
{
    public string Name { get; set; } = "";
    public string? CoverUrl { get; set; }
    public string? MagnetUri { get; set; }
    public string? DownloadDir { get; set; }
    public int RetryCount { get; set; }
}
