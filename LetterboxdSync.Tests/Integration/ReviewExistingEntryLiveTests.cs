using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace LetterboxdSync.Tests.Integration;

/// <summary>
/// Live check that a review of a film already on the diary goes onto that entry
/// (<see cref="LetterboxdApiClient.AddReviewToDiaryEntryAsync"/>, a <c>PATCH /log-entry/{id}</c>)
/// rather than adding a second entry. It logs a film on a fixed past date, adds the review,
/// and reads the diary back through its own signed requests (not the client's parser). It then
/// deletes only the entries it created and restores the film's watched, liked, watchlist and
/// rating state, in a finally block so a failed assertion still leaves the account as it was.
/// The unit tests in ApiClientReviewExistingEntryTests mock the same request and reply shapes.
/// </summary>
[Trait("Category", "Integration")]
public class ReviewExistingEntryLiveTests
{
    private readonly ITestOutputHelper _output;

    public ReviewExistingEntryLiveTests(ITestOutputHelper output) => _output = output;

    // 12 Angry Men: stable, and used by no other live test (Pulp Fiction and The Godfather are),
    // so test classes running in parallel never touch the same film.
    private const int TmdbTwelveAngryMen = 389;
    private static readonly DateTime DiaryDate = new(2001, 2, 3);
    private const string DiaryDay = "2001-02-03";

    private sealed record Entry(string Id, string? Date, string? Review, double? Rating);

    private sealed record Relationship(double? Rating, bool Watched, bool InWatchlist, bool Liked);

    [SkippableFact]
    public async Task ReviewOfALoggedFilm_GoesOnThatEntry_AndAddsNoSecondEntry()
    {
        var user = Environment.GetEnvironmentVariable("LETTERBOXD_TEST_USERNAME");
        var pass = Environment.GetEnvironmentVariable("LETTERBOXD_TEST_PASSWORD");
        Skip.If(string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass),
            "Skipping live test: set LETTERBOXD_TEST_USERNAME and LETTERBOXD_TEST_PASSWORD to run. See LetterboxdSync.Tests/Integration/README.md");

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LetterboxdSync/1.6");
        var tokenBody = $"grant_type=password&username={Uri.EscapeDataString(user!)}&password={Uri.EscapeDataString(pass!)}";
        var (tokenStatus, tokenJson) = await RatingEndpointProbeTests.SendAsync(http, HttpMethod.Post, "/auth/token", null, tokenBody, "application/x-www-form-urlencoded", null);
        Skip.If(tokenStatus != 200, $"Skipping live test: API auth returned HTTP {tokenStatus}.");
        var token = JsonDocument.Parse(tokenJson).RootElement.GetProperty("access_token").GetString()!;
        var (meStatus, meJson) = await RatingEndpointProbeTests.SendAsync(http, HttpMethod.Get, "/me", null, null, null, token);
        Assert.Equal(200, meStatus);
        var memberId = JsonDocument.Parse(meJson).RootElement.GetProperty("member").GetProperty("id").GetString()!;

        using var client = new LetterboxdApiClient(new XunitLogger(_output));
        await client.AuthenticateAsync(user!, pass!);
        var film = await client.LookupFilmByTmdbIdAsync(TmdbTwelveAngryMen);
        // Ids only, never a token: a mismatch here explains a member-scoped 404 at a glance.
        _output.WriteLine($"member id: test {memberId}, client {(string.IsNullOrEmpty(client.MemberIdForTesting) ? "(empty)" : client.MemberIdForTesting)}; film {film.Slug} LID {film.FilmId}");
        Assert.Equal(memberId, client.MemberIdForTesting);

        var before = await ListAsync(http, token, memberId, film.FilmId);
        Skip.If(before.Any(e => e.Date == DiaryDay),
            $"Skipping live test: the test account already has an entry for {film.Slug} on {DiaryDay}, so the result would be ambiguous.");
        var originalRelationship = await GetRelationshipAsync(http, token, film.FilmId);
        var marker = $"integration-test-{Guid.NewGuid():N}";

        try
        {
            await client.MarkAsWatchedAsync(film.Slug, film.FilmId, DiaryDate, liked: false);
            await PollAsync(() => ListAsync(http, token, memberId, film.FilmId), l => l.Any(e => e.Date == DiaryDay));

            var result = await client.AddReviewToDiaryEntryAsync(TmdbTwelveAngryMen, DiaryDate, marker, containsSpoilers: false, rating: 3.5);
            Assert.Equal(ReviewAttachResult.Attached, result);

            var after = await PollAsync(() => ListAsync(http, token, memberId, film.FilmId),
                l => l.Any(e => e.Date == DiaryDay && e.Review?.Contains(marker, StringComparison.Ordinal) == true));
            var onDate = after.Where(e => e.Date == DiaryDay).ToList();
            var entry = Assert.Single(onDate);
            Assert.Contains(marker, entry.Review ?? string.Empty, StringComparison.Ordinal);
            Assert.Equal(3.5, entry.Rating);
            Assert.Equal(before.Count + 1, after.Count);

            // A second review of the same entry is refused, never written over the first.
            Assert.Equal(ReviewAttachResult.AlreadyReviewed,
                await client.AddReviewToDiaryEntryAsync(TmdbTwelveAngryMen, DiaryDate, marker + "-again", false, null));
        }
        finally
        {
            // Each step on its own, so a failed list or delete never skips restoring the film's state.
            // Only entries on the test's own date that were not there before are deleted: anything
            // else on the account is left alone, even an entry for this film added meanwhile.
            try
            {
                var keep = before.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var created in (await ListAsync(http, token, memberId, film.FilmId)).Where(e => e.Date == DiaryDay && !keep.Contains(e.Id)))
                {
                    var (status, _) = await RatingEndpointProbeTests.SendAsync(http, HttpMethod.Delete, $"/log-entry/{Uri.EscapeDataString(created.Id)}", null, null, null, token);
                    _output.WriteLine($"cleanup: DELETE /log-entry/{created.Id} -> HTTP {status}");
                    await Task.Delay(250);
                }
            }
            catch (Exception ex)
            {
                _output.WriteLine($"cleanup: deleting the test's entries failed: {ex.Message}");
            }

            await RestoreAsync(http, token, film.FilmId, originalRelationship);
        }

        var restored = await PollAsync(() => ListAsync(http, token, memberId, film.FilmId),
            l => l.Select(e => e.Id).OrderBy(i => i).SequenceEqual(before.Select(e => e.Id).OrderBy(i => i)));
        Assert.Equal(before.Select(e => e.Id).OrderBy(i => i), restored.Select(e => e.Id).OrderBy(i => i));
        Assert.Equal(originalRelationship, await GetRelationshipAsync(http, token, film.FilmId));
    }

    private static async Task<List<Entry>> ListAsync(HttpClient http, string token, string memberId, string lid)
    {
        var (status, json) = await RatingEndpointProbeTests.SendAsync(http, HttpMethod.Get, "/log-entries",
            $"member={Uri.EscapeDataString(memberId)}&film={Uri.EscapeDataString(lid)}&perPage=100", null, null, token);
        Assert.Equal(200, status);

        var entries = new List<Entry>();
        foreach (var item in JsonDocument.Parse(json).RootElement.GetProperty("items").EnumerateArray())
        {
            var date = item.TryGetProperty("diaryDetails", out var d) && d.TryGetProperty("diaryDate", out var dd) ? dd.GetString() : null;
            string? review = null;
            if (item.TryGetProperty("review", out var r) && r.ValueKind == JsonValueKind.Object)
                review = (r.TryGetProperty("lbml", out var lbml) ? lbml.GetString() : null) + " " + (r.TryGetProperty("text", out var text) ? text.GetString() : null);
            double? rating = item.TryGetProperty("rating", out var rt) && rt.ValueKind == JsonValueKind.Number ? rt.GetDouble() : null;
            entries.Add(new Entry(item.GetProperty("id").GetString()!, date, review, rating));
        }

        return entries;
    }

    private static async Task<Relationship> GetRelationshipAsync(HttpClient http, string token, string lid)
    {
        var (status, json) = await RatingEndpointProbeTests.SendAsync(http, HttpMethod.Get, $"/film/{lid}/me", null, null, null, token);
        Assert.Equal(200, status);
        var root = JsonDocument.Parse(json).RootElement;
        var rel = root.TryGetProperty("data", out var data) ? data : root;
        return new Relationship(
            rel.TryGetProperty("rating", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetDouble() : null,
            rel.TryGetProperty("watched", out var w) && w.GetBoolean(),
            rel.TryGetProperty("inWatchlist", out var iw) && iw.GetBoolean(),
            rel.TryGetProperty("liked", out var l) && l.GetBoolean());
    }

    // As RatingEndpointProbeTests restores: the rating off first (setting one forces watched),
    // then watched, watchlist, liked, and the original rating last.
    private async Task RestoreAsync(HttpClient http, string token, string lid, Relationship original)
    {
        var patches = new List<string>
        {
            "{\"rating\":null}",
            $"{{\"watched\":{Bool(original.Watched)}}}",
            $"{{\"inWatchlist\":{Bool(original.InWatchlist)}}}",
            $"{{\"liked\":{Bool(original.Liked)}}}",
        };
        if (original.Rating.HasValue)
            patches.Add($"{{\"rating\":{original.Rating.Value.ToString(CultureInfo.InvariantCulture)}}}");

        foreach (var body in patches)
        {
            try
            {
                var (status, _) = await RatingEndpointProbeTests.SendAsync(http, HttpMethod.Patch, $"/film/{lid}/me", null, body, "application/json", token);
                _output.WriteLine($"restore: PATCH /film/{lid}/me {body} -> HTTP {status}");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"restore: PATCH /film/{lid}/me {body} failed: {ex.Message}");
            }
        }
    }

    /// <summary>Re-reads every 2s until <paramref name="done"/> holds or 30s pass: diary reads lag writes.</summary>
    private static async Task<T> PollAsync<T>(Func<Task<T>> read, Func<T, bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var value = await read();
            if (done(value) || DateTime.UtcNow >= deadline)
                return value;
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }

    private static string Bool(bool b) => b ? "true" : "false";
}
