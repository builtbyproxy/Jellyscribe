using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using LetterboxdSync;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LetterboxdSync.Tests;

public class FilmSummaryHelperTests
{
    private static JsonElement Parse(string json)
    {
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public void ExtractTmdbIdFromLinks_ValidTmdbLink_ReturnsId()
    {
        var film = Parse(@"{
            ""links"": [
                { ""type"": ""letterboxd"", ""id"": ""KQMM"" },
                { ""type"": ""imdb"", ""id"": ""tt31193180"" },
                { ""type"": ""tmdb"", ""id"": ""1233413"" }
            ]
        }");

        Assert.Equal(1233413, LetterboxdApiClient.ExtractTmdbIdFromLinks(film));
    }

    [Fact]
    public void ExtractTmdbIdFromLinks_TmdbCaseInsensitive_ReturnsId()
    {
        var film = Parse(@"{ ""links"": [ { ""type"": ""TMDB"", ""id"": ""550"" } ] }");
        Assert.Equal(550, LetterboxdApiClient.ExtractTmdbIdFromLinks(film));
    }

    [Fact]
    public void ExtractTmdbIdFromLinks_NoTmdbLink_ReturnsNull()
    {
        var film = Parse(@"{
            ""links"": [
                { ""type"": ""letterboxd"", ""id"": ""KQMM"" },
                { ""type"": ""imdb"", ""id"": ""tt31193180"" }
            ]
        }");

        Assert.Null(LetterboxdApiClient.ExtractTmdbIdFromLinks(film));
    }

    [Fact]
    public void ExtractTmdbIdFromLinks_MissingLinks_ReturnsNull()
    {
        Assert.Null(LetterboxdApiClient.ExtractTmdbIdFromLinks(Parse(@"{ ""name"": ""Sinners"" }")));
    }

    [Fact]
    public void ExtractTmdbIdFromLinks_NonNumericTmdbId_ReturnsNull()
    {
        var film = Parse(@"{ ""links"": [ { ""type"": ""tmdb"", ""id"": ""junk"" } ] }");
        Assert.Null(LetterboxdApiClient.ExtractTmdbIdFromLinks(film));
    }

    // Regression: Letterboxd's API returns the same FilmSummary shape for TV-show
    // entries, with the tmdb link pointing at /tv/<id>. TMDb's movie and TV ID
    // namespaces are independent, so e.g. tv/198102 = "Hijack" and movie/198102 =
    // "Cutie Honey Flash". Without the /tv/ guard, watchlisting Hijack on
    // Letterboxd ends up auto-requesting Cutie Honey in Seerr.
    [Fact]
    public void ExtractTmdbIdFromLinks_TvShow_SkipsTvLink()
    {
        var film = Parse(@"{
            ""name"": ""Hijack"",
            ""links"": [
                { ""type"": ""letterboxd"", ""id"": ""HykO"", ""url"": ""https://letterboxd.com/film/hijack-2023/"" },
                { ""type"": ""imdb"", ""id"": ""tt19854762"", ""url"": ""https://www.imdb.com/title/tt19854762/"" },
                { ""type"": ""tmdb"", ""id"": ""198102"", ""url"": ""https://www.themoviedb.org/tv/198102"" }
            ]
        }");

        Assert.Null(LetterboxdApiClient.ExtractTmdbIdFromLinks(film));
    }

    [Fact]
    public void ExtractTmdbIdFromLinks_MovieUrl_ReturnsId()
    {
        var film = Parse(@"{
            ""links"": [
                { ""type"": ""tmdb"", ""id"": ""1233413"", ""url"": ""https://www.themoviedb.org/movie/1233413"" }
            ]
        }");

        Assert.Equal(1233413, LetterboxdApiClient.ExtractTmdbIdFromLinks(film));
    }

    [Fact]
    public void ExtractTmdbIdFromLinks_TmdbWithNoUrl_StillReturnsId()
    {
        // Older API responses sometimes omit `url`; without it we have no way to
        // disambiguate, so fall back to returning the id (caller's existing
        // movie-namespace assumption).
        var film = Parse(@"{ ""links"": [ { ""type"": ""tmdb"", ""id"": ""550"" } ] }");
        Assert.Equal(550, LetterboxdApiClient.ExtractTmdbIdFromLinks(film));
    }

    [Fact]
    public void ExtractTmdbIdFromLinks_MixedTvAndMovieLinks_PrefersMovie()
    {
        // Defensive: if the API ever returns both, take the movie one.
        var film = Parse(@"{
            ""links"": [
                { ""type"": ""tmdb"", ""id"": ""198102"", ""url"": ""https://www.themoviedb.org/tv/198102"" },
                { ""type"": ""tmdb"", ""id"": ""1233413"", ""url"": ""https://www.themoviedb.org/movie/1233413"" }
            ]
        }");

        Assert.Equal(1233413, LetterboxdApiClient.ExtractTmdbIdFromLinks(film));
    }

    [Fact]
    public void ExtractMemberRating_PresentRating_ReturnsValue()
    {
        var film = Parse(@"{
            ""relationships"": [
                {
                    ""member"": { ""id"": ""614Bn"" },
                    ""relationship"": {
                        ""watched"": true,
                        ""rating"": 2.0,
                        ""liked"": false
                    }
                }
            ]
        }");

        Assert.Equal(2.0, LetterboxdApiClient.ExtractMemberRating(film));
    }

    [Fact]
    public void ExtractMemberRating_HalfStar_ReturnsValue()
    {
        var film = Parse(@"{
            ""relationships"": [
                { ""relationship"": { ""rating"": 4.5 } }
            ]
        }");

        Assert.Equal(4.5, LetterboxdApiClient.ExtractMemberRating(film));
    }

    [Fact]
    public void ExtractMemberRating_NoRatingKey_ReturnsNull()
    {
        var film = Parse(@"{
            ""relationships"": [
                { ""relationship"": { ""watched"": true } }
            ]
        }");

        Assert.Null(LetterboxdApiClient.ExtractMemberRating(film));
    }

    [Fact]
    public void ExtractMemberRating_EmptyRelationships_ReturnsNull()
    {
        Assert.Null(LetterboxdApiClient.ExtractMemberRating(Parse(@"{ ""relationships"": [] }")));
    }

    [Fact]
    public void ExtractMemberRating_MissingRelationshipsKey_ReturnsNull()
    {
        Assert.Null(LetterboxdApiClient.ExtractMemberRating(Parse(@"{ ""name"": ""Sinners"" }")));
    }

    [Fact]
    public void ExtractMemberRating_RelationshipsWrongType_ReturnsNull()
    {
        Assert.Null(LetterboxdApiClient.ExtractMemberRating(Parse(@"{ ""relationships"": ""oops"" }")));
    }

    [Fact]
    public void ExtractMemberRating_RatingNotNumber_ReturnsNull()
    {
        var film = Parse(@"{
            ""relationships"": [
                { ""relationship"": { ""rating"": ""4.5"" } }
            ]
        }");

        Assert.Null(LetterboxdApiClient.ExtractMemberRating(film));
    }
}

public class GetDiaryFilmEntriesIntegrationTests
{
    private static readonly ILogger TestLogger = NullLoggerFactory.Instance.CreateLogger("test");

    /// <summary>
    /// Real shape of the /films?memberRelationship=Watched response we captured
    /// during the live API probe, three rated films, one unrated, one duplicate
    /// TMDb id (defensive: API has been seen to return dupes after edits).
    /// </summary>
    private const string SampleResponse = @"{
        ""next"": null,
        ""items"": [
            {
                ""type"": ""FilmSummary"",
                ""id"": ""KQMM"",
                ""name"": ""Sinners"",
                ""links"": [
                    { ""type"": ""letterboxd"", ""id"": ""KQMM"" },
                    { ""type"": ""tmdb"", ""id"": ""1233413"" }
                ],
                ""relationships"": [
                    { ""relationship"": { ""watched"": true, ""rating"": 2.0 } }
                ]
            },
            {
                ""type"": ""FilmSummary"",
                ""id"": ""2a9q"",
                ""name"": ""Iron Man"",
                ""links"": [
                    { ""type"": ""tmdb"", ""id"": ""1726"" }
                ],
                ""relationships"": [
                    { ""relationship"": { ""watched"": true, ""rating"": 4.5 } }
                ]
            },
            {
                ""type"": ""FilmSummary"",
                ""id"": ""abcd"",
                ""name"": ""Some Watched But Unrated Film"",
                ""links"": [
                    { ""type"": ""tmdb"", ""id"": ""99999"" }
                ],
                ""relationships"": [
                    { ""relationship"": { ""watched"": true } }
                ]
            },
            {
                ""type"": ""FilmSummary"",
                ""id"": ""KQMM"",
                ""name"": ""Sinners (duplicate row)"",
                ""links"": [
                    { ""type"": ""tmdb"", ""id"": ""1233413"" }
                ],
                ""relationships"": [
                    { ""relationship"": { ""watched"": true, ""rating"": 5.0 } }
                ]
            }
        ]
    }";

    [Fact]
    public async Task GetDiaryFilmEntriesAsync_ParsesFilmsAndRatings()
    {
        string? capturedQuery = null;

        var handler = ApiTestHelpers.CreateAuthenticatedHandler(extraHandler: (request) =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/films") == true &&
                request.Method == HttpMethod.Get)
            {
                capturedQuery = request.RequestUri.Query;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SampleResponse)
                };
            }
            return null;
        });

        using var client = new LetterboxdApiClient(TestLogger, handler);
        await client.AuthenticateAsync("user", "pass");
        var entries = await client.GetDiaryFilmEntriesAsync("user");

        // The /films endpoint must be called with the Watched relationship and
        // MemberRelationship include, that's what carries the per-member rating.
        Assert.NotNull(capturedQuery);
        Assert.Contains("memberRelationship=Watched", capturedQuery);
        Assert.Contains("include=MemberRelationship", capturedQuery);

        // Three unique TMDb IDs (Sinners deduped), parsed in input order.
        Assert.Equal(3, entries.Count);
        Assert.Equal(new[] { 1233413, 1726, 99999 }, entries.Select(e => e.TmdbId).ToArray());

        // Sinners kept its first rating (2.0), not the duplicate row's 5.0.
        var sinners = entries.First(e => e.TmdbId == 1233413);
        Assert.Equal(2.0, sinners.Rating);

        var ironMan = entries.First(e => e.TmdbId == 1726);
        Assert.Equal(4.5, ironMan.Rating);

        var unrated = entries.First(e => e.TmdbId == 99999);
        Assert.Null(unrated.Rating);
    }

    /// <summary>
    /// A fake Letterboxd list endpoint that behaves like the documented API, not like our
    /// assumptions about it (issues #109 and #125): it pages with an opaque cursor taken from
    /// the previous response's <c>next</c>, ignores any <c>start</c> parameter, and marks the
    /// last page with <c>"next": null</c>, as the real /films response does. Item ids are
    /// <c>f{i}</c> and TMDb ids <c>1000 + i</c>; the cursor values are deliberately not
    /// <c>start=N</c>, so a client that builds offsets itself instead of echoing <c>next</c>
    /// cannot pass.
    /// </summary>
    private static Func<HttpRequestMessage, HttpResponseMessage?> CursorPagedList(
        string pathPart, int total, List<string> queries, bool honourCursor = true)
        => request =>
        {
            if (request.RequestUri?.AbsolutePath.Contains(pathPart) != true || request.Method != HttpMethod.Get)
                return null;

            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            queries.Add(query);
            var m = System.Text.RegularExpressions.Regex.Match(query, @"[?&]cursor=opaque-(\d+)");
            var start = honourCursor && m.Success ? int.Parse(m.Groups[1].Value) : 0;

            var count = Math.Min(100, total - start);
            var items = string.Join(",", Enumerable.Range(start, count).Select(i =>
                $@"{{ ""type"": ""FilmSummary"", ""id"": ""f{i}"", ""links"": [ {{ ""type"": ""tmdb"", ""id"": ""{1000 + i}"" }} ],
                     ""relationships"": [ {{ ""relationship"": {{ ""rating"": 4.0 }} }} ] }}"));
            var next = start + count < total ? $@"""opaque-{start + count}""" : "null";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($@"{{ ""next"": {next}, ""items"": [{items}] }}")
            };
        };

    [Fact]
    public async Task GetDiaryFilmEntriesAsync_FollowsCursorAcrossAllPages()
    {
        // Issue #125: the diary import paged with `start=`, which the API ignores, so anyone with
        // more than 100 watched films only ever imported the first 100.
        var queries = new List<string>();
        var handler = ApiTestHelpers.CreateAuthenticatedHandler(extraHandler: CursorPagedList("/films", 250, queries));

        using var client = new LetterboxdApiClient(TestLogger, handler);
        await client.AuthenticateAsync("user", "pass");
        var entries = await client.GetDiaryFilmEntriesAsync("user");

        Assert.Equal(250, entries.Count);
        Assert.Equal(1000, entries[0].TmdbId);
        Assert.Equal(1249, entries[^1].TmdbId);
        Assert.Equal(4.0, entries[^1].Rating);
        Assert.Equal(3, queries.Count);
        Assert.DoesNotContain("cursor=", queries[0]);
        Assert.Contains("cursor=opaque-100", queries[1]);
        Assert.Contains("cursor=opaque-200", queries[2]);
        Assert.All(queries, q => Assert.Contains("memberRelationship=Watched", q));
    }

    [Fact]
    public async Task GetDiaryFilmEntriesAsync_EmptyResponse_ReturnsEmpty()
    {
        var handler = ApiTestHelpers.CreateAuthenticatedHandler(extraHandler: (request) =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/films") == true)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"{ ""items"": [] }")
                };
            }
            return null;
        });

        using var client = new LetterboxdApiClient(TestLogger, handler);
        await client.AuthenticateAsync("user", "pass");
        var entries = await client.GetDiaryFilmEntriesAsync("user");

        Assert.Empty(entries);
    }

    [Fact]
    public async Task GetWatchlistTmdbIdsAsync_FollowsCursorAcrossAllPages()
    {
        // Issue #125: a 1,011-film watchlist came back as 99 films. The client asked for later
        // pages with `start=N`, the API ignored it and returned page one each time, and dedupe
        // collapsed 50 identical pages into one. Pages must be requested with cursor=<next>.
        var queries = new List<string>();
        var handler = ApiTestHelpers.CreateAuthenticatedHandler(extraHandler: CursorPagedList("/watchlist", 1011, queries));

        using var client = new LetterboxdApiClient(TestLogger, handler);
        await client.AuthenticateAsync("user", "pass");
        var ids = await client.GetWatchlistTmdbIdsAsync("user");

        Assert.Equal(1011, ids.Count);
        Assert.Equal(1000, ids[0]);
        Assert.Equal(2010, ids[^1]);
        Assert.Equal(11, queries.Count);
        Assert.DoesNotContain("cursor=", queries[0]);
        Assert.Contains("cursor=opaque-1000", queries[^1]);
        Assert.All(queries, q => Assert.DoesNotContain("start=", q));
    }

    [Fact]
    public async Task GetWatchlistTmdbIdsAsync_NullNextOnSinglePage_MakesOneRequest()
    {
        // The real last page carries "next": null. Treating a present-but-null `next` as
        // "more pages" kept the loop requesting until its cap on every sync.
        var queries = new List<string>();
        var handler = ApiTestHelpers.CreateAuthenticatedHandler(extraHandler: CursorPagedList("/watchlist", 40, queries));

        using var client = new LetterboxdApiClient(TestLogger, handler);
        await client.AuthenticateAsync("user", "pass");
        var ids = await client.GetWatchlistTmdbIdsAsync("user");

        Assert.Equal(40, ids.Count);
        Assert.Single(queries);
    }

    /// <summary>Serves the nth watchlist request (0-based) the body from <paramref name="bodyFor"/>.</summary>
    private static Func<HttpRequestMessage, HttpResponseMessage?> ScriptedWatchlist(Func<int, string> bodyFor, List<string> queries)
        => request =>
        {
            if (request.RequestUri?.AbsolutePath.Contains("/watchlist") != true) return null;
            var n = queries.Count;
            queries.Add(Uri.UnescapeDataString(request.RequestUri.Query));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(bodyFor(n)) };
        };

    private static string Items(int from, int count)
        => string.Join(",", Enumerable.Range(from, count).Select(i =>
            $@"{{ ""id"": ""f{i}"", ""links"": [ {{ ""type"": ""tmdb"", ""id"": ""{1000 + i}"" }} ] }}"));

    private static async Task<InvalidOperationException> WatchlistReadFails(Func<int, string> bodyFor, List<string> queries)
    {
        var handler = ApiTestHelpers.CreateAuthenticatedHandler(extraHandler: ScriptedWatchlist(bodyFor, queries));
        using var client = new LetterboxdApiClient(TestLogger, handler);
        await client.AuthenticateAsync("user", "pass");
        return await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetWatchlistTmdbIdsAsync("user"));
    }

    // Every way a read can stop early must throw, never return a short list: watchlist sync
    // reconciles the playlist to whatever comes back, so a partial list would remove films.

    [Fact]
    public async Task GetWatchlistTmdbIdsAsync_ServerIgnoresCursor_Throws()
    {
        // The API ignoring the cursor (as it ignored `start=` in #125): same items, same next.
        var queries = new List<string>();
        var handler = ApiTestHelpers.CreateAuthenticatedHandler(
            extraHandler: CursorPagedList("/watchlist", 1011, queries, honourCursor: false));
        using var client = new LetterboxdApiClient(TestLogger, handler);
        await client.AuthenticateAsync("user", "pass");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetWatchlistTmdbIdsAsync("user"));

        Assert.Equal(2, queries.Count);
        Assert.Contains("Nothing was changed", ex.Message);
    }

    [Fact]
    public async Task GetWatchlistTmdbIdsAsync_SamePageWithFreshCursor_Throws()
    {
        // Isolates the "nothing new on this page" guard: the cursor never repeats.
        var queries = new List<string>();
        var ex = await WatchlistReadFails(n => $@"{{ ""next"": ""c{n + 1}"", ""items"": [{Items(0, 100)}] }}", queries);

        Assert.Equal(2, queries.Count);
        Assert.Contains("same page again", ex.Message);
    }

    [Fact]
    public async Task GetWatchlistTmdbIdsAsync_RepeatedCursorWithNewItems_Throws()
    {
        // Isolates the repeated-cursor guard: every page brings new items but the same marker.
        var queries = new List<string>();
        var ex = await WatchlistReadFails(n => $@"{{ ""next"": ""c1"", ""items"": [{Items(n * 100, 100)}] }}", queries);

        Assert.Equal(2, queries.Count);
        Assert.Contains("already followed", ex.Message);
    }

    [Fact]
    public async Task GetWatchlistTmdbIdsAsync_EmptyPageMidList_Throws()
    {
        var queries = new List<string>();
        var ex = await WatchlistReadFails(n => n == 0
            ? $@"{{ ""next"": ""c1"", ""items"": [{Items(0, 100)}] }}"
            : @"{ ""next"": ""c2"", ""items"": [] }", queries);

        Assert.Contains("empty page", ex.Message);
    }

    [Fact]
    public async Task GetWatchlistTmdbIdsAsync_UnreadableNext_Throws()
    {
        var queries = new List<string>();
        var ex = await WatchlistReadFails(_ => $@"{{ ""next"": 12345, ""items"": [{Items(0, 100)}] }}", queries);

        Assert.Single(queries);
        Assert.Contains("could not read", ex.Message);
    }

    [Fact]
    public async Task GetWatchlistTmdbIdsAsync_ResponseWithoutItems_Throws()
    {
        var queries = new List<string>();
        var ex = await WatchlistReadFails(_ => @"{ ""message"": ""Unauthorized"" }", queries);

        Assert.Contains("no list of items", ex.Message);
    }

    [Fact]
    public async Task GetWatchlistTmdbIdsAsync_NeverEndingList_StopsAtPageCap()
    {
        // Fresh items and a fresh cursor forever: only the page cap can stop it.
        var queries = new List<string>();
        var ex = await WatchlistReadFails(n => $@"{{ ""next"": ""c{n + 1}"", ""items"": [{Items(n * 100, 100)}] }}", queries);

        Assert.Equal(200, queries.Count);
        Assert.Contains("more than 200 pages", ex.Message);
    }

    [Fact]
    public async Task GetWatchlistTmdbIdsAsync_EmptyResponse_ReturnsEmpty()
    {
        var handler = ApiTestHelpers.CreateAuthenticatedHandler(extraHandler: (request) =>
        {
            if (request.RequestUri?.AbsolutePath.Contains("/watchlist") == true)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"{ ""items"": [] }")
                };
            return null;
        });

        using var client = new LetterboxdApiClient(TestLogger, handler);
        await client.AuthenticateAsync("user", "pass");
        var ids = await client.GetWatchlistTmdbIdsAsync("user");

        Assert.Empty(ids);
    }

    [Fact]
    public async Task GetWatchlistTmdbIdsAsync_ItemWithoutTmdbLink_Skipped()
    {
        // Some Letterboxd films don't have a TMDb link (especially older or obscure
        // titles). The watchlist fetcher should silently skip them rather than fail
        // the whole sync, so the rest of the watchlist still imports.
        var handler = ApiTestHelpers.CreateAuthenticatedHandler(extraHandler: (request) =>
        {
            if (request.RequestUri?.AbsolutePath.Contains("/watchlist") == true)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"{
                        ""items"": [
                            { ""id"": ""x"", ""links"": [ { ""type"": ""imdb"", ""id"": ""tt123"" } ] },
                            { ""id"": ""y"", ""links"": [ { ""type"": ""tmdb"", ""id"": ""550"" } ] }
                        ]
                    }")
                };
            return null;
        });

        using var client = new LetterboxdApiClient(TestLogger, handler);
        await client.AuthenticateAsync("user", "pass");
        var ids = await client.GetWatchlistTmdbIdsAsync("user");

        Assert.Equal(new[] { 550 }, ids.ToArray());
    }

    [Fact]
    public async Task GetDiaryTmdbIdsAsync_ReturnsJustTmdbIds()
    {
        var handler = ApiTestHelpers.CreateAuthenticatedHandler(extraHandler: (request) =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/films") == true)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SampleResponse)
                };
            }
            return null;
        });

        using var client = new LetterboxdApiClient(TestLogger, handler);
        await client.AuthenticateAsync("user", "pass");
        var ids = await client.GetDiaryTmdbIdsAsync("user");

        // Same dedup behaviour as the entries call, just stripped to ids.
        Assert.Equal(new[] { 1233413, 1726, 99999 }, ids.ToArray());
    }
}
