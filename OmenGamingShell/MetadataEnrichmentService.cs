using System.IO;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OmenGamingShell;

public static class MetadataEnrichmentService
{
    private static readonly HttpClient Client = CreateClient();
    private static readonly ConcurrentDictionary<string, IgdbToken> IgdbTokens = new();
    private static readonly SemaphoreSlim IgdbRequestGate = new(1, 1);
    private static DateTime _lastIgdbRequestUtc = DateTime.MinValue;

    private static HttpClient CreateClient()
    {
        // Artwork CDNs can take longer to begin transferring than the small JSON
        // API responses, especially during a full-library refresh.
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OmenGamingShell/1.0");
        return client;
    }

    public static async Task EnrichAsync(IReadOnlyList<GameEntry> games, IProgress<int>? progress = null,
        IProgress<string>? status = null)
    {
        var settings = MetadataSettingsStore.Load();
        var enabledSources = settings.Sources.Where(source => source.IsEnabled).ToList();
        var sources = new List<MetadataSourceConfig>();
        var primary = enabledSources.FirstOrDefault(source => source.Id == settings.PrimarySourceId);
        if (primary is not null) sources.Add(primary);
        sources.AddRange(enabledSources.Where(source => source.Id != settings.PrimarySourceId));
        if (sources.Count == 0 || games.Count == 0)
        {
            progress?.Report(100);
            return;
        }

        var completed = 0;
        await Parallel.ForEachAsync(games, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (game, token) =>
        {
            if (game.HasMetadataOverride)
            {
                var overridden = Interlocked.Increment(ref completed);
                progress?.Report(65 + (int)Math.Round(35d * overridden / games.Count));
                return;
            }
            if (!string.IsNullOrWhiteSpace(game.Cover) && File.Exists(game.Cover) &&
                !string.IsNullOrWhiteSpace(game.Background) && File.Exists(game.Background) &&
                !string.IsNullOrWhiteSpace(game.Description))
            {
                var cachedFinished = Interlocked.Increment(ref completed);
                progress?.Report(65 + (int)Math.Round(35d * cachedFinished / games.Count));
                return;
            }
            status?.Report($"DOWNLOADING METADATA  {game.Name.ToUpperInvariant()}");
            foreach (var source in sources)
            {
                var secret = WindowsCredentialStore.ReadMetadataKey(source.Id);
                if (string.IsNullOrWhiteSpace(secret)) continue;
                string? cover = null;
                string? background = null;
                if (IsSteamGridDb(source))
                {
                    var steamArtwork = await TrySteamGridDbArtworkAsync(source, secret, game.Name, token);
                    cover = steamArtwork.Cover;
                    background = steamArtwork.Background;
                }
                else if (IsIgdb(source))
                {
                    var artwork = await TryIgdbArtworkAsync(source, secret, game.Name, token);
                    if (artwork is not null)
                    {
                        game.Cover ??= artwork.Cover;
                        game.Background ??= artwork.Background;
                        game.Description ??= artwork.Description;
                        game.Genres ??= artwork.Genres;
                        game.Developer ??= artwork.Developer;
                        game.Publisher ??= artwork.Publisher;
                        game.ReleaseDate ??= artwork.ReleaseDate;
                        game.Platforms ??= artwork.Platforms;
                        game.Rating ??= artwork.Rating;
                    }
                }
                if (cover is not null) game.Cover ??= cover;
                if (background is not null) game.Background ??= background;
                if (!string.IsNullOrWhiteSpace(game.Cover) && !string.IsNullOrWhiteSpace(game.Background) &&
                    !string.IsNullOrWhiteSpace(game.Description)) break;
            }
            if (string.IsNullOrWhiteSpace(game.Background) && !string.IsNullOrWhiteSpace(game.Cover))
                game.Background = ArtworkCache.CreateBackgroundFromCover(game.Cover,
                    $"cover-fallback-v2-{game.Target}-{game.Name}");
            var finished = Interlocked.Increment(ref completed);
            progress?.Report(65 + (int)Math.Round(35d * finished / games.Count));
        });
        GameMetadataCache.Save(games);
    }

    private static bool IsSteamGridDb(MetadataSourceConfig source) =>
        Uri.TryCreate(source.BaseUrl, UriKind.Absolute, out var uri) &&
        uri.Host.Contains("steamgriddb.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsIgdb(MetadataSourceConfig source) =>
        Uri.TryCreate(source.BaseUrl, UriKind.Absolute, out var uri) &&
        uri.Host.Contains("igdb.com", StringComparison.OrdinalIgnoreCase);

    private static async Task<GameArtwork?> TryIgdbArtworkAsync(
        MetadataSourceConfig source, string clientSecret, string gameName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.ClientId)) return null;
        try
        {
            var token = await GetIgdbTokenAsync(source, clientSecret, cancellationToken);
            if (token is null) return null;
            var baseUrl = source.BaseUrl!.TrimEnd('/');
            if (!baseUrl.EndsWith("/v4", StringComparison.OrdinalIgnoreCase)) baseUrl += "/v4";

            foreach (var query in BuildSearchQueries(gameName))
            {
                var safeQuery = query.Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("\"", "\\\"", StringComparison.Ordinal);
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/games");
                request.Headers.Add("Client-ID", source.ClientId);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Content = new StringContent(
                    $"search \"{safeQuery}\"; fields name,summary,first_release_date,rating,genres.name,platforms.name,cover.image_id,artworks.image_id,artworks.width,artworks.height,involved_companies.developer,involved_companies.publisher,involved_companies.company.name; where version_parent = null; limit 20;",
                    Encoding.UTF8, "text/plain");
                using var response = await SendIgdbAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode) continue;
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (json.RootElement.ValueKind != JsonValueKind.Array) continue;
                var match = FindBestIgdbMatch(json.RootElement, query) ?? FindBestIgdbMatch(json.RootElement, gameName);
                if (match is null) continue;

                var coverUrl = $"https://images.igdb.com/igdb/image/upload/t_cover_big_2x/{match.CoverImageId}.jpg";
                var downloadedCover = await DownloadArtworkAsync(coverUrl, $"igdb-{match.Id}", cancellationToken);
                var cover = downloadedCover is null
                    ? null : ArtworkCache.NormalizeCover(downloadedCover, $"igdb-{match.Id}");
                string? background = null;
                if (!string.IsNullOrWhiteSpace(match.BackgroundImageId))
                {
                    var backgroundUrl = $"https://images.igdb.com/igdb/image/upload/t_original/{match.BackgroundImageId}.jpg";
                    var downloadedBackground = await DownloadArtworkAsync(backgroundUrl,
                        $"igdb-artwork-original-v2-{match.Id}", cancellationToken);
                    if (downloadedBackground is not null)
                        background = ArtworkCache.NormalizeBackground(downloadedBackground,
                            $"igdb-artwork-2k-v2-{match.Id}");
                }
                return new GameArtwork(cover, background, match.Description, match.Genres,
                    match.Developer, match.Publisher, match.ReleaseDate, match.Platforms, match.Rating);
            }
        }
        catch { }
        return null;
    }

    private static IgdbGameMatch? FindBestIgdbMatch(
        JsonElement results, string gameName)
    {
        var wanted = Normalize(gameName);
        var match = results.EnumerateArray()
            .Where(item => item.TryGetProperty("id", out _) && item.TryGetProperty("name", out _) &&
                           item.TryGetProperty("cover", out var cover) && cover.TryGetProperty("image_id", out _))
            .Select(item => new
            {
                Id = item.GetProperty("id").GetInt32(),
                CoverImageId = item.GetProperty("cover").GetProperty("image_id").GetString() ?? string.Empty,
                BackgroundImageId = item.TryGetProperty("artworks", out var artworks) &&
                                    artworks.ValueKind == JsonValueKind.Array
                    ? artworks.EnumerateArray()
                        .Where(artwork => artwork.TryGetProperty("image_id", out _))
                        .OrderByDescending(LandscapeArtworkScore)
                        .Select(artwork => artwork.GetProperty("image_id").GetString())
                        .FirstOrDefault(imageId => !string.IsNullOrWhiteSpace(imageId))
                    : null,
                Description = GetString(item, "summary"),
                Genres = JoinNames(item, "genres"),
                Developer = FindCompany(item, "developer"),
                Publisher = FindCompany(item, "publisher"),
                ReleaseDate = GetReleaseDate(item),
                Platforms = JoinNames(item, "platforms"),
                Rating = GetRating(item),
                Score = MatchScore(wanted, Normalize(item.GetProperty("name").GetString() ?? string.Empty))
            })
            .Where(item => item.CoverImageId.Length > 0)
            .OrderByDescending(item => item.Score)
            .FirstOrDefault();
        return match is { Score: >= 50 }
            ? new IgdbGameMatch(match.Id, match.CoverImageId, match.BackgroundImageId, match.Description,
                match.Genres, match.Developer, match.Publisher, match.ReleaseDate, match.Platforms, match.Rating)
            : null;
    }

    private static string? GetString(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static string? JoinNames(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var values) || values.ValueKind != JsonValueKind.Array) return null;
        var names = values.EnumerateArray().Select(value => GetString(value, "name"))
            .Where(name => !string.IsNullOrWhiteSpace(name));
        var joined = string.Join("  •  ", names!);
        return joined.Length == 0 ? null : joined;
    }

    private static string? FindCompany(JsonElement item, string role)
    {
        if (!item.TryGetProperty("involved_companies", out var companies) ||
            companies.ValueKind != JsonValueKind.Array) return null;
        return companies.EnumerateArray()
            .Where(company => company.TryGetProperty(role, out var flag) && flag.ValueKind == JsonValueKind.True)
            .Select(company => company.TryGetProperty("company", out var detail) ? GetString(detail, "name") : null)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }

    private static string? GetReleaseDate(JsonElement item)
    {
        if (!item.TryGetProperty("first_release_date", out var value) || !value.TryGetInt64(out var seconds)) return null;
        return DateTimeOffset.FromUnixTimeSeconds(seconds).ToString("dd MMMM yyyy");
    }

    private static string? GetRating(JsonElement item)
    {
        if (!item.TryGetProperty("rating", out var value) || !value.TryGetDouble(out var rating)) return null;
        return $"{Math.Round(rating):0}/100";
    }

    private static async Task<string?> GetIgdbTokenAsync(
        MetadataSourceConfig source, string clientSecret, CancellationToken cancellationToken)
    {
        if (IgdbTokens.TryGetValue(source.Id, out var cached) && cached.ExpiresUtc > DateTime.UtcNow.AddMinutes(5))
            return cached.Value;
        var url = "https://id.twitch.tv/oauth2/token";
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = source.ClientId!,
            ["client_secret"] = clientSecret,
            ["grant_type"] = "client_credentials"
        });
        using var response = await Client.PostAsync(url, content, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (!json.RootElement.TryGetProperty("access_token", out var tokenValue)) return null;
        var value = tokenValue.GetString();
        if (string.IsNullOrWhiteSpace(value)) return null;
        var seconds = json.RootElement.TryGetProperty("expires_in", out var expiry) && expiry.TryGetInt32(out var parsed)
            ? parsed : 3600;
        IgdbTokens[source.Id] = new IgdbToken(value, DateTime.UtcNow.AddSeconds(seconds));
        return value;
    }

    private static async Task<HttpResponseMessage> SendIgdbAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await IgdbRequestGate.WaitAsync(cancellationToken);
        try
        {
            var remaining = TimeSpan.FromMilliseconds(260) - (DateTime.UtcNow - _lastIgdbRequestUtc);
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, cancellationToken);
            var response = await Client.SendAsync(request, cancellationToken);
            _lastIgdbRequestUtc = DateTime.UtcNow;
            return response;
        }
        finally { IgdbRequestGate.Release(); }
    }

    private sealed record IgdbToken(string Value, DateTime ExpiresUtc);
    private sealed record GameArtwork(string? Cover, string? Background, string? Description, string? Genres,
        string? Developer, string? Publisher, string? ReleaseDate, string? Platforms, string? Rating);
    private sealed record IgdbGameMatch(int Id, string CoverImageId, string? BackgroundImageId,
        string? Description, string? Genres, string? Developer, string? Publisher,
        string? ReleaseDate, string? Platforms, string? Rating);

    private static async Task<(string? Cover, string? Background)> TrySteamGridDbArtworkAsync(
        MetadataSourceConfig source, string apiKey, string gameName, CancellationToken cancellationToken)
    {
        try
        {
            var baseUrl = source.BaseUrl!.TrimEnd('/');
            if (!baseUrl.EndsWith("/api/v2", StringComparison.OrdinalIgnoreCase))
                baseUrl += "/api/v2";
            var gameId = await FindSteamGridDbGameIdAsync(baseUrl, apiKey, gameName, cancellationToken);
            if (gameId is null) return (null, null);

            using var gridRequest = CreateRequest(
                $"{baseUrl}/grids/game/{gameId}?dimensions=600x900,342x482,660x930&types=static&nsfw=false&humor=false", apiKey);
            using var gridResponse = await Client.SendAsync(gridRequest, cancellationToken);
            string? cover = null;
            if (gridResponse.IsSuccessStatusCode)
            {
            using var gridJson = JsonDocument.Parse(await gridResponse.Content.ReadAsStringAsync());
                if (gridJson.RootElement.TryGetProperty("data", out var grids) && grids.ValueKind == JsonValueKind.Array)
                {
                    var imageUrl = grids.EnumerateArray().Where(grid => grid.TryGetProperty("url", out _) && IsPortraitGrid(grid))
                        .OrderByDescending(GridQualityScore).Select(grid => grid.GetProperty("url").GetString())
                        .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));
                    if (imageUrl is not null)
                    {
                        var downloaded = await DownloadArtworkAsync(imageUrl, $"steamgriddb-{gameId}", cancellationToken);
                        if (downloaded is not null) cover = ArtworkCache.NormalizeCover(downloaded, $"steamgriddb-{gameId}");
                    }
                }
            }

            string? background = null;
            using var heroRequest = CreateRequest($"{baseUrl}/heroes/game/{gameId}?types=static&nsfw=false&humor=false", apiKey);
            using var heroResponse = await Client.SendAsync(heroRequest, cancellationToken);
            if (heroResponse.IsSuccessStatusCode)
            {
                using var heroJson = JsonDocument.Parse(await heroResponse.Content.ReadAsStringAsync());
                if (heroJson.RootElement.TryGetProperty("data", out var heroes) && heroes.ValueKind == JsonValueKind.Array)
                {
                    var heroUrl = heroes.EnumerateArray().Where(hero => hero.TryGetProperty("url", out _))
                        .OrderByDescending(LandscapeArtworkScore).Select(hero => hero.GetProperty("url").GetString())
                        .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));
                    if (heroUrl is not null)
                    {
                        var downloaded = await DownloadArtworkAsync(heroUrl, $"steamgriddb-hero-v2-{gameId}", cancellationToken);
                        if (downloaded is not null) background = ArtworkCache.NormalizeBackground(downloaded, $"steamgriddb-hero-2k-v2-{gameId}");
                    }
                }
            }
            return (cover, background);
        }
        catch
        {
            return (null, null);
        }
    }

    private static int LandscapeArtworkScore(JsonElement artwork)
    {
        var width = artwork.TryGetProperty("width", out var widthValue) && widthValue.TryGetInt32(out var parsedWidth)
            ? parsedWidth : 0;
        var height = artwork.TryGetProperty("height", out var heightValue) && heightValue.TryGetInt32(out var parsedHeight)
            ? parsedHeight : 0;
        var communityScore = artwork.TryGetProperty("score", out var scoreValue) && scoreValue.TryGetInt32(out var score)
            ? score : 0;
        if (width <= 0 || height <= 0) return communityScore;
        var ratioPenalty = (int)(Math.Abs((double)width / height - 16d / 9d) * 300);
        var resolutionBonus = Math.Min(500, width * height / 10000);
        return communityScore + resolutionBonus - ratioPenalty - (width <= height ? 1000 : 0);
    }

    private static HttpRequestMessage CreateRequest(string url, string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }

    private static async Task<int?> FindSteamGridDbGameIdAsync(
        string baseUrl, string apiKey, string gameName, CancellationToken cancellationToken)
    {
        foreach (var query in BuildSearchQueries(gameName))
        {
            using var request = CreateRequest(
                $"{baseUrl}/search/autocomplete/{Uri.EscapeDataString(query)}", apiKey);
            using var response = await Client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) continue;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!json.RootElement.TryGetProperty("data", out var results) ||
                results.ValueKind != JsonValueKind.Array) continue;
            // Score against the query that produced these results. This matters for
            // abbreviated or decorated install names such as "AC Black Flag Resynced".
            var match = FindBestMatch(results, query);
            if (match is null && !query.Equals(gameName, StringComparison.OrdinalIgnoreCase))
                match = FindBestMatch(results, gameName);
            if (match is not null) return match;
        }
        return null;
    }

    private static IEnumerable<string> BuildSearchQueries(string gameName)
    {
        yield return gameName;
        var simplified = Regex.Replace(gameName,
            @"\b(?:resynced|remastered|remake|deluxe|ultimate|edition)\b", string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        simplified = Regex.Replace(simplified, @"\s+", " ").Trim();
        if (!simplified.Equals(gameName, StringComparison.OrdinalIgnoreCase)) yield return simplified;
        if (Regex.IsMatch(simplified, @"^AC\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            yield return Regex.Replace(simplified, @"^AC\b", "Assassin's Creed",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static int? FindBestMatch(JsonElement results, string gameName)
    {
        var wanted = Normalize(gameName);
        var matches = results.EnumerateArray()
            .Where(result => result.TryGetProperty("id", out _) && result.TryGetProperty("name", out _))
            .Select(result => new
            {
                Id = result.GetProperty("id").GetInt32(),
                Name = result.GetProperty("name").GetString() ?? string.Empty
            })
            .Select(result => new { result.Id, Score = MatchScore(wanted, Normalize(result.Name)) })
            .OrderByDescending(result => result.Score)
            .FirstOrDefault();
        return matches is { Score: >= 50 } ? matches.Id : null;
    }

    private static int MatchScore(string wanted, string candidate)
    {
        if (wanted == candidate) return 100;
        if (wanted.Contains(candidate, StringComparison.Ordinal) ||
            candidate.Contains(wanted, StringComparison.Ordinal)) return 80;
        var wantedTokens = Tokens(wanted);
        var candidateTokens = Tokens(candidate);
        if (wantedTokens.Count == 0 || candidateTokens.Count == 0) return 0;
        return (int)Math.Round(100d * wantedTokens.Intersect(candidateTokens).Count() /
                               Math.Max(wantedTokens.Count, candidateTokens.Count));
    }

    private static HashSet<string> Tokens(string value)
    {
        var ignored = new HashSet<string>(StringComparer.Ordinal)
            { "edition", "remastered", "remake", "deluxe", "ultimate", "resynced", "game" };
        var tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => !ignored.Contains(token))
            .ToHashSet(StringComparer.Ordinal);
        if (tokens.Remove("ac"))
        {
            tokens.Add("assassins");
            tokens.Add("creed");
        }
        return tokens;
    }

    private static string Normalize(string value)
    {
        // Apostrophes are part of a word here: "Assassin's" should normalize to
        // "assassins", not the two tokens "assassin s".
        value = value.Replace("'", string.Empty, StringComparison.Ordinal)
            .Replace("’", string.Empty, StringComparison.Ordinal);
        return Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]+", " ").Trim();
    }

    private static bool IsPortraitGrid(JsonElement grid)
    {
        if (!grid.TryGetProperty("width", out var widthValue) ||
            !grid.TryGetProperty("height", out var heightValue) ||
            !widthValue.TryGetInt32(out var width) || !heightValue.TryGetInt32(out var height)) return true;
        return height > width;
    }

    private static int GridQualityScore(JsonElement grid)
    {
        var score = grid.TryGetProperty("score", out var scoreValue) && scoreValue.TryGetInt32(out var votes)
            ? votes : 0;
        if (!grid.TryGetProperty("width", out var widthValue) ||
            !grid.TryGetProperty("height", out var heightValue) ||
            !widthValue.TryGetInt32(out var width) || !heightValue.TryGetInt32(out var height)) return score;
        var resolutionBonus = height >= 800 ? 100 : height / 10;
        var ratioPenalty = (int)(Math.Abs((double)width / height - 0.75) * 100);
        return score + resolutionBonus - ratioPenalty;
    }

    private static async Task<string?> DownloadArtworkAsync(
        string url, string cacheKey, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OmenGamingShell", "Metadata", "Downloads");
        var path = Path.Combine(folder, $"{cacheKey}.image");
        if (File.Exists(path)) return path;
        using var response = await Client.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        Directory.CreateDirectory(folder);
        await using var output = File.Create(path);
        await response.Content.CopyToAsync(output, cancellationToken);
        return path;
    }
}
