using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
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
        bool IsInstalled) : INotifyPropertyChanged
    {
        public string? Cover => CoverUrl;
        public string? Background => BackgroundUrl;
        public string? StoreName => "IGDB";
        public string? Target => null;

        private bool _isDownloading;
        public bool IsDownloading
        {
            get => _isDownloading;
            set { if (_isDownloading == value) return; _isDownloading = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsBusy)); }
        }

        private bool _isInstalling;
        public bool IsInstalling
        {
            get => _isInstalling;
            set { if (_isInstalling == value) return; _isInstalling = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsBusy)); }
        }

        // True while the game is downloading or installing, so the card can show
        // its live progress bar.
        public bool IsBusy => _isDownloading || _isInstalling;

        private double _progressWidth;
        public double ProgressWidth
        {
            get => _progressWidth;
            set { if (Math.Abs(_progressWidth - value) < 0.01) return; _progressWidth = value; OnPropertyChanged(); }
        }

        private string _progressText = "";
        public string ProgressText
        {
            get => _progressText;
            set { if (_progressText == value) return; _progressText = value; OnPropertyChanged(); }
        }

        private string _activityText = "";
        public string ActivityText
        {
            get => _activityText;
            set { if (_activityText == value) return; _activityText = value; OnPropertyChanged(); }
        }

        private bool _isFitGirlAvailable = true;
        public bool IsFitGirlAvailable
        {
            get => _isFitGirlAvailable;
            set { if (_isFitGirlAvailable == value) return; _isFitGirlAvailable = value; OnPropertyChanged(); }
        }

        private string? _localSetupPath;
        public string? LocalSetupPath
        {
            get => _localSetupPath;
            set { if (_localSetupPath == value) return; _localSetupPath = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasLocalRepack)); }
        }

        public bool HasLocalRepack => !string.IsNullOrWhiteSpace(_localSetupPath);

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
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
            return results
                .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }
        finally { RequestGate.Release(); }
    }

    // Quick IGDB cover lookup for a single title (used to give repack-detected
    // Download Center cards a cover image). Returns null when the store has no
    // credentials, the search stalls, or no cover exists.
    //
    // IGDB's search ranking is fuzzy: "God of War Ragnarok; limit 1" can return a
    // different God of War entry, so we pull up to 10 candidates and pick the one
    // whose name actually matches the requested title.
    public static async Task<string?> FindCoverUrlAsync(string name, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var settings = MetadataSettingsStore.Load();
            var source = settings.Sources.FirstOrDefault(s =>
                s.IsEnabled && Uri.TryCreate(s.BaseUrl, UriKind.Absolute, out var u) &&
                u.Host.Contains("igdb.com", StringComparison.OrdinalIgnoreCase));
            if (source is null || string.IsNullOrWhiteSpace(source.ClientId)) return null;
            var secret = WindowsCredentialStore.ReadMetadataKey(source.Id);
            if (string.IsNullOrWhiteSpace(secret)) return null;

            var token = await GetTokenAsync(source, secret, ct);
            if (token is null) return null;

            var baseUrl = source.BaseUrl!.TrimEnd('/');
            if (!baseUrl.EndsWith("/v4", StringComparison.OrdinalIgnoreCase)) baseUrl += "/v4";
            var safeQuery = name.Replace("\\", "\\\\").Replace("\"", "\\\"");
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/games");
            request.Headers.Add("Client-ID", source.ClientId);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(
                $"search \"{safeQuery}\"; fields name,cover.image_id; limit 10;",
                Encoding.UTF8, "text/plain");

            await RequestGate.WaitAsync(ct);
            try
            {
                var remaining = TimeSpan.FromMilliseconds(260) - (DateTime.UtcNow - _lastRequestUtc);
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining, ct);
                using var response = await Client.SendAsync(request, ct);
                _lastRequestUtc = DateTime.UtcNow;
                if (!response.IsSuccessStatusCode) return null;
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (json.RootElement.ValueKind != JsonValueKind.Array) return null;

                var wanted = SlugCore(name);
                (int Gap, string Url)? best = null;
                foreach (var item in json.RootElement.EnumerateArray())
                {
                    if (!item.TryGetProperty("cover", out var cover) ||
                        !cover.TryGetProperty("image_id", out var imgId) ||
                        imgId.GetString() is not { Length: > 0 } imageId)
                        continue;
                    var candidate = item.TryGetProperty("name", out var nm)
                        ? nm.GetString()
                        : null;
                    var gap = string.IsNullOrWhiteSpace(candidate)
                        ? 1000
                        : EditDistance(SlugCore(candidate), wanted);
                    if (best is null || gap < best.Value.Gap)
                        best = (gap, $"https://images.igdb.com/igdb/image/upload/t_cover_big_2x/{imageId}.jpg");
                }
                return best is { } b ? b.Url : null;
            }
            finally { RequestGate.Release(); }
        }
        catch { }
        return null;
    }

    // Lowercase, strip owner tags & non-alphanumerics so "God of War Ragnarok
    // [FitGirl Repack]" and "God of War Ragnarök" compare on the same plane.
    private static string SlugCore(string text)
    {
        var t = text.ToLowerInvariant();
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\[[^\]]*\]", " ");
        var sb = new StringBuilder(t.Length);
        foreach (var c in t)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else sb.Append(' ');
        }
        return sb.ToString();
    }

    // Simple Levenshtein distance for picking the closest cover candidate.
    private static int EditDistance(string a, string b)
    {
        var dp = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) dp[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) dp[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        for (var j = 1; j <= b.Length; j++)
            dp[i, j] = Math.Min(
                Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1),
                dp[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return dp[a.Length, b.Length];
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
