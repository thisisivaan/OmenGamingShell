using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp;
using AngleSharp.Html.Parser;
using FuzzySharp;

namespace OmenGamingShell;

public sealed record FitGirlMatch(
    string Title, string Url, int Score);

public static class FitGirlScrapingService
{
    private const string BaseSiteUrl = "https://fitgirl-repacks.site";
    // A single hiccup used to kill an entire download attempt: this request had a
    // 20s timeout and no retry. Cloudflare challenges typically hold the connection
    // open with no response until the client gives up, which surfaces as a client
    // timeout rather than an error from the site, so the search is retried and the
    // allowance is wide enough for a slow-but-working response.
    private const int SearchAttempts = 3;
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(35);

    // Availability probing: cap concurrent HEAD checks so a 20-result page never
    // hammers FitGirl, and memoize keyword searches per query so rapid re-typing of
    // the same search does not re-fire the site.
    private static readonly TimeSpan KeywordMemoTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AvailabilityCacheTtl = TimeSpan.FromMinutes(30);
    private static readonly object KeywordMemoLock = new();
    private static string? _keywordMemoKey;
    private static Task<List<FitGirlMatch>>? _keywordMemoTask;
    private static DateTime _keywordMemoWhen;
    private static readonly ConcurrentDictionary<string, (bool Ok, DateTime When)> AvailabilityCache = new();
    private static readonly SemaphoreSlim HeadGate = new(4, 4);

    private static readonly HttpClient Http = new()
    {
        Timeout = SearchTimeout
    };

    static FitGirlScrapingService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
        Http.DefaultRequestHeaders.Referrer = new Uri("https://fitgirl-repacks.site/");
    }

    public static async Task<List<FitGirlMatch>> SearchAsync(string gameTitle, CancellationToken ct = default)
    {
        var query = Uri.EscapeDataString(gameTitle);
        var url = $"https://fitgirl-repacks.site/?s={query}";

        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= SearchAttempts; attempt++)
        {
            try
            {
                var html = await Http.GetStringAsync(url, ct);
                if (IsChallengeResponse(html))
                    throw new CloudflareChallengeException();

                var results = ParseResults(html, gameTitle);
                if (attempt > 1)
                    TorrentDownloadService.Log($"FitGirl search recovered on attempt {attempt} for '{gameTitle}'");
                return results;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                lastFailure = ex;
                TorrentDownloadService.Log(
                    $"FitGirl search attempt {attempt}/{SearchAttempts} failed for '{gameTitle}': {DescribeFailure(ex)}");
                if (attempt < SearchAttempts)
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
            }
        }

        // Callers surface this message straight to the user, so it names the cause
        // instead of leaking HttpClient wording.
        throw new InvalidOperationException(
            $"FitGirl search failed after {SearchAttempts} attempts ({DescribeFailure(lastFailure!)}). " +
            "The site may be rate-limiting; try again in a minute.", lastFailure);
    }

    private static bool IsChallengeResponse(string html) =>
        html.Contains("cf-challenge", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("Checking your browser", StringComparison.OrdinalIgnoreCase) ||
        html.Length < 1000;

    private static string DescribeFailure(Exception ex) => ex switch
    {
        CloudflareChallengeException => "Cloudflare bot check",
        OperationCanceledException => $"no response within {SearchTimeout.TotalSeconds:F0}s",
        HttpRequestException => $"network error ({ex.Message})",
        _ => ex.Message,
    };

    private static List<FitGirlMatch> ParseResults(string html, string gameTitle)
    {
        var parser = new HtmlParser();
        var doc = parser.ParseDocument(html);

        var results = new List<FitGirlMatch>();

        var articles = doc.QuerySelectorAll("article");
        foreach (var article in articles)
        {
            var titleEl = article.QuerySelector("h2 a, h3 a, .entry-title a");
            if (titleEl is null) continue;

            var postTitle = titleEl.TextContent.Trim();
            var postUrl = titleEl.GetAttribute("href") ?? "";

            if (string.IsNullOrWhiteSpace(postTitle) || string.IsNullOrWhiteSpace(postUrl))
                continue;

            var score = Fuzz.WeightedRatio(
                NormalizeTitle(gameTitle),
                NormalizeTitle(postTitle));

            results.Add(new FitGirlMatch(postTitle, postUrl, score));
        }

        return results;
    }

    public static async Task<FitGirlMatch?> FindBestMatchAsync(
        string gameTitle, CancellationToken ct = default)
    {
        var all = await SearchAsync(gameTitle, ct);
        if (all.Count == 0) return null;

        var best = all.OrderByDescending(m => m.Score).First();
        return best.Score >= 80 ? best : null;
    }

    // ------------------------------------------------------------------
    // Store-page availability: tells the UI which IGDB results actually have
    // a repack so those rows can be greyed out and made non-clickable.
    // A result counts as downloadable when its slug-core appears verbatim in
    // the keyword-search hits (>=95) OR a direct HEAD probe of its page 200s.
    // ------------------------------------------------------------------

    public static Task<List<FitGirlMatch>> StartQuerySearch(string query)
    {
        lock (KeywordMemoLock)
        {
            if (_keywordMemoTask is not null && _keywordMemoKey == query &&
                DateTime.UtcNow - _keywordMemoWhen < KeywordMemoTtl)
                return _keywordMemoTask;

            _keywordMemoKey = query;
            _keywordMemoWhen = DateTime.UtcNow;
            return _keywordMemoTask = SearchAsync(query, CancellationToken.None);
        }
    }

    public static async Task<Dictionary<string, bool>> ResolveAvailabilityAsync(
        Task<List<FitGirlMatch>> keywordSearch,
        IReadOnlyList<string> gameNames,
        CancellationToken ct = default)
    {
        var availability = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (gameNames.Count == 0) return availability;

        // Fail-open: if the keyword search broke completely, fall back to HEAD
        // probes only instead of treating every row as "no repack".
        List<FitGirlMatch> hits;
        try { hits = await keywordSearch; }
        catch { hits = []; }

        var hitCores = hits
            .Select(h => SlugCore(SlugFromUrl(h.Url)))
            .Where(c => c.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var name in gameNames)
        {
            if (ct.IsCancellationRequested) break;
            var nameCore = SlugCore(name);
            var available = nameCore.Length > 0 && hitCores.Contains(nameCore);
            if (!available) available = await HeadCheckExistsAsync(name, ct);
            availability[name] = available;
        }
        return availability;
    }

    private static string SlugFromUrl(string url)
    {
        var trimmed = url.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        return idx >= 0 ? trimmed[(idx + 1)..] : trimmed;
    }

    private static string CleanName(string t)
    {
        t = FoldAccents(t).ToLowerInvariant();
        t = t.Replace("\u2019", "'");
        t = Regex.Replace(t, @"'s\b", "s");
        t = Regex.Replace(t, @"[']", "");
        t = Regex.Replace(t, @"[^\w\s]", " ");
        return Regex.Replace(t, @"\s+", " ").Trim();
    }

    // FitGirl post slugs are ASCII (god-of-war-ragnarok), while IGDB names keep
    // diacritics (Ragnarök). Fold them to their base letters before any slug work.
    private static string FoldAccents(string t)
    {
        var hasAccents = false;
        foreach (var c in t)
        {
            if (c > 0x7F && char.IsLetter(c)) { hasAccents = true; break; }
        }
        if (!hasAccents) return t;

        var decomposed = t.Normalize(NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string SlugCore(string t) =>
        EditionRegex.Replace(" " + CleanName(t) + " ", " ").Trim();

    // Public slug for matching a repack folder name against an installed game's
    // (or IGDB result's) name. "God of War Ragnarök" and "God of War Ragnarok"
    // both normalize to the same core.
    public static string NormalizeName(string t) => SlugCore(t);

    private static readonly Regex EditionRegex = new(
        @"\b(ultimate|definitive|legendary|game of the year|goty|complete|standard|deluxe|remastered?|remaster|anniversary|special edition|director.?s cut|enhanced|digital|gold|premium|collector.?s? edition|edition|plus dlc|all dlc|all dlcs|bundle|master assassin|jackdaw|day 1 patch|bonus content|bonus ost|with update|v\d+(\.\d+)*|update\d*|dlc|dlcs|windows 7 fix|repack|reloaded)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string SlugifyName(string name) =>
        Regex.Replace(CleanName(name), @"\s+", "-");

    private static string RomanToDigits(string slug) =>
        Regex.Replace(slug, @"\b(iii|ii|iv|ix|vi|vii|viii|v|x|i)\b",
            m => m.Value.ToLowerInvariant() switch
            {
                "i" => "1", "ii" => "2", "iii" => "3", "iv" => "4", "v" => "5",
                "vi" => "6", "vii" => "7", "viii" => "8", "ix" => "9", "x" => "10",
                _ => m.Value,
            }, RegexOptions.IgnoreCase);

    private static async Task<bool> HeadCheckExistsAsync(string name, CancellationToken ct)
    {
        var baseSlug = SlugifyName(name);
        foreach (var candidate in new[] { baseSlug, RomanToDigits(baseSlug) }.Distinct())
        {
            var url = $"{BaseSiteUrl}/{candidate}/";
            if (AvailabilityCache.TryGetValue(url, out var cached) &&
                DateTime.UtcNow - cached.When < AvailabilityCacheTtl)
                return cached.Ok;

            var ok = await HeadSlugAsync(url, ct);
            AvailabilityCache[url] = (ok, DateTime.UtcNow);
            if (ok) return true;
        }
        return false;
    }

    private static async Task<bool> HeadSlugAsync(string url, CancellationToken ct)
    {
        try
        {
            await HeadGate.WaitAsync(ct);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, url);
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                // Grey out only on a definitive 404/410; challenges, redirects and
                // rate-limit responses keep the row clickable (fail-open).
                return response.StatusCode is not System.Net.HttpStatusCode.NotFound
                    and not System.Net.HttpStatusCode.Gone;
            }
            finally { HeadGate.Release(); }
        }
        catch
        {
            return true; // network hiccup -> leave clickable
        }
    }

    public static async Task<string?> ExtractMagnetLinkAsync(
        string postUrl, CancellationToken ct = default)
    {
        var html = await Http.GetStringAsync(postUrl, ct);
        var magnetPattern = @"magnet:\?xt=urn:[a-zA-Z0-9]+:[a-zA-Z0-9]{32,}[a-zA-Z0-9%&=_.\-]*";
        var match = Regex.Match(html, magnetPattern, RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        var magnet = match.Value;

        var trackerPattern = @"https?://[a-zA-Z0-9._\-]+:\d+/announce[^""'\s&<>]*";
        var trackerMatches = Regex.Matches(html, trackerPattern, RegexOptions.IgnoreCase);
        if (trackerMatches.Count > 0)
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match tp in Regex.Matches(magnet, @"[&?]tr=([^&]+)", RegexOptions.IgnoreCase))
                existing.Add(Uri.UnescapeDataString(tp.Groups[1].Value));

            foreach (Match tm in trackerMatches)
            {
                var trackerUrl = tm.Value.TrimEnd('"', '\'', ')', ']', ' ');
                if (!existing.Contains(trackerUrl))
                {
                    magnet += $"&tr={Uri.EscapeDataString(trackerUrl)}";
                    existing.Add(trackerUrl);
                }
            }
        }

        return magnet;
    }

    public static async Task<string?> ExtractTorrentFileUrlAsync(
        string postUrl, CancellationToken ct = default)
    {
        var html = await Http.GetStringAsync(postUrl, ct);
        var patterns = new[]
        {
            @"href\s*=\s*[""']([^""']*\.torrent[^""']*)[""']",
            @"href\s*=\s*[""']([^""']*torrent[^""']*)[""']"
        };
        foreach (var pat in patterns)
        {
            var m = Regex.Match(html, pat, RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var url = m.Groups[1].Value;
                if (!url.StartsWith("http"))
                    url = "https://fitgirl-repacks.site" + url;
                return url;
            }
        }
        return null;
    }

    // Reads the "Install size" figure from a repack post so a silent install can
    // show real progress (bytes written / expected size). FitGirl posts don't use
    // one fixed label: most say "Install size: 32.5 GB", others "HDD space after
    // installation: up to 176 GB". We try the exact labels in order of preference
    // and convert whichever is found to bytes.
    public static async Task<long> ExtractInstallSizeBytesAsync(
        string postUrl, CancellationToken ct = default)
    {
        try
        {
            var html = await Http.GetStringAsync(postUrl, ct);
            string[] patterns =
            {
                @"(?:Install size|install size|installation size)\s*:\s*([\d.,]+)\s*(GB|GiB|MB|MiB)",
                @"HDD space after installation\s*:\s*(?:up to\s*)?([\d.,]+)\s*(GB|GiB|MB|MiB)",
            };
            foreach (var pattern in patterns)
            {
                var m = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
                if (m.Success)
                    return ParseSizeWithUnit(m.Groups[1].Value, m.Groups[2].Value);
            }
            return 0;
        }
        catch
        {
            return 0;
        }
    }

    // Reads the "Download size" figure from a repack post so the shell can refuse
    // to start a download that cannot possibly fit. FitGirl pages state it in a
    // couple of layouts: "Download size: X GB", or the banner line
    // "Compressed from cumulative A to B GB" (often a range like "65.1~82 GB",
    // in which case we keep the upper bound as the worst case).
    public static async Task<long> ExtractDownloadSizeBytesAsync(
        string postUrl, CancellationToken ct = default)
    {
        try
        {
            var html = await Http.GetStringAsync(postUrl, ct);
            var label = Regex.Match(html,
                @"(?:Download size|Repack size|download size)\s*:\s*([\d.,]+)\s*(GB|GiB|MB|MiB)",
                RegexOptions.IgnoreCase);
            if (label.Success)
                return ParseSizeWithUnit(label.Groups[1].Value, label.Groups[2].Value);

            var range = Regex.Match(html,
                @"compressed from cumulative [\d.,]+\s+to\s+~?([\d.,]+)(?:~([\d.,]+))?\s*(GB|GiB)",
                RegexOptions.IgnoreCase);
            if (range.Success)
            {
                var unit = range.Groups[3].Value;
                var hi = range.Groups[2].Success
                    ? ParseSizeWithUnit(range.Groups[2].Value, unit)
                    : ParseSizeWithUnit(range.Groups[1].Value, unit);
                var lo = ParseSizeWithUnit(range.Groups[1].Value, unit);
                return Math.Max(hi, lo);
            }
            return 0;
        }
        catch
        {
            return 0;
        }
    }

    private static long ParseSizeWithUnit(string number, string unit)
    {
        if (!decimal.TryParse(number.Replace(",", "."), NumberStyles.Number,
                CultureInfo.InvariantCulture, out var value) || value <= 0)
            return 0;
        var u = unit.ToUpperInvariant();
        if (u is "MB" or "MIB" or "M") return (long)(value * 1024 * 1024);
        return (long)(value * 1024 * 1024 * 1024);
    }

    public static async Task<byte[]?> DownloadTorrentFileAsync(
        string torrentUrl, CancellationToken ct = default)
    {
        try
        {
            return await Http.GetByteArrayAsync(torrentUrl, ct);
        }
        catch
        {
            return null;
        }
    }

    private sealed class CloudflareChallengeException : Exception
    {
        public CloudflareChallengeException() : base("Cloudflare protection detected.") { }
    }

    private static string NormalizeTitle(string title)
    {
        var t = title.ToLowerInvariant();
        t = Regex.Replace(t, @"[^\w\s]", " ");
        t = Regex.Replace(t, @"\s+", " ").Trim();
        t = Regex.Replace(t, @"\b(repack|fitgirl|reloaded|codex|skidrow|plaza|cpy)\b", "",
            RegexOptions.IgnoreCase);
        return t.Trim();
    }
}
