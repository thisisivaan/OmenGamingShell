using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using csdl;
using csdl.Enums;

namespace OmenGamingShell;

public static class TorrentDownloadService
{
    private static TorrentClient? _client;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static string _downloadPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    private static string SettingsFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OmenGamingShell", "download_settings.json");

    static TorrentDownloadService()
    {
        LoadSettings();
    }

    private static void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsFilePath)) return;
            var json = File.ReadAllText(SettingsFilePath);
            var s = JsonSerializer.Deserialize<DownloadSettings>(json);
            if (s is not null)
            {
                if (!string.IsNullOrWhiteSpace(s.DownloadPath) && Directory.Exists(s.DownloadPath))
                    _downloadPath = s.DownloadPath;
            }
        }
        catch { }
    }

    private static void SaveSettings()
    {
        try
        {
            var folder = Path.GetDirectoryName(SettingsFilePath)!;
            Directory.CreateDirectory(folder);
            var json = JsonSerializer.Serialize(new DownloadSettings { DownloadPath = _downloadPath },
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFilePath, json);
        }
        catch { }
    }

    public static string GetDownloadPath() => _downloadPath;

    public static void SetDownloadPath(string path)
    {
        _downloadPath = path;
        Directory.CreateDirectory(path);
        SaveSettings();
        if (_client is not null)
            _client.DefaultDownloadPath = path;
    }

    public static void Log(string msg)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OmenGamingShell", "Logs");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "downloads.log"),
                $"[{DateTime.Now:HH:mm:ss}] [Torrent] {msg}\n", Encoding.UTF8);
        }
        catch { }
    }

    public static TorrentClient GetClientAsync()
    {
        if (_client is not null) return _client;

        Directory.CreateDirectory(_downloadPath);

        var config = new TorrentClientConfig
        {
            ForceEncryption = false,
            MaxConnections = 500,
            UserAgent = "OmenGamingShell/1.0 libtorrent/2.0",
        };

        _client = new TorrentClient(config);
        _client.DefaultDownloadPath = _downloadPath;

        ApplySpeedSettings(_client);

        Log("csdl/libtorrent session created");
        return _client;
    }

    // Session tuning. Every key below is validated against the libtorrent build
    // shipped by csdl.Native: csdl throws ArgumentException on an unknown key, so
    // keys are applied individually and reported. Previously the whole pack sat in
    // one try/catch, and a single bad key (checking_mode, use_disk_cache,
    // disk_cache_size, recv_buffer_watermark - all removed upstream in libtorrent
    // 1.2/2.0) aborted the loop before UpdateSettings ran, silently discarding
    // every other setting and leaving the session on stock defaults.
    private static void ApplySpeedSettings(TorrentClient client)
    {
        var settings = new (string Name, object Value)[]
        {
            // Peer discovery - the biggest lever on public swarms.
            ("enable_dht", true),
            ("enable_lsd", true),
            ("enable_natpmp", true),
            ("enable_upnp", true),
            ("dht_announce_interval", 30),
            ("announce_to_all_trackers", true),
            ("announce_to_all_tiers", true),
            ("max_peerlist_size", 5000),
            ("connections_limit", 500),
            ("torrent_connect_boost", 100),

            // Connect to more peers up front, then churn out the stalled ones
            // instead of waiting on them.
            ("enable_incoming_utp", true),
            ("enable_outgoing_utp", true),
            ("handshake_timeout", 5),
            ("inactivity_timeout", 20),
            ("peer_turnover", 2),
            ("peer_turnover_cutoff", 40),
            ("peer_turnover_interval", 5),
            ("max_failcount", 3),
            ("min_reconnect_time", 5),
            ("allow_multiple_connections_per_ip", true),
            ("tracker_receive_timeout", 5),
            ("stop_tracker_timeout", 5),

            // Request pipelining and socket buffers.
            ("max_out_request_queue", 500),
            ("request_timeout", 10),
            ("peer_timeout", 20),
            ("urlseed_timeout", 10),
            ("whole_pieces_threshold", 4),
            ("send_buffer_watermark", 512 * 1024),
            ("send_buffer_low_watermark", 32 * 1024),
            ("max_peer_recv_buffer_size", 2 * 1024 * 1024),
            ("max_http_recv_buffer_size", 1024 * 1024),

            // Disk I/O. libtorrent 2.0 replaced the user-space cache with mmap-based
            // reads, so the old use_disk_cache/disk_cache_size pair no longer exists;
            // max_queued_disk_bytes is the surviving write-coalescing knob.
            ("aio_threads", 8),
            ("no_atime_storage", true),
            ("max_queued_disk_bytes", 32 * 1024 * 1024),

            ("seed_choking_algorithm", 1),
        };

        var pack = new SettingsPack();
        var applied = 0;
        var rejected = new List<string>();

        foreach (var (name, value) in settings)
        {
            try
            {
                if (value is bool flag) pack.Set(name, flag);
                else pack.Set(name, (int)value);
                applied++;
            }
            catch (Exception ex)
            {
                rejected.Add($"{name} ({ex.Message})");
            }
        }

        try
        {
            client.UpdateSettings(pack);
            Log($"libtorrent tuning applied: {applied}/{settings.Length} settings" +
                (rejected.Count > 0 ? $" | rejected: {string.Join(", ", rejected)}" : string.Empty));
        }
        catch (Exception ex)
        {
            Log($"libtorrent UpdateSettings failed: {ex.Message}");
        }
    }

    public static async Task<TorrentManager> StartDownloadAsync(
        string magnetUri,
        string downloadDir,
        Action<TorrentManager>? onManagerCreated = null,
        CancellationToken ct = default)
    {
        var client = GetClientAsync();
        Directory.CreateDirectory(downloadDir);

        // Every magnet goes through here, including auto-resumed downloads, so the
        // fallback trackers are added in exactly one place.
        var magnet = TrackerList.AppendTo(magnetUri);

        Log($"Attaching magnet link via libtorrent...");
        TorrentManager manager;
        try
        {
            manager = client.AttachMagnet(magnet, downloadDir);
        }
        catch (Exception ex)
        {
            Log($"AttachMagnet failed: {ex.Message}");
            throw;
        }

        Log($"Manager created, waiting for metadata...");
        onManagerCreated?.Invoke(manager);

        manager.PauseAfterMetadata = false;
        manager.Start();

        try
        {
            await manager.WaitForMetadata(TimeSpan.FromSeconds(90));
            Log($"Metadata received: {manager.Info?.Metadata.Name ?? "unknown"}");
        }
        catch (TimeoutException)
        {
            Log("Metadata fetch timed out after 90s");
        }
        catch (Exception ex)
        {
            Log($"Metadata error: {ex.Message}");
        }

        manager.Start();

        for (int i = 1; i <= 6; i++)
        {
            var delay = i * 10;
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(delay));
                try
                {
                    var status = manager.GetCurrentStatus();
                    Log($"{delay}s - State={status.State} Progress={status.Progress * 100:F1}% " +
                        $"Peers={status.PeerCount} Seeds={status.SeedCount} " +
                        $"Downloaded={FormatSize(status.BytesDownloaded)} " +
                        $"Rate={FormatSpeed(status.DownloadRate)}");
                }
                catch (Exception ex) { Log($"{delay}s error: {ex.Message}"); }
            });
        }

        return manager;
    }

    public static async Task<TorrentManager> StartDownloadFromTorrentFileAsync(
        byte[] torrentData,
        string downloadDir,
        Action<TorrentManager>? onManagerCreated = null,
        CancellationToken ct = default)
    {
        var client = GetClientAsync();
        Directory.CreateDirectory(downloadDir);

        var tempFile = Path.Combine(Path.GetTempPath(), $"omen_torrent_{Guid.NewGuid():N}.torrent");
        try
        {
            await File.WriteAllBytesAsync(tempFile, torrentData, ct);
            var info = new TorrentInfo(tempFile);
            Log($"Loaded .torrent: {info.Metadata.Name}");

            var manager = client.AttachTorrent(info, downloadDir);
            onManagerCreated?.Invoke(manager);
            manager.Start();
            Log("Torrent file download started");
            return manager;
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    // Public .torrent caches are frequently dead or hang until timeout. They are
    // therefore raced against each other behind a short deadline: these are at best
    // a shortcut to metadata, and the magnet link can always deliver it anyway.
    // Sequentially waiting on dead hosts used to stall a download by 40+ seconds
    // before the torrent engine had even started.
    private static readonly TimeSpan CacheServiceTimeout = TimeSpan.FromSeconds(5);

    public static async Task<bool> TryDownloadTorrentFileAsync(string infoHashHex, string downloadDir,
        Action<TorrentManager>? onManagerCreated, CancellationToken ct = default)
    {
        var services = new[]
        {
            $"https://itorrents.org/torrent/{infoHashHex}.torrent",
            $"https://torrage.info/torrent/{infoHashHex}.torrent",
            $"https://torcache.net/torrent/{infoHashHex}.torrent.torrent",
        };

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(CacheServiceTimeout);

        var attempts = new List<Task<byte[]?>>();
        foreach (var url in services)
            attempts.Add(FetchTorrentFromCacheAsync(url, deadline.Token));

        byte[]? data = null;
        while (attempts.Count > 0)
        {
            var finished = await Task.WhenAny(attempts);
            attempts.Remove(finished);
            var result = await finished;
            if (result is not null) { data = result; break; }
        }

        if (data is null)
        {
            Log($"No .torrent from any cache within {CacheServiceTimeout.TotalSeconds:F0}s; using magnet link");
            return false;
        }

        var manager = await StartDownloadFromTorrentFileAsync(data, downloadDir, onManagerCreated, ct);
        return true;
    }

    private static async Task<byte[]?> FetchTorrentFromCacheAsync(string url, CancellationToken ct)
    {
        try
        {
            var data = await Http.GetByteArrayAsync(url, ct);
            if (data is { Length: > 100 } && data[0] == 'd')
            {
                Log($"Got .torrent file from {url} ({data.Length} bytes)");
                return data;
            }
            Log($"Cache {url} returned no usable torrent");
        }
        catch (Exception ex)
        {
            Log($"Cache {url} failed: {ex.Message}");
        }
        return null;
    }

    public static void PauseDownload(TorrentManager manager)
    {
        try { manager.Stop(); } catch { }
    }

    // A finished torrent keeps seeding, and on an asymmetric home connection that
    // upload competes with whatever is downloading next. Stopping and detaching ends
    // the transfer outright: the files stay on disk, but the torrent stops
    // advertising to peers and gives its upload slots back.
    public static void StopSeeding(TorrentManager manager)
    {
        try
        {
            manager.Stop();
            _client?.DetachTorrent(manager);
            Log("Seeding stopped after download completed (releases upload bandwidth)");
        }
        catch (Exception ex)
        {
            Log($"Stop-seeding failed: {ex.Message}");
        }
    }

    // Stops a torrent and detaches it from the session, so a superseded manager
    // stops competing for peers and upload slots with the one replacing it.
    public static void ReleaseTorrent(TorrentManager manager)
    {
        try { manager.Stop(); } catch { }
        try { _client?.DetachTorrent(manager); } catch (Exception ex) { Log($"Detach failed: {ex.Message}"); }
    }

    public static void ResumeDownload(TorrentManager manager)
    {
        try { manager.Start(); } catch { }
    }

    public static void CancelDownload(TorrentManager manager)
    {
        try { manager.Stop(); } catch { }
    }

    public static (double progress01, long speedBytes, long downloaded, long total,
        int peers, int seeds) GetStats(TorrentManager manager)
    {
        try
        {
            var status = manager.GetCurrentStatus();
            return (status.Progress, status.DownloadRate, status.BytesDownloaded, 0,
                status.PeerCount, status.SeedCount);
        }
        catch { return (0, 0, 0, 0, 0, 0); }
    }

    public static string FormatSpeed(long bytesPerSecond)
    {
        if (bytesPerSecond < 1024) return $"{bytesPerSecond} B/s";
        if (bytesPerSecond < 1024 * 1024) return $"{bytesPerSecond / 1024.0:F1} KB/s";
        return $"{bytesPerSecond / (1024.0 * 1024.0):F1} MB/s";
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }

    public static string FormatEta(TimeSpan eta)
    {
        if (eta.TotalSeconds < 1) return "\u2014";
        if (eta.TotalHours >= 1) return $"{(int)eta.TotalHours}h {eta.Minutes}m";
        if (eta.TotalMinutes >= 1) return $"{(int)eta.TotalMinutes}m {eta.Seconds}s";
        return $"{(int)eta.TotalSeconds}s";
    }
}

public class DownloadSettings
{
    public string? DownloadPath { get; set; }
}
