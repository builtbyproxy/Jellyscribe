using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace LetterboxdSync.Tests.Integration;

/// <summary>
/// Live probe of the official API's member film-relationship endpoint
/// (<c>GET</c>/<c>PATCH /film/{id}/me</c>), the research gate (tasks 1.1) of the
/// stream-ratings-to-letterboxd OpenSpec change. It records how the endpoint behaves
/// (rating scale, invalid values, watched/watchlist side effects, diary residue) as
/// FINDING lines in the test output, and restores the film's original relationship
/// state for the test account before finishing. Requests are signed here directly from
/// <see cref="LetterboxdApiConstants"/> so the probe needs no production surface.
/// </summary>
[Trait("Category", "Integration")]
public class RatingEndpointProbeTests
{
    private readonly ITestOutputHelper _output;

    public RatingEndpointProbeTests(ITestOutputHelper output) => _output = output;

    // The Godfather: stable, and not used by the diary write tests in LetterboxdLiveTests.
    private const int TmdbGodfather = 238;

    private sealed record Relationship(double? Rating, bool Watched, bool InWatchlist, bool Liked, int DiaryEntries);

    [SkippableFact]
    public async Task FilmRelationshipRating_Probe_RecordsBehaviourAndRestoresState()
    {
        var user = Environment.GetEnvironmentVariable("LETTERBOXD_TEST_USERNAME");
        var pass = Environment.GetEnvironmentVariable("LETTERBOXD_TEST_PASSWORD");
        Skip.If(string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass),
            "Skipping live probe: set LETTERBOXD_TEST_USERNAME and LETTERBOXD_TEST_PASSWORD to run.");

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LetterboxdSync/1.6");

        var tokenBody = $"grant_type=password&username={Uri.EscapeDataString(user!)}&password={Uri.EscapeDataString(pass!)}";
        var (tokenStatus, tokenJson) = await SendAsync(http, HttpMethod.Post, "/auth/token", null, tokenBody, "application/x-www-form-urlencoded", null);
        Skip.If(tokenStatus != 200, $"Skipping live probe: API auth returned HTTP {tokenStatus}.");
        var token = JsonDocument.Parse(tokenJson).RootElement.GetProperty("access_token").GetString()!;

        var (filmStatus, filmJson) = await SendAsync(http, HttpMethod.Get, "/films", $"filmId=tmdb%3A{TmdbGodfather}&perPage=1", null, null, token);
        Assert.Equal(200, filmStatus);
        var lid = JsonDocument.Parse(filmJson).RootElement.GetProperty("items")[0].GetProperty("id").GetString()!;
        Finding($"film tmdb:{TmdbGodfather} resolves to LID {lid}");

        var original = await GetRelationshipAsync(http, lid, token, "original");

        try
        {
            // 1. Set a rating on the 0.5..5.0 half-star scale.
            await PatchAsync(http, lid, token, "{\"rating\":3.5}", "set rating 3.5");
            var afterSet = await GetRelationshipAsync(http, lid, token, "after rating 3.5");
            Assert.Equal(3.5, afterSet.Rating);
            Finding($"rating set creates diary entries: {afterSet.DiaryEntries != original.DiaryEntries} " +
                    $"({original.DiaryEntries} -> {afterSet.DiaryEntries}); watched {original.Watched} -> {afterSet.Watched}");

            // 2. Same value again: expected to be a no-op.
            await PatchAsync(http, lid, token, "{\"rating\":3.5}", "set rating 3.5 again (idempotency)");
            await GetRelationshipAsync(http, lid, token, "after repeat 3.5");

            // 3. Off-scale value: does it 400, or 200 with an InvalidRatingValue message?
            await PatchAsync(http, lid, token, "{\"rating\":3.3}", "set rating 3.3 (invalid)");
            await GetRelationshipAsync(http, lid, token, "after invalid 3.3");

            // 4. Watchlist interaction: clear, unwatch, watchlist it, then rate.
            await PatchAsync(http, lid, token, "{\"rating\":null}", "clear rating (null)");
            await GetRelationshipAsync(http, lid, token, "after clear");
            await PatchAsync(http, lid, token, "{\"watched\":false}", "set watched false");
            await PatchAsync(http, lid, token, "{\"inWatchlist\":true}", "add to watchlist");
            var watchlisted = await GetRelationshipAsync(http, lid, token, "watchlisted, unrated");
            await PatchAsync(http, lid, token, "{\"rating\":4.0}", "rate a watchlisted film 4.0");
            var ratedFromWatchlist = await GetRelationshipAsync(http, lid, token, "after rating a watchlisted film");
            Finding($"rating a watchlisted film: inWatchlist {watchlisted.InWatchlist} -> {ratedFromWatchlist.InWatchlist}, " +
                    $"watched {watchlisted.Watched} -> {ratedFromWatchlist.Watched}");
        }
        finally
        {
            // Restore: rating off first (it forces watched), then watched, watchlist, liked, rating.
            await PatchAsync(http, lid, token, "{\"rating\":null}", "restore: clear rating");
            await PatchAsync(http, lid, token, $"{{\"watched\":{Bool(original.Watched)}}}", "restore: watched");
            await PatchAsync(http, lid, token, $"{{\"inWatchlist\":{Bool(original.InWatchlist)}}}", "restore: watchlist");
            await PatchAsync(http, lid, token, $"{{\"liked\":{Bool(original.Liked)}}}", "restore: liked");
            if (original.Rating.HasValue)
                await PatchAsync(http, lid, token,
                    $"{{\"rating\":{original.Rating.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}", "restore: rating");
        }

        var restored = await GetRelationshipAsync(http, lid, token, "restored");
        Assert.Equal(original, restored);
    }

    private async Task PatchAsync(HttpClient http, string lid, string token, string body, string label)
    {
        var (status, json) = await SendAsync(http, HttpMethod.Patch, $"/film/{lid}/me", null, body, "application/json", token);
        var messages = "none";
        try
        {
            if (JsonDocument.Parse(json).RootElement.TryGetProperty("messages", out var m))
                messages = m.GetRawText();
        }
        catch (JsonException)
        {
            messages = $"(non-JSON body: {json})";
        }
        Finding($"PATCH {label}: body {body} -> HTTP {status}, messages {messages}");
    }

    private async Task<Relationship> GetRelationshipAsync(HttpClient http, string lid, string token, string label)
    {
        var (status, json) = await SendAsync(http, HttpMethod.Get, $"/film/{lid}/me", null, null, null, token);
        Assert.Equal(200, status);
        var root = JsonDocument.Parse(json).RootElement;
        var rel = root.TryGetProperty("data", out var data) ? data : root;

        double? rating = rel.TryGetProperty("rating", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetDouble() : null;
        var result = new Relationship(
            rating,
            rel.TryGetProperty("watched", out var w) && w.GetBoolean(),
            rel.TryGetProperty("inWatchlist", out var iw) && iw.GetBoolean(),
            rel.TryGetProperty("liked", out var l) && l.GetBoolean(),
            rel.TryGetProperty("diaryEntries", out var d) && d.ValueKind == JsonValueKind.Array ? d.GetArrayLength() : 0);
        Finding($"GET {label}: {result}");
        return result;
    }

    private static async Task<(int Status, string Body)> SendAsync(HttpClient http, HttpMethod method, string path,
        string? query, string? body, string? contentType, string? token)
    {
        // Same signing scheme as LetterboxdApiClient.SendSignedAsync.
        var nonce = Guid.NewGuid().ToString();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var prefix = string.IsNullOrEmpty(query) ? string.Empty : query + "&";
        var url = $"{LetterboxdApiConstants.BaseUrl}{path}?{prefix}apikey={LetterboxdApiConstants.ApiKey}&nonce={nonce}&timestamp={timestamp}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(LetterboxdApiConstants.ApiSecret));
        var signature = Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{method.Method}\0{url}\0{body ?? string.Empty}")));

        using var request = new HttpRequestMessage(method, url + $"&signature={signature}");
        if (token != null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null && contentType != null)
            request.Content = new StringContent(body, Encoding.UTF8, contentType);

        using var response = await http.SendAsync(request);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static string Bool(bool b) => b ? "true" : "false";

    private void Finding(string line) => _output.WriteLine("FINDING " + line);
}
