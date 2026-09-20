using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OmenGamingShell;

public static class GameStoreSearchService
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly ConcurrentDictionary<string, IgdbToken> Tokens = new();
    private static readonly SemaphoreSlim RequestGate = new(1, 1);
    private static DateTime _lastRequestUtc = DateTime.MinValue;

    static GameStoreSearchService()
    {
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("OmenGamingShell/1.0");
    }

    public sealed record SearchResult(
        int Id, string Name, string? CoverUrl, string? BackgroundUrl,
        string? Description, string? Genres, string? Developer,
        string? Publisher, string? ReleaseDate, string? Platforms, string? Rating,
        bool IsInstalled)
    {
        public string? Cover => CoverUrl;
        public string? Background => BackgroundUrl;
        public string? StoreName => "IGDB";
        public string? Target => null;
        public bool IsDownloading { get; set; }
    }

    public static async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, HashSet<string> installedNames, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var settings = MetadataSettingsStore.Load();
        var source = settings.Sources.FirstOrDefault(s =>
            s.IsEnabled && Uri.TryCreate(s.BaseUrl, UriKind.Absolute, out var u) &&
            u.Host.Contains("igdb.com", StringComparison.OrdinalIgnoreCase));
        if (source is null || string.IsNullOrWhiteSpace(source.ClientId)) return [];

        var secret = WindowsCredentialStore.ReadMetadataKey(source.Id);
        if (string.IsNullOrWhiteSpace(secret)) return [];

        var token = await GetTokenAsync(source, secret, ct);
        if (token is null) return [];

        var baseUrl = source.BaseUrl!.TrimEnd('/');
        if (!baseUrl.EndsWith("/v4", StringComparison.OrdinalIgnoreCase)) baseUrl += "/v4";

        var safeQuery = query.Replace("\\", "\\\\").Replace("\"", "\\\"");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/games");
        request.Headers.Add("Client-ID", source.ClientId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(
            $"search \"{safeQuery}\"; fields name,summary,first_release_date,rating,genres.name,platforms.name,cover.image_id,artworks.image_id,artworks.width,artworks.height,involved_companies.developer,involved_companies.publisher,involved_companies.company.name; where version_parent = null; limit 20;",
            Encoding.UTF8, "text/plain");

        await RequestGate.WaitAsync(ct);
        try
        {
            var remaining = TimeSpan.FromMilliseconds(260) - (DateTime.UtcNow - _lastRequestUtc);
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, ct);
            using var response = await Client.SendAsync(request, ct);
            _lastRequestUtc = DateTime.UtcNow;
            if (!response.IsSuccessStatusCode) return [];
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (json.RootElement.ValueKind != JsonValueKind.Array) return [];

            var results = new List<SearchResult>();
            foreach (var item in json.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idProp) || !idProp.TryGetInt32(out var id)) continue;
                var name = GetString(item, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;

                var nameLower = name.ToLowerInvariant();
                if (nameLower.Contains("dlc") || nameLower.Contains("skin") || nameLower.Contains("expansion") ||
                    nameLower.Contains("season pass") || nameLower.Contains("battle pass") ||
                    nameLower.Contains("content pack") || nameLower.Contains("character pack") ||
                    nameLower.Contains("weapon pack") || nameLower.Contains("map pack") ||
                    nameLower.Contains("cosmetic") || nameLower.Contains("bundle") ||
                    nameLower.Contains("soundtrack") || nameLower.Contains("artbook") ||
                    nameLower.Contains("season ") || nameLower.Contains("part ") && nameLower.Contains("pack"))
                    continue;

                string? coverUrl = null;
                if (item.TryGetProperty("cover", out var cover) &&
                    cover.TryGetProperty("image_id", out var imgId) &&
                    imgId.GetString() is { Length: > 0 } imageId)
                {
                    coverUrl = $"https://images.igdb.com/igdb/image/upload/t_cover_big_2x/{imageId}.jpg";
                }

                string? backgroundUrl = null;
                if (item.TryGetProperty("artworks", out var artworks) && artworks.ValueKind == JsonValueKind.Array)
                {
                    var best = artworks.EnumerateArray()
                        .Where(a => a.TryGetProperty("image_id", out _))
                        .OrderByDescending(a =>
                        {
                            var w = a.TryGetProperty("width", out var wv) && wv.TryGetInt32(out var ww) ? ww : 0;
                            var h = a.TryGetProperty("height", out var hv) && hv.TryGetInt32(out var hh) ? hh : 0;
                            return w > 0 && h > 0 && w > h ? w * h : 0;
                        })
                        .FirstOrDefault();
                    if (best.TryGetProperty("image_id", out var bgImgId) &&
                        bgImgId.GetString() is { Length: > 0 } bgImageId)
                    {
                        backgroundUrl = $"https://images.igdb.com/igdb/image/upload/t_original/{bgImageId}.jpg";
                    }
                }

                var isInstalled = installedNames.Contains(name);
                results.Add(new SearchResult(
                    id, name, coverUrl, backgroundUrl,
                    GetString(item, "summary"),
                    JoinNames(item, "genres"),
                    FindCompany(item, "developer"),
                    FindCompany(item, "publisher"),
                    GetReleaseDate(item),
                    JoinNames(item, "platforms"),
                    GetRating(item),
                    isInstalled));
            }
            return results;
        }
        finally { RequestGate.Release(); }
    }

    private static async Task<string?> GetTokenAsync(
        MetadataSourceConfig source, string clientSecret, CancellationToken ct)
    {
        if (Tokens.TryGetValue(source.Id, out var cached) && cached.ExpiresUtc > DateTime.UtcNow.AddMinutes(5))
            return cached.Value;
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = source.ClientId!,
            ["client_secret"] = clientSecret,
            ["grant_type"] = "client_credentials"
        });
        using var response = await Client.PostAsync("https://id.twitch.tv/oauth2/token", content, ct);
        if (!response.IsSuccessStatusCode) return null;
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!json.RootElement.TryGetProperty("access_token", out var val)) return null;
        var value = val.GetString();
        if (string.IsNullOrWhiteSpace(value)) return null;
        var seconds = json.RootElement.TryGetProperty("expires_in", out var exp) && exp.TryGetInt32(out var s) ? s : 3600;
        Tokens[source.Id] = new(value, DateTime.UtcNow.AddSeconds(seconds));
        return value;
    }

    private static string? GetString(JsonElement item, string property) =>
        item.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? JoinNames(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        var names = arr.EnumerateArray().Select(v => GetString(v, "name")).Where(n => !string.IsNullOrWhiteSpace(n));
        var joined = string.Join("  \u2022  ", names!);
        return joined.Length == 0 ? null : joined;
    }

    private static string? FindCompany(JsonElement item, string role)
    {
        if (!item.TryGetProperty("involved_companies", out var companies) || companies.ValueKind != JsonValueKind.Array) return null;
        return companies.EnumerateArray()
            .Where(c => c.TryGetProperty(role, out var flag) && flag.ValueKind == JsonValueKind.True)
            .Select(c => c.TryGetProperty("company", out var detail) ? GetString(detail, "name") : null)
            .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
    }

    private static string? GetReleaseDate(JsonElement item)
    {
        if (!item.TryGetProperty("first_release_date", out var v) || !v.TryGetInt64(out var seconds)) return null;
        return DateTimeOffset.FromUnixTimeSeconds(seconds).ToString("dd MMMM yyyy");
    }

    private static string? GetRating(JsonElement item)
    {
        if (!item.TryGetProperty("rating", out var v) || !v.TryGetDouble(out var rating)) return null;
        return $"{Math.Round(rating):0}/100";
    }

    private sealed record IgdbToken(string Value, DateTime ExpiresUtc);
}
