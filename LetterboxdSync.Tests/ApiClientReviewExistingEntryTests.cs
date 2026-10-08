using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// LetterboxdApiClient.AddReviewToDiaryEntryAsync: a review of a film already on the diary goes
/// onto that entry with PATCH /log-entry/{id}, never as a second POST /log-entries. The mocked
/// replies follow the shapes the live test (Integration/ReviewExistingEntryLiveTests) reads back:
/// GET /log-entries lists LogEntry objects with diaryDetails.diaryDate as yyyy-MM-dd and an
/// optional review object; PATCH answers with a LogEntryUpdateResponse (data + messages).
/// </summary>
public class ApiClientReviewExistingEntryTests
{
    // Distinct from every other test's TMDb ids: the film lookup cache is process-wide.
    private const int TmdbId = 389;
    private const string Lid = "2b8k";
    private static readonly DateTime Watched = new(2024, 3, 9);

    private sealed record Sent(HttpMethod Method, string Path, string Query, string? Body);

    private static string Entry(string id, string date, string? reviewLbml = null, double? rating = null)
    {
        var entry = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["name"] = "12 Angry Men",
            ["film"] = new { id = Lid, name = "12 Angry Men" },
            ["diaryDetails"] = new { diaryDate = date, rewatch = false },
            ["like"] = false,
            ["whenCreated"] = date + "T20:00:00Z",
        };
        if (reviewLbml != null)
            entry["review"] = new { lbml = reviewLbml, text = $"<p>{reviewLbml}</p>", containsSpoilers = false, spoilersLocked = false, moderated = false };
        if (rating.HasValue)
            entry["rating"] = rating.Value;
        return JsonSerializer.Serialize(entry);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body) };

    private static async Task<(LetterboxdApiClient Client, List<Sent> Sent)> ClientAsync(
        string[] entries, HttpStatusCode listStatus = HttpStatusCode.OK, Func<string, HttpResponseMessage>? patch = null)
    {
        LetterboxdApiClient.ResetFilmCacheForTesting(TmdbId);
        var sent = new List<Sent>();
        var handler = ApiTestHelpers.CreateAuthenticatedHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content?.ReadAsStringAsync().Result;
            if (path.EndsWith("/films", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    items = new[]
                    {
                        new
                        {
                            id = Lid,
                            name = "12 Angry Men",
                            links = new[] { new { type = "letterboxd", id = Lid, url = "https://letterboxd.com/film/12-angry-men/" } }
                        }
                    }
                }));
            if (path.EndsWith("/auth/token", StringComparison.Ordinal) || path.EndsWith("/me", StringComparison.Ordinal))
                return null;

            sent.Add(new Sent(request.Method, path, request.RequestUri.Query, body));
            if (path.EndsWith("/log-entries", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
                return Json(listStatus, listStatus == HttpStatusCode.OK ? $"{{\"items\":[{string.Join(',', entries)}]}}" : "{}");
            if (path.Contains("/log-entry/", StringComparison.Ordinal) && request.Method == HttpMethod.Patch)
                return patch?.Invoke(body!) ?? Json(HttpStatusCode.OK, $"{{\"data\":{entries.Last()},\"messages\":[]}}");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var client = new LetterboxdApiClient(NullLogger.Instance, handler) { EntryReadRetryDelay = TimeSpan.Zero };
        await client.AuthenticateAsync("review-existing-user", "pass");
        return (client, sent);
    }

    [Fact]
    public async Task EntryOnThatDate_IsPatchedWithTheReviewAndRating_AndNothingIsLogged()
    {
        var (client, sent) = await ClientAsync(new[]
        {
            Entry("newer", "2025-01-02"),
            Entry("ours", "2024-03-09", rating: 3.0),
        });
        using var owned = client;

        var result = await client.AddReviewToDiaryEntryAsync(TmdbId, Watched, "A tense room.", containsSpoilers: true, rating: 4.5);

        Assert.Equal(ReviewAttachResult.Attached, result);
        var list = sent.Single(s => s.Method == HttpMethod.Get);
        Assert.Contains("member=mock-member", list.Query);
        Assert.Contains($"film={Lid}", list.Query);

        var patch = Assert.Single(sent, s => s.Method == HttpMethod.Patch);
        Assert.EndsWith("/log-entry/ours", patch.Path);
        using var doc = JsonDocument.Parse(patch.Body!);
        var root = doc.RootElement;
        Assert.Equal("A tense room.", root.GetProperty("review").GetProperty("text").GetString());
        Assert.True(root.GetProperty("review").GetProperty("containsSpoilers").GetBoolean());
        Assert.Equal(4.5, root.GetProperty("rating").GetDouble());
        // Only the review and rating change: the entry keeps its date, like and tags.
        Assert.Equal(new[] { "rating", "review" }, root.EnumerateObject().Select(p => p.Name).OrderBy(n => n));
        Assert.DoesNotContain(sent, s => s.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task NoRating_LeavesTheEntrysRatingOutOfTheUpdate()
    {
        var (client, sent) = await ClientAsync(new[] { Entry("ours", "2024-03-09", rating: 3.0) });
        using var owned = client;

        await client.AddReviewToDiaryEntryAsync(TmdbId, Watched, "Words only.", false, rating: null);

        using var doc = JsonDocument.Parse(Assert.Single(sent, s => s.Method == HttpMethod.Patch).Body!);
        Assert.False(doc.RootElement.TryGetProperty("rating", out _));
    }

    [Fact]
    public async Task NoEntryOnThatDate_ChangesNothing()
    {
        var (client, sent) = await ClientAsync(new[] { Entry("other", "2024-03-10") });
        using var owned = client;

        var result = await client.AddReviewToDiaryEntryAsync(TmdbId, Watched, "A tense room.", false, 4.0);

        Assert.Equal(ReviewAttachResult.NoEntry, result);
        // Read twice, since diary reads lag writes, before answering that there is none.
        Assert.Equal(2, sent.Count(s => s.Method == HttpMethod.Get));
        Assert.DoesNotContain(sent, s => s.Method != HttpMethod.Get);
    }

    [Fact]
    public async Task AnEntryNotListedYet_IsFoundOnTheSecondRead()
    {
        LetterboxdApiClient.ResetFilmCacheForTesting(TmdbId);
        var reads = 0;
        var patched = new List<string>();
        var handler = ApiTestHelpers.CreateAuthenticatedHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/films", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"items\":[{\"id\":\"" + Lid + "\",\"name\":\"12 Angry Men\",\"links\":[]}]}");
            if (path.EndsWith("/log-entries", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, ++reads == 1 ? "{\"items\":[]}" : "{\"items\":[" + Entry("fresh", "2024-03-09") + "]}");
            if (request.Method == HttpMethod.Patch)
            {
                patched.Add(path);
                return Json(HttpStatusCode.OK, "{\"data\":{},\"messages\":[]}");
            }
            return null;
        });
        using var client = new LetterboxdApiClient(NullLogger.Instance, handler) { EntryReadRetryDelay = TimeSpan.Zero };
        await client.AuthenticateAsync("review-existing-user", "pass");

        Assert.Equal(ReviewAttachResult.Attached, await client.AddReviewToDiaryEntryAsync(TmdbId, Watched, "Words.", false, null));
        Assert.EndsWith("/log-entry/fresh", Assert.Single(patched));
    }

    [Fact]
    public async Task EntryWithAReview_IsNeverOverwritten()
    {
        var (client, sent) = await ClientAsync(new[] { Entry("ours", "2024-03-09", reviewLbml: "Written on Letterboxd.") });
        using var owned = client;

        var result = await client.AddReviewToDiaryEntryAsync(TmdbId, Watched, "A tense room.", false, 4.0);

        Assert.Equal(ReviewAttachResult.AlreadyReviewed, result);
        Assert.DoesNotContain(sent, s => s.Method != HttpMethod.Get);
    }

    [Fact]
    public async Task TwoEntriesOnThatDate_TheUnreviewedOneGetsTheReview()
    {
        var (client, sent) = await ClientAsync(new[]
        {
            Entry("reviewed", "2024-03-09", reviewLbml: "First viewing."),
            Entry("plain", "2024-03-09"),
        });
        using var owned = client;

        Assert.Equal(ReviewAttachResult.Attached, await client.AddReviewToDiaryEntryAsync(TmdbId, Watched, "Second look.", false, null));
        Assert.EndsWith("/log-entry/plain", Assert.Single(sent, s => s.Method == HttpMethod.Patch).Path);
    }

    [Fact]
    public async Task UnreadableDiary_Throws_InsteadOfReportingNoEntry()
    {
        // "No entry" would make the caller log a new one, the duplicate this exists to prevent.
        var (client, sent) = await ClientAsync(Array.Empty<string>(), listStatus: HttpStatusCode.InternalServerError);
        using var owned = client;

        await Assert.ThrowsAnyAsync<Exception>(() => client.AddReviewToDiaryEntryAsync(TmdbId, Watched, "A tense room.", false, 4.0));
        Assert.DoesNotContain(sent, s => s.Method != HttpMethod.Get);
    }

    [Fact]
    public async Task RefusedUpdate_Throws_WithoutQuotingTheReview()
    {
        const string review = "A very private opinion.";
        var (client, _) = await ClientAsync(new[] { Entry("ours", "2024-03-09") },
            patch: _ => Json(HttpStatusCode.OK,
                "{\"data\":{},\"messages\":[{\"type\":\"Error\",\"code\":\"InvalidRatingValue\",\"title\":\"" + review + " is not allowed\"}]}"));
        using var owned = client;

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => client.AddReviewToDiaryEntryAsync(TmdbId, Watched, review, false, 4.0));

        Assert.Contains("InvalidRatingValue", ex.Message);
        Assert.DoesNotContain(review, ex.Message);
    }

    [Fact]
    public async Task FailedUpdate_Throws_WithTheStatusOnly_NeverTheEchoedReview()
    {
        const string review = "Line one\n\"quoted\" caf\u00e9";
        var (client, _) = await ClientAsync(new[] { Entry("ours", "2024-03-09") },
            patch: body => Json(HttpStatusCode.BadRequest, "{\"message\":\"invalid\",\"echo\":" + body + "}"));
        using var owned = client;

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => client.AddReviewToDiaryEntryAsync(TmdbId, Watched, review, false, null));

        Assert.Contains("BadRequest", ex.Message);
        Assert.DoesNotContain("quoted", ex.Message);
        Assert.DoesNotContain("Line one", ex.Message);
    }

    [Fact]
    public async Task EntriesOnALaterPage_AreFound()
    {
        LetterboxdApiClient.ResetFilmCacheForTesting(TmdbId);
        var patched = new List<string>();
        var handler = ApiTestHelpers.CreateAuthenticatedHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/films", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"items\":[{\"id\":\"" + Lid + "\",\"name\":\"12 Angry Men\",\"links\":[]}]}");
            if (path.EndsWith("/log-entries", StringComparison.Ordinal))
            {
                // Letterboxd pages with an opaque cursor: next goes back as cursor.
                return request.RequestUri.Query.Contains("cursor=page2", StringComparison.Ordinal)
                    ? Json(HttpStatusCode.OK, "{\"items\":[" + Entry("old", "2024-03-09") + "]}")
                    : Json(HttpStatusCode.OK, "{\"next\":\"page2\",\"items\":[" + Entry("new", "2025-06-01") + "]}");
            }
            if (request.Method == HttpMethod.Patch)
            {
                patched.Add(path);
                return Json(HttpStatusCode.OK, "{\"data\":{},\"messages\":[]}");
            }
            return null;
        });
        using var client = new LetterboxdApiClient(NullLogger.Instance, handler) { EntryReadRetryDelay = TimeSpan.Zero };
        await client.AuthenticateAsync("review-existing-user", "pass");

        Assert.Equal(ReviewAttachResult.Attached, await client.AddReviewToDiaryEntryAsync(TmdbId, Watched, "Words.", false, null));
        Assert.EndsWith("/log-entry/old", Assert.Single(patched));
    }

    [Fact]
    public async Task ListedEntries_AreReadWithTheirDateAndWhetherTheyHaveAReview()
    {
        var (client, _) = await ClientAsync(new[]
        {
            Entry("a", "2024-03-09", reviewLbml: "Kept.", rating: 4.0),
            Entry("b", "2024-03-10"),
        });
        using var owned = client;

        var entries = await client.GetMemberLogEntriesAsync(Lid);

        Assert.Equal(new[]
        {
            new LetterboxdApiClient.LogEntrySummary("a", Watched, true),
            new LetterboxdApiClient.LogEntrySummary("b", Watched.AddDays(1), false),
        }, entries);
    }

    [Fact]
    public async Task AnEmptyReviewObject_StillCountsAsAReview()
    {
        var withEmptyReview = Entry("ours", "2024-03-09").TrimEnd('}') + ",\"review\":{}}";
        var (client, sent) = await ClientAsync(new[] { withEmptyReview });
        using var owned = client;

        Assert.Equal(ReviewAttachResult.AlreadyReviewed, await client.AddReviewToDiaryEntryAsync(TmdbId, Watched, "Words.", false, null));
        Assert.DoesNotContain(sent, s => s.Method != HttpMethod.Get);
    }
}
