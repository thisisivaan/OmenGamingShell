using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
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
    // A single hiccup used to kill an entire download attempt: this request had a
    // 20s timeout and no retry. Cloudflare challenges typically hold the connection
    // open with no response until the client gives up, which surfaces as a client
    // timeout rather than an error from the site, so the search is retried and the
    // allowance is wide enough for a slow-but-working response.
    private const int SearchAttempts = 3;
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(35);

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
