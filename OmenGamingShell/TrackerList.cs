using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace OmenGamingShell;

// Extra public trackers appended to every magnet link.
//
// A magnet only carries the trackers its author embedded - usually a handful,
// and some of those are dead. Trackers are introducer servers: each one hands
// out peers you would not otherwise find, so a longer list (especially early in
// a download, before DHT has populated) means the swarm is found sooner and the
// download ramps up faster.
//
// The list refreshes from ngosang/trackerslist (trackers_best.txt), which is
// actively maintained, because public trackers appear and disappear over time.
// The curated snapshot below is the offline baseline: refresh is best-effort,
// never blocks a download, and is only trusted after every entry is validated.
//
// Scope: magnets only. csdl exposes no API to add trackers to an attached
// torrent (only ReannounceAllTrackers), and rewriting a .torrent's announce-list
// by hand would risk corrupting the info hash, so .torrent downloads keep the
// announce list baked into the file.
public static class TrackerList
{
    // Source: ngosang/trackerslist, trackers_best.txt (snapshot 2026-09-19).
    private static readonly string[] Snapshot =
    {
        "udp://tracker.opentrackr.org:1337/announce",
        "udp://open.stealth.si:80/announce",
        "udp://tracker.torrent.eu.org:451/announce",
        "udp://open.demonii.com:1337/announce",
        "udp://tracker.qu.ax:6969/announce",
        "udp://exodus.desync.com:6969/announce",
        "udp://tracker.theoks.net:6969/announce",
        "udp://tracker.nyaa.vc:6969/announce",
        "udp://tracker.gmi.gd:6969/announce",
        "udp://tracker.plx.im:6969/announce",
        "udp://tracker-udp.gbitt.info:80/announce",
        "udp://tracker.corpscorp.online:80/announce",
        "udp://tracker.bittor.pw:1337/announce",
        "udp://tracker.ducks.party:1984/announce",
        "udp://explodie.org:6969/announce",
        "udp://tracker.0x7c0.com:6969/announce",
        "udp://retracker01-msk-virt.corbina.net:80/announce",
        "http://tracker.dler.com:6969/announce",
        "udp://tracker2.dler.org:80/announce",
        "http://tracker.renfei.net:8080/announce",
    };

    private const string SourceUrl =
        "https://raw.githubusercontent.com/ngosang/trackerslist/master/trackers_best.txt";

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(7);

    // Announcing to a very long list costs time and traffic without adding peers,
    // and an enormous magnet gets unwieldy.
    private const int MaxTrackers = 40;

    private static readonly HttpClient Http = CreateClient();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly object Gate = new();

    private static string[] _trackers = Snapshot;
    private static DateTimeOffset _lastRefreshed = DateTimeOffset.MinValue;

    static TrackerList() => LoadCache();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OmenGamingShell/1.0");
        return client;
    }

    private static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OmenGamingShell", "tracker-list.json");

    public static IReadOnlyList<string> Fallback { get { lock (Gate) return _trackers; } }

    public static int Count { get { lock (Gate) return _trackers.Length; } }

    public static DateTimeOffset LastRefreshedUtc { get { lock (Gate) return _lastRefreshed; } }

    // Best-effort background refresh. Called at startup and when a download
    // begins; cheap and silent when the cached list is still fresh.
    public static async Task<bool> RefreshAsync(bool force = false)
    {
        try
        {
            lock (Gate)
            {
                if (!force && DateTimeOffset.UtcNow - _lastRefreshed < RefreshInterval) return false;
            }

            using var response = await Http.GetAsync(SourceUrl);
            if (!response.IsSuccessStatusCode)
            {
                TorrentDownloadService.Log($"Tracker refresh failed: HTTP {(int)response.StatusCode} - keeping current list");
                return false;
            }

            var fetched = Sanitize((await response.Content.ReadAsStringAsync()).Split('\n'));
            if (fetched.Count == 0)
            {
                TorrentDownloadService.Log("Tracker refresh returned nothing usable - keeping current list");
                return false;
            }

            // Fetched entries lead (they are the maintained, currently-live ones).
            // Baseline entries missing from the response are kept, so a rotation
            // upstream cannot silently shrink the set we announce to.
            var merged = new List<string>(fetched);
            foreach (var tracker in Snapshot)
                if (!merged.Contains(tracker, StringComparer.OrdinalIgnoreCase))
                    merged.Add(tracker);
            if (merged.Count > MaxTrackers)
                merged.RemoveRange(MaxTrackers, merged.Count - MaxTrackers);

            var stamp = DateTimeOffset.UtcNow;
            lock (Gate)
            {
                _trackers = merged.ToArray();
                _lastRefreshed = stamp;
            }

            SaveCache(merged, stamp);
            TorrentDownloadService.Log($"Tracker list refreshed: {fetched.Count} fetched, {merged.Count} in use");
            return true;
        }
        catch (Exception ex)
        {
            TorrentDownloadService.Log($"Tracker refresh error: {ex.Message} - keeping current list");
            return false;
        }
    }

    // Exposed separately from the network path so the validation rules can be
    // tested without hitting the internet.
    public static IReadOnlyList<string> Parse(string? rawText)
    {
        if (string.IsNullOrEmpty(rawText)) return Array.Empty<string>();
        return Sanitize(rawText.Split('\n'));
    }

    // Returns the magnet with any trackers it does not already carry appended.
    // Trackers already in the link win: they are the ones the swarm's author
    // vetted, and they are never duplicated.
    public static string AppendTo(string magnetUri)
    {
        if (string.IsNullOrWhiteSpace(magnetUri) ||
            !magnetUri.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            return magnetUri;

        var current = Fallback;

        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(magnetUri, @"[&?]tr=([^&]+)", RegexOptions.IgnoreCase))
            present.Add(Normalize(Uri.UnescapeDataString(m.Groups[1].Value)));

        var existing = present.Count;
        var builder = new StringBuilder(magnetUri);
        foreach (var tracker in current)
        {
            // HashSet.Add returns false when the tracker is already there, which
            // also collapses duplicates within the list itself.
            if (!present.Add(Normalize(tracker))) continue;
            builder.Append("&tr=").Append(Uri.EscapeDataString(tracker));
        }

        var added = present.Count - existing;
        if (added > 0)
            TorrentDownloadService.Log(
                $"Magnet trackers: {existing} embedded + {added} fallback = {present.Count} total");

        return builder.ToString();
    }

    private static List<string> Sanitize(IEnumerable<string>? raw)
    {
        var result = new List<string>();
        if (raw is null) return result;

        foreach (var line in raw)
        {
            var entry = line.Trim();
            if (entry.Length == 0 || entry.StartsWith('#')) continue;
            if (!Uri.TryCreate(entry, UriKind.Absolute, out var uri)) continue;
            if (uri.Scheme is not ("udp" or "http" or "https")) continue;
            if (!uri.AbsolutePath.Contains("/announce", StringComparison.OrdinalIgnoreCase)) continue;
            if (result.Contains(entry, StringComparer.OrdinalIgnoreCase)) continue;
            result.Add(entry);
        }

        return result;
    }

    private static void LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return;
            var cache = JsonSerializer.Deserialize<TrackerCache>(File.ReadAllText(CachePath), Json);
            if (cache is null) return;

            var trackers = Sanitize(cache.Trackers);
            if (trackers.Count == 0) return;

            _trackers = trackers.ToArray();
            _lastRefreshed = cache.FetchedUtc;
            TorrentDownloadService.Log(
                $"Tracker cache loaded: {trackers.Count} trackers (fetched {cache.FetchedUtc:u})");
        }
        catch { }
    }

    private static void SaveCache(List<string> trackers, DateTimeOffset stamp)
    {
        try
        {
            var folder = Path.GetDirectoryName(CachePath)!;
            Directory.CreateDirectory(folder);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(
                new TrackerCache { FetchedUtc = stamp, Trackers = trackers }, Json));
        }
        catch { }
    }

    private static string Normalize(string trackerUrl) => trackerUrl.TrimEnd('/');

    private sealed class TrackerCache
    {
        public DateTimeOffset FetchedUtc { get; set; }
        public List<string> Trackers { get; set; } = new();
    }
}
