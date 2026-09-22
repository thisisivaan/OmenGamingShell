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
    Installing,
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
    private string? _setupFilePath;
    private bool _isDetectedRepack;
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

    // Points at setup.exe inside a finished FitGirl repack, letting the Download
    // Center launch the installer for downloads that completed and repacks that
    // were discovered on disk without ever being started from this app.
    public string? SetupFilePath
    {
        get => _setupFilePath;
        set { _setupFilePath = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanInstall)); }
    }

    public bool IsDetectedRepack
    {
        get => _isDetectedRepack;
        set { _isDetectedRepack = value; OnPropertyChanged(); }
    }

    private long _expectedInstallBytes;
    public long ExpectedInstallBytes
    {
        get => _expectedInstallBytes;
        set { _expectedInstallBytes = value; OnPropertyChanged(); }
    }

    private long _expectedDownloadBytes;
    public long ExpectedDownloadBytes
    {
        get => _expectedDownloadBytes;
        set { _expectedDownloadBytes = value; OnPropertyChanged(); }
    }

    private string? _installDir;
    public string? InstallDir
    {
        get => _installDir;
        set { _installDir = value; OnPropertyChanged(); }
    }

    private bool _isInstalling;
    public bool IsInstalling
    {
        get => _isInstalling;
        set { _isInstalling = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanInstall)); }
    }

    public bool IsPaused
    {
        get => _isPaused;
        set { _isPaused = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanPause)); OnPropertyChanged(nameof(CanResume)); }
    }

    public bool CanPause => _status == DownloadStatus.Downloading && !IsPaused;
    public bool CanResume => _status == DownloadStatus.Paused || (_status == DownloadStatus.Downloading && IsPaused);
    public bool CanCancel => _status is DownloadStatus.Downloading or DownloadStatus.Queued or DownloadStatus.Searching or DownloadStatus.Matching or DownloadStatus.Paused && !IsInstalling;
    public bool CanOpenFolder => _status == DownloadStatus.Completed && !string.IsNullOrWhiteSpace(_downloadDir) && !IsInstalling;
    public bool CanInstall => _status == DownloadStatus.Completed && !IsInstalling && !string.IsNullOrWhiteSpace(_setupFilePath);

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
                case DownloadStatus.Installing:
                    StatusText = "Installing..."; StatusIcon = "\xE895"; StatusColor = "#E8B83C"; break;
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

    // Returns every finished FitGirl repack found on disk (folder + setup path),
    // without filtering by install status. Used by the store search so a game that
    // was downloaded but never installed can show a local Install action.
    public static IReadOnlyList<(string Name, string Setup, string Dir)> DiscoverRepacksOnDisk()
    {
        var root = TorrentDownloadService.GetDownloadPath();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return [];
        var found = new List<(string Name, string Setup, string Dir)>();
        try { FindRepackFolders(root, found, isRoot: true); } catch { }
        return found;
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
        item.SetupFilePath ??= DiscoverSetupFile(item.DownloadDir);
        RemoveState(item.Name);
        Notify();
    }

    // Locates setup.exe for a finished repack so an Install button can appear.
    public static string? DiscoverSetupFile(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return null;
        try
        {
            var direct = Directory.EnumerateFiles(dir, "setup*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (direct is not null) return direct;

            // Some torrents unpack into a nested "[FitGirl Repack]" folder.
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (IsHidden(sub)) continue;
                var nested = Directory.EnumerateFiles(sub, "setup*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (nested is not null) return nested;
            }
        }
        catch { }
        return null;
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

    // True when the drive hosting `path` has at least `neededBytes` free.
    // `freeGb` reports the actual free space (GB) for error messages.
    public static bool HasEnoughStorage(string? path, long neededBytes, out double freeGb)
    {
        freeGb = 0;
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return true;
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(root)) return true;
            var drive = new System.IO.DriveInfo(root);
            if (!drive.IsReady) return true;
            freeGb = drive.AvailableFreeSpace / 1024.0 / 1024.0 / 1024.0;
            return drive.AvailableFreeSpace >= neededBytes;
        }
        catch
        {
            return true; // can't measure -> don't block on a false negative
        }
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

        // Storage guard: refuse to start a download that cannot fit, rather than
        // let it fill the drive and fail mid-way. The published download size is
        // the authoritative figure; when it is unavailable we fall back to the
        // install size as an upper-bound sanity check.
        var needed = item.ExpectedDownloadBytes > 0 ? item.ExpectedDownloadBytes : item.ExpectedInstallBytes;
        if (needed > 0 && !HasEnoughStorage(TorrentDownloadService.GetDownloadPath(), needed, out var freeGb))
        {
            item.Status = DownloadStatus.Failed;
            item.StatusText = $"Not enough storage ({freeGb:N2} GB free)";
            item.SpeedText = "";
            item.EtaText = "";
            SaveState();
            Notify();
            FireNotification($"{item.Name} download failed \u2014 not enough storage ({freeGb:N2} GB free)");
            return;
        }

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
                        item.SetupFilePath ??= DiscoverSetupFile(item.DownloadDir);

                        // Stop seeding the moment the download completes, so the
                        // finished torrent stops competing for upload bandwidth with
                        // whatever is downloading next.
                        if (item.TorrentManager is not null)
                            TorrentDownloadService.StopSeeding(item.TorrentManager);

                        RemoveState(item.Name);

                        // Auto-install silently once the repack is on disk,
                        // announced with a single "downloaded, initiated install"
                        // toast so it reads as one seamless step.
                        if (!string.IsNullOrWhiteSpace(item.SetupFilePath))
                        {
                            FireNotification($"{item.Name} downloaded, initiated install");
                            _dispatcher?.BeginInvoke(new Action(() => StartSilentInstall(item, announce: false)));
                        }
                        else
                        {
                            FireNotification($"{item.Name} downloaded");
                            Notify();
                        }
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

    // Launches the repack installer (setup.exe). Used both by downloads that just
    // completed inside this app and by repacks discovered on disk that were never
    // started from here. All installs go through the silent flow now.
    public static void LaunchInstaller(DownloadItem item)
    {
        StartSilentInstall(item);
    }

    // Re-launches a silent install that was interrupted (app closed or rebooted
    // while setup.exe was still extracting). The paused PDF extraction continues
    // from partial data already on disk on the same target folder.
    public static void ResumeInstall(DownloadState state)
    {
        if (string.IsNullOrWhiteSpace(state.Name) ||
            string.IsNullOrWhiteSpace(state.SetupFilePath) ||
            string.IsNullOrWhiteSpace(state.InstallDir)) return;
        if (!File.Exists(state.SetupFilePath)) return;

        DownloadItem item;
        lock (_lock)
        {
            item = _downloads.FirstOrDefault(d =>
                string.Equals(d.SetupFilePath, state.SetupFilePath, StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                item = new DownloadItem
                {
                    Name = state.Name,
                    CoverUrl = state.CoverUrl,
                    IsDetectedRepack = true,
                    DownloadDir = Path.GetDirectoryName(state.SetupFilePath),
                    SetupFilePath = state.SetupFilePath,
                    InstallDir = state.InstallDir,
                    ExpectedInstallBytes = state.ExpectedInstallBytes,
                };
                _downloads.Add(item);
            }
        }
        Notify();
        StartSilentInstall(item, resume: true);
    }

    // Store-side entry point: runs the given setup silently, backed by a tracked
    // DownloadItem so the Download Center shows the Installing state and progress.
    public static void LaunchInstallerFromPath(string setupPath)
    {
        if (string.IsNullOrWhiteSpace(setupPath) || !File.Exists(setupPath)) return;

        DownloadItem? item;
        lock (_lock)
        {
            item = _downloads.FirstOrDefault(d =>
                string.Equals(d.SetupFilePath, setupPath, StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                item = new DownloadItem
                {
                    Name = Path.GetFileName(Path.GetDirectoryName(setupPath)?.TrimEnd(Path.DirectorySeparatorChar)) ?? Path.GetFileName(setupPath),
                    IsDetectedRepack = true,
                    DownloadDir = Path.GetDirectoryName(setupPath),
                    SetupFilePath = setupPath,
                };
                item.Status = DownloadStatus.Completed;
                item.StatusText = "Downloaded - not installed";
                item.StatusIcon = "\xE96E";
                item.StatusColor = "#E8B83C";
                item.ProgressWidth = 420;
                item.ProgressText = "100%";
                _downloads.Add(item);
            }
        }
        Notify();
        StartSilentInstall(item);
    }

    // ------------------------------------------------------------------
    // Silent auto-install.
    //
    // setup.exe in a FitGirl repack is Inno-Setup derived, so it accepts the
    // quiet switches below and honors /DIR to force the destination. We point it
    // at <download-drive>:\Games\<name>, the same folder GameScanner probes, so a
    // finished silent install is picked up by the library automatically. Progress
    // is estimated by watching that folder grow against the repack's published
    // "Install size"; if the expected size is unknown (or an installer ignores
    // /DIR) the bar runs indeterminate until the process exits.
    // ------------------------------------------------------------------

    public static event Action<DownloadItem>? InstallCompleted;
    public static event Action<DownloadItem>? InstallFailed;

    private static readonly Dictionary<string, System.Diagnostics.Process> _installProcesses = new(StringComparer.OrdinalIgnoreCase);

    public static void StartSilentInstall(DownloadItem item, bool resume = false, bool announce = true)
    {
        var setup = item.SetupFilePath;
        if (string.IsNullOrWhiteSpace(setup) || !File.Exists(setup)) return;
        if (item.IsInstalling) return;

        item.InstallDir = ResolveInstallTarget(item);
        item.IsInstalling = true;
        item.Status = DownloadStatus.Installing;
        item.ProgressWidth = 0;
        item.ProgressText = "0%";
        SaveState(); // persist so an interrupted install can be resumed
        Notify();

        // Storage guard: the installer needs room for the full, extracted game on
        // the target drive. The published "after extraction" size is the only
        // authoritative figure; the compressed repack archive is a strict lower
        // bound (extracted data always exceeds the archive) so it can only be a
        // floor, never the requirement, when the true size is unknown.
        var installNeed = item.ExpectedInstallBytes;
        if (installNeed <= 0)
        {
            var archive = EstimateRepackArchiveBytes(Path.GetDirectoryName(setup));
            if (archive > 0)
            {
                // No published size: gate on the archive plus headroom, which is
                // guaranteed-enough to detect a plainly-failing run without
                // pretending the archive size is the real requirement.
                installNeed = (long)(archive * 1.25);
                LogInstall(item, $"Install size unknown; gating on repack size +25% ({installNeed:N0} bytes).");
            }
        }
        if (installNeed > 0 && !HasEnoughStorage(item.InstallDir ?? Path.GetDirectoryName(setup), installNeed, out var freeGb))
        {
            LogInstall(item, $"Install refused: need {installNeed:N0} bytes, only {freeGb:N2} GB free.");
            item.IsInstalling = false;
            item.Status = DownloadStatus.Completed;
            item.StatusText = $"Needs {installNeed / 1024f / 1024f / 1024f:N1} GB, only {freeGb:N1} GB free";
            item.SpeedText = "";
            item.EtaText = "";
            item.StatusIcon = "\xEA39";
            item.StatusColor = "#FF4444";
            item.ProgressWidth = 0;
            RemoveState(item.Name); // nothing in-flight to resume
            Notify();
            FireNotification($"{item.Name} install can't start \u2014 not enough storage ({freeGb:N1} GB free)");
            InstallFailed?.Invoke(item);
            return;
        }

        // A previous, killed install may have left only debris (uninstaller,
        // redistritables, stub folders) in the target. Wipe it before the fresh
        // run so we never "verify" yesterday's broken leftovers as today's game.
        // A resume is the counter-case: the target holds partially-unpacked data
        // that the installer will finish; deleting it would restart from zero.
        if (!resume && !string.IsNullOrWhiteSpace(item.InstallDir) && IsDebrisOnlyFolder(item.InstallDir))
        {
            try { Directory.Delete(item.InstallDir, true); } catch { }
        }

        try
        {
            var dir = Path.GetDirectoryName(setup);
            var args = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-";
            if (!string.IsNullOrWhiteSpace(item.InstallDir))
                args += $" /DIR=\"{item.InstallDir}\"";

            // Elevated launch: the one UAC prompt the user accepted. The installer
            // itself then runs headless with no music, dialogs or window.
            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(setup)
            {
                Arguments = args,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = dir ?? "",
            });
            if (process is null) throw new InvalidOperationException("Process.Start returned null");

            lock (_installProcesses) _installProcesses[item.Id] = process;

            _ = MuteInstallerAudioAsync(item, process);
            _ = MonitorInstallAsync(item, process);
            if (announce)
                FireNotification($"Installing {item.Name}...");
        }
        catch (Exception ex)
        {
            LogInstall(item, $"Failed to start installer: {ex.Message}");
            item.IsInstalling = false;
            item.Status = item.Status == DownloadStatus.Installing ? DownloadStatus.Completed : item.Status;
            item.StatusText = "Install failed to start";
            item.StatusIcon = "\xEA39";
            item.StatusColor = "#FF4444";
            Notify();
            InstallFailed?.Invoke(item);
        }
    }

    // The FitGirl installer plays its own soundtrack via a BASS/ISDone window.
    // Mute that process family's audio session so the machine stays quiet while
    // the install runs. Retries briefly because the session registry appears a
    // heartbeat or two after the process spawns.
    private static async Task MuteInstallerAudioAsync(DownloadItem item, System.Diagnostics.Process process)
    {
        try
        {
            for (int attempt = 0; attempt < 15; attempt++)
            {
                try
                {
                    if (process.HasExited) break;
                }
                catch { break; }

                try
                {
                    var muted = AudioSessionHelper.MuteProcessFamily(process.Id);
                    if (muted > 0)
                    {
                        LogInstall(item, $"Muted {muted} installer audio session(s).");
                        return;
                    }
                }
                catch { }

                await Task.Delay(1000);
            }
            LogInstall(item, "No installer audio session found to mute.");
        }
        catch { }
    }

    private static async Task MonitorInstallAsync(DownloadItem item, System.Diagnostics.Process process)
    {
        var expected = item.ExpectedInstallBytes;
        var target = item.InstallDir;

        // Published "Install size" is often missing or in an unexpected layout
        // (FitGirl posts vary). Fall back to the sum of the repack's fg-*.bin
        // archives so the bar still reflects real bytes unzipped against disk.
        if (expected <= 0 && !string.IsNullOrWhiteSpace(item.SetupFilePath))
        {
            expected = EstimateRepackArchiveBytes(Path.GetDirectoryName(item.SetupFilePath));
            item.ExpectedInstallBytes = expected;
            LogInstall(item, $"No published size; using archive estimate: {expected:N0} bytes");
        }

        LogInstall(item, $"Installing to '{target}' expected={expected:N0} bytes");
        await Task.Delay(2500); // let the installer spin up and create folders

        long lastSize = 0;
        var lastProbe = DateTime.UtcNow;

        while (true)
        {
            try
            {
                if (process.HasExited)
                {
                    LogInstall(item, $"Installer exited (code {(process.ExitCode)}). Verifying result...");
                    await Task.Delay(1500); // flush pending writes on exit
                    break;
                }
            }
            catch { break; }

            if (expected > 0 && !string.IsNullOrWhiteSpace(target))
            {
                try
                {
                    var size = await Task.Run(() => FolderSize(target));
                    var now = DateTime.UtcNow;
                    var progress = Math.Min(0.99, size / (double)expected);
                    var elapsed = (now - lastProbe).TotalSeconds;
                    long speed = 0;
                    if (elapsed >= 0.6 && size > lastSize)
                        speed = (long)((size - lastSize) / elapsed);
                    lastSize = size;
                    lastProbe = now;

                    var pct = (int)(progress * 100);
                    var eta = speed > 0
                        ? TimeSpan.FromSeconds(Math.Max(0, (expected - size) / (double)speed))
                        : TimeSpan.Zero;

                    _dispatcher?.BeginInvoke(new Action(() =>
                    {
                        item.ProgressWidth = progress * 420;
                        item.ProgressText = $"{pct}%";
                        item.SizeText = $"{TorrentDownloadService.FormatSize(size)} / {TorrentDownloadService.FormatSize(expected)}";
                        if (speed > 0)
                        {
                            item.SpeedText = TorrentDownloadService.FormatSpeed(speed);
                            item.EtaText = TorrentDownloadService.FormatEta(eta);
                            item.StatusText = $"Installing... {item.SpeedText}  \u2022  ETA {item.EtaText}";
                        }
                        else
                        {
                            item.StatusText = $"Installing... {pct}%";
                        }
                    }));
                }
                catch { }
            }

            try { await Task.Delay(700); } catch { break; }
            if (!item.IsInstalling) return; // cancelled / removed while running
        }

        var exitCode = 0;
        try { exitCode = process.ExitCode; } catch { }
        var installed = await VerifyInstallAsync(item, expected, exitCode);
        if (installed) CompleteInstall(item);
        else CompleteInstallFailed(item, exitCode);
    }

    // The installer exiting at all is not proof of success (killed, skipped,
    // "not enough disk" hard-exits all produce a clean exit). We only claim
    // Installed when the target folder holds a real game exe alongside a
    // meaningful amount of data.
    private static async Task<bool> VerifyInstallAsync(DownloadItem item, long expected, int exitCode)
    {
        var target = item.InstallDir;
        if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target)) return false;

        var size = await Task.Run(() => FolderSize(target));
        var gameExe = await Task.Run(() => FindGameExecutable(target));
        var minSize = Math.Max(50L * 1024 * 1024, (long)(expected * 0.3));
        var hasExe = gameExe is not null;
        var hasBulk = expected <= 0 || size >= minSize;

        LogInstall(item, $"Verify: size={size:N0} min={minSize:N0} exe={gameExe ?? "none"} exit={exitCode}");
        return hasExe && hasBulk;
    }

    // Finds a "real" game executable, excluding installers/uninstallers and
    // utility helpers the setup drops into the target folder.
    private static string? FindGameExecutable(string folder)
    {
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "unins000", "unins001", "setup", "install", "redist", "vcredist", "vc_redist", "run",
        };
        try
        {
            var pending = new Queue<string>();
            pending.Enqueue(folder);
            while (pending.Count > 0)
            {
                var current = pending.Dequeue();
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(current, "*.exe").ToList(); }
                catch { files = Array.Empty<string>(); }
                foreach (var file in files)
                {
                    if (blocked.Contains(Path.GetFileNameWithoutExtension(file))) continue;
                    var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                    if (name.Contains("crash") || name.Contains("redist") || name.Contains("repair")) continue;
                    if (name.Contains("launcher") && !name.Contains("vulkan")) continue;

                    // an exe at the folder root or inside a bin/exec subfolder is
                    // overwhelmingly the game binary rather than a support tool
                    var relative = Path.GetRelativePath(folder, file);
                    if (relative.IndexOf(Path.DirectorySeparatorChar) < 0) return file;
                    if (relative.StartsWith("bin") || relative.StartsWith("exec")) return file;
                }
                IEnumerable<string> subdirs;
                try { subdirs = Directory.EnumerateDirectories(current).ToList(); }
                catch { subdirs = Array.Empty<string>(); }
                foreach (var sub in subdirs)
                {
                    var name = Path.GetFileName(sub).ToLowerInvariant();
                    if (name is "_redist" or "_commonredist" or "redist" or "_bypass")
                        continue;
                    pending.Enqueue(sub);
                }
            }
        }
        catch { }
        return null;
    }

    // True when a folder cannot be a completed install: no game executable
    // anywhere in it. A killed fitgirl run leaves either pure debris (uninstaller,
    // redistritables, stubs) or a partially-unpacked tree with no exe yet — both
    // would fake the progress bar (counted bytes with zero install work) and
    // could confuse verification, so they must be wiped before a fresh install.
    private static bool IsDebrisOnlyFolder(string folder)
    {
        if (!Directory.Exists(folder)) return false;
        try
        {
            return FindGameExecutable(folder) is null;
        }
        catch { return false; }
    }

    private static long EstimateRepackArchiveBytes(string? repackDir)
    {
        if (string.IsNullOrWhiteSpace(repackDir) || !Directory.Exists(repackDir)) return 0;
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(repackDir, "*.bin"))
            {
                try { total += new FileInfo(file).Length; } catch { }
            }
        }
        catch { }
        return total;
    }

    private static void CompleteInstallFailed(DownloadItem item, int exitCode)
    {
        _dispatcher?.BeginInvoke(new Action(() =>
        {
            item.IsInstalling = false;
            item.Status = DownloadStatus.Completed;
            item.StatusText = exitCode == 0 ? "Install failed" : $"Install failed (code {exitCode})";
            item.StatusIcon = "\xEA39";
            item.StatusColor = "#FF4444";
            item.ProgressWidth = 420;
            item.ProgressText = "100%";
            // keep SetupFilePath so the Install button remains for a retry
            lock (_installProcesses) _installProcesses.Remove(item.Id);
            RemoveState(item.Name); // don't auto-resume a failed install
            Notify();
            LogInstall(item, $"Install FAILED (exit code {exitCode}); game not found in target folder.");
            FireNotification($"{item.Name} install failed");
            InstallFailed?.Invoke(item);
        }));
    }

    private static void CompleteInstall(DownloadItem item)
    {
        _dispatcher?.BeginInvoke(new Action(() =>
        {
            item.IsInstalling = false;
            item.Status = DownloadStatus.Completed;
            item.StatusText = "Installed";
            item.StatusIcon = "\xE73E";
            item.StatusColor = "#24C486";
            item.ProgressWidth = 420;
            item.ProgressText = "100%";
            item.SetupFilePath = null; // hide the install button now it is done
            lock (_installProcesses) _installProcesses.Remove(item.Id);
            RemoveState(item.Name); // no longer an in-progress install to resume
            LogInstall(item, "Install complete.");
            FireNotification($"{item.Name} installed successfully");
            InstallCompleted?.Invoke(item);
            // The game now lives in the library, so the Download Center row has
            // nothing left to track; drop it so the card clears after install.
            lock (_lock) _downloads.Remove(item);
            Notify();
        }));
    }

    // Picks where a silent install lands. Fits the GameScanner convention of
    // scanning X:\Games for standalone game folders.
    private static string? ResolveInstallTarget(DownloadItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.InstallDir)) return item.InstallDir;
        try
        {
            var root = TorrentDownloadService.GetDownloadPath();
            if (string.IsNullOrWhiteSpace(root)) return null;
            var drive = Path.GetPathRoot(Path.GetFullPath(root));
            if (string.IsNullOrWhiteSpace(drive)) return null;
            return Path.Combine(drive, "Games", SanitizeFileName(item.Name));
        }
        catch { return null; }
    }

    private static long FolderSize(string dir)
    {
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; } catch { }
            }
        }
        catch { }
        return total;
    }

    private static void LogInstall(DownloadItem item, string message)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OmenGamingShell", "Logs");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "installs.log"),
                $"[{DateTime.Now:HH:mm:ss}] [{item.Name}] {message}\n");
        }
        catch { }
    }

    // Scans the download root for finished FitGirl repacks that were never
    // installed (a folder containing setup.exe whose game is not in the library).
    // They surface in the Download Center as "Downloaded - not installed" with an
    // Install button, without ever having been a tracked download here.
    public static void ScanForExistingRepacks(IReadOnlyList<string> installedNames)
    {
        var root = TorrentDownloadService.GetDownloadPath();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;

        var installedCores = installedNames
            .Select(FitGirlScrapingService.NormalizeName)
            .Where(c => c.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var found = new List<(string Name, string Setup, string Dir)>();
        try
        {
            FindRepackFolders(root, repos: found, isRoot: true);
        }
        catch { }

        // Drop rows for repacks whose game has since been installed, and reclaim
        // the disk by deleting those repack folders entirely.
        var reclaimed = false;
        lock (_lock)
        {
            foreach (var stale in _downloads
                .Where(d => d.IsDetectedRepack &&
                    installedCores.Contains(FitGirlScrapingService.NormalizeName(d.Name)))
                .ToList())
            {
                _downloads.Remove(stale);
                if (TryDeleteRepackFolder(stale.DownloadDir, stale.Name)) reclaimed = true;
            }
        }

        var added = false;
        foreach (var (name, setup, dir) in found)
        {
            var core = FitGirlScrapingService.NormalizeName(name);
            if (core.Length > 0 && installedCores.Contains(core))
            {
                if (TryDeleteRepackFolder(dir, name)) reclaimed = true;
                continue; // game is already installed
            }

            lock (_lock)
            {
                // Skip if an existing item already tracks this exact repack, or if
                // the repack folder lives inside a tracked download's folder (the
                // torrent may still be writing into a parent directory).
                var handsOff = _downloads.Any(d =>
                    !string.IsNullOrWhiteSpace(d.DownloadDir) &&
                    (string.Equals(d.DownloadDir, dir, StringComparison.OrdinalIgnoreCase) ||
                     dir.StartsWith(d.DownloadDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                     d.DownloadDir.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
                if (handsOff) continue;

                if (_downloads.Any(d => string.Equals(d.SetupFilePath, setup, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var item = new DownloadItem
                {
                    Name = name,
                    IsDetectedRepack = true,
                    DownloadDir = dir,
                    SetupFilePath = setup,
                };
                item.Status = DownloadStatus.Completed;
                item.StatusText = "Downloaded - not installed";
                item.StatusIcon = "\xE96E";
                item.StatusColor = "#E8B83C";
                item.ProgressWidth = 420;
                item.ProgressText = "100%";
                _downloads.Add(item);
                added = true;
            }
        }

        if (added || reclaimed) Notify();
    }

    // Deletes a discovered repack folder once its game is installed, freeing the
    // disk. Only ever removes folders that were found by FindRepackFolders (i.e.
    // non-root, not hands-off, repack-like folders), so it can't touch the
    // download root or a still-running download's directory.
    private static bool TryDeleteRepackFolder(string? dir, string? displayName)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;

        var root = TorrentDownloadService.GetDownloadPath();
        if (!string.IsNullOrWhiteSpace(root) &&
            string.Equals(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar),
                          Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar),
                          StringComparison.OrdinalIgnoreCase))
            return false; // never delete the download root

        lock (_lock)
        {
            var handsOff = _downloads.Any(d =>
                !string.IsNullOrWhiteSpace(d.DownloadDir) &&
                (string.Equals(d.DownloadDir, dir, StringComparison.OrdinalIgnoreCase) ||
                 dir.StartsWith(d.DownloadDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                 d.DownloadDir.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
            if (handsOff) return false;
        }

        try
        {
            Directory.Delete(dir, true);
            FireNotification($"Deleted repack folder for {displayName ?? Path.GetFileName(dir)}");
            return true;
        }
        catch
        {
            return false; // in use / locked; leave it for a later scan
        }
    }

    private static void FindRepackFolders(string dir, List<(string Name, string Setup, string Dir)> repos, bool isRoot = false)
    {
        if (Directory.Exists(dir))
        {
            var setup = Directory.EnumerateFiles(dir, "setup.exe", SearchOption.TopDirectoryOnly).FirstOrDefault()
                        ?? Directory.EnumerateFiles(dir, "setup_*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (setup is not null && !isRoot)
            {
                repos.Add((DeriveRepackName(dir), setup, dir));
                return; // a repack folder may still hold useful subfolders, but
                        // nested repacks are vanishingly rare; stop here.
            }
        }

        if (!isRoot && IsRepackLike(dir)) return; // don't descend into other repacks' guts

        try
        {
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (IsHidden(sub)) continue;
                FindRepackFolders(sub, repos, isRoot: false);
            }
        }
        catch { }
    }

    private static bool IsRepackLike(string dir)
    {
        var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return name.Contains("FitGirl", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Repack", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("DODI", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("[v", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHidden(string dir)
    {
        try { return (File.GetAttributes(dir) & FileAttributes.Hidden) != 0 ||
                     (File.GetAttributes(dir) & FileAttributes.System) != 0; }
        catch { return false; }
    }

    // "God of War Ragnarok [FitGirl Repack]" => "God of War Ragnarok".
    private static string DeriveRepackName(string dir)
    {
        var folder = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var name = System.Text.RegularExpressions.Regex.Replace(
            folder, @"\s*\[[^\]]*\]\s*", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        name = System.Text.RegularExpressions.Regex.Replace(name, @"\s+", " ").Trim();
        return string.IsNullOrWhiteSpace(name) ? folder : name;
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
                                (d.Status == DownloadStatus.Failed && !string.IsNullOrWhiteSpace(d.MagnetUri)) ||
                                // An interrupted silent install: re-launchable against the same
                                // target once the app restarts.
                                (d.IsInstalling && !string.IsNullOrWhiteSpace(d.SetupFilePath)))
                    .Select(d => new DownloadState
                    {
                        Name = d.Name,
                        CoverUrl = d.CoverUrl,
                        MagnetUri = d.MagnetUri,
                        DownloadDir = d.DownloadDir,
                        RetryCount = d.RetryCount,
                        SetupFilePath = d.IsInstalling ? d.SetupFilePath : null,
                        InstallDir = d.IsInstalling ? d.InstallDir : null,
                        ExpectedInstallBytes = d.IsInstalling ? d.ExpectedInstallBytes : 0,
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

    // A silent install that was interrupted (app closed / rebooted mid-extract).
    // Kept so AutoResume can re-launch the same setup against the same target.
    public string? SetupFilePath { get; set; }
    public string? InstallDir { get; set; }
    public long ExpectedInstallBytes { get; set; }
}
