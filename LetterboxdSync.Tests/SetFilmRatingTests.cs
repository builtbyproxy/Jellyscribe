using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using LetterboxdSync;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using DiaryMockHandler = LetterboxdSync.Tests.DiaryOperationTests.DiaryMockHandler;

namespace LetterboxdSync.Tests;

/// <summary>
/// SetFilmRatingAsync on both implementations. The two endpoints use different wire scales
/// (official API: half-stars; site rate action: 0-10 integer), verified live for the API in
/// RatingEndpointProbeTests. The parity test pins that the same half-star input lands as the
/// same rating on both paths.
/// </summary>
public class SetFilmRatingTests
{
    private static readonly ILogger TestLogger = NullLoggerFactory.Instance.CreateLogger("test");
    private static readonly Uri BaseUri = new("https://letterboxd.com/");

    // ----- Official API -----

    private static async Task<(string? Method, string? Path, string? Body)> CaptureApiPatch(
        double rating, HttpResponseMessage response)
    {
        string? method = null, path = null, body = null;
        var handler = ApiTestHelpers.CreateAuthenticatedHandler(extraHandler: request =>
        {
            // Only the rating PATCH: authentication itself GETs /me for the member id.
            var p = request.RequestUri?.AbsolutePath ?? "";
            if (request.Method != HttpMethod.Patch || !p.Contains("/film/")) return null;
            method = request.Method.Method;
            path = p;
            body = request.Content?.ReadAsStringAsync().Result;
            return response;
        });
        using var client = new LetterboxdApiClient(TestLogger, handler);
        await client.AuthenticateAsync("user", "pass");
        await client.SetFilmRatingAsync("sinners-2025", "KQMM", rating);
        return (method, path, body);
    }

    [Fact]
    public async Task Api_PatchesFilmRelationshipWithHalfStars()
    {
        var (method, path, body) = await CaptureApiPatch(3.5,
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":{\"rating\":3.5},\"messages\":[]}") });

        Assert.Equal("PATCH", method);
        Assert.EndsWith("/film/KQMM/me", path);
        Assert.Equal("{\"rating\":3.5}", body);
    }

    [Fact]
    public async Task Api_WholeStarRating_SerializesAsANumber()
    {
        var (_, _, body) = await CaptureApiPatch(4.0,
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"messages\":[]}") });

        Assert.Equal(4.0, System.Text.Json.JsonDocument.Parse(body!).RootElement.GetProperty("rating").GetDouble());
    }

    [Fact]
    public async Task Api_ErrorMessageOnHttp200_Throws()
    {
        // Verified live: an off-scale value returns 200 with this message and leaves the rating unchanged.
        var ex = await Assert.ThrowsAsync<Exception>(() => CaptureApiPatch(3.5,
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"messages\":[{\"type\":\"Error\",\"code\":\"InvalidRatingValue\",\"title\":\"Rating must be a number between 0.5 and 5.0, with increments of 0.5.\"}]}")
            }));

        Assert.Contains("InvalidRatingValue", ex.Message);
    }

    [Fact]
    public async Task Api_Unauthorized_Throws()
    {
        var ex = await Assert.ThrowsAsync<Exception>(() => CaptureApiPatch(3.5, new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        Assert.Contains("token expired", ex.Message);
    }

    [Fact]
    public async Task Api_ServerError_Throws()
    {
        await Assert.ThrowsAsync<Exception>(() => CaptureApiPatch(3.5, new HttpResponseMessage(HttpStatusCode.InternalServerError)));
    }

    [Theory]
    [InlineData("{\"messages\":[]}", null)]
    [InlineData("{\"data\":{}}", null)]
    [InlineData("", null)]
    [InlineData("not json", null)]
    [InlineData("{\"messages\":[{\"type\":\"Success\",\"code\":\"X\",\"title\":\"ok\"}]}", null)]
    [InlineData("{\"messages\":[{\"type\":\"Error\",\"code\":\"UnableToRemoveWatch\",\"title\":\"nope\"}]}", "UnableToRemoveWatch: nope")]
    public void Api_ExtractRelationshipUpdateError(string json, string? expected)
    {
        Assert.Equal(expected, LetterboxdApiClient.ExtractRelationshipUpdateError(json));
    }

    // ----- Scraping (site rate action) -----

    private static async Task<(string? Path, string? Body)> CaptureSitePost(double rating, HttpResponseMessage response)
    {
        string? path = null, body = null;
        var handler = new DiaryMockHandler((request, http) =>
        {
            var p = request.RequestUri?.PathAndQuery ?? "";
            if (request.Method == HttpMethod.Get && p == "/")
            {
                http.CookieContainer.Add(BaseUri, new Cookie("com.xk72.webparts.csrf", "csrf-token"));
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            if (request.Method == HttpMethod.Post && p.Contains("/rate/"))
            {
                path = p;
                body = request.Content?.ReadAsStringAsync().Result;
                return response;
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var (http, _, _, diary) = handler.CreateClients(TestLogger);
        using var _ = http;
        await diary.SetFilmRatingAsync("sinners-2025", "51561", rating);
        return (path, body);
    }

    [Fact]
    public async Task Site_PostsRateActionWithDoubledIntegerAndCsrf()
    {
        var (path, body) = await CaptureSitePost(3.5,
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"result\":true}") });

        Assert.Equal("/s/film:51561/rate/", path);
        Assert.Equal("rating=7&__csrf=csrf-token", body);
    }

    [Fact]
    public async Task Site_HtmlPageOnHttp200_Throws()
    {
        // A sign-in or Cloudflare challenge page served with 200 must not count as rated.
        var ex = await Assert.ThrowsAsync<Exception>(() => CaptureSitePost(3.5,
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html><body>Sign in</body></html>") }));
        Assert.Contains("non-JSON", ex.Message);
    }

    [Fact]
    public async Task Site_ResultFalseOnHttp200_Throws()
    {
        var ex = await Assert.ThrowsAsync<Exception>(() => CaptureSitePost(3.5,
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"result\":false,\"messages\":[\"Please sign in\"]}") }));
        Assert.Contains("Please sign in", ex.Message);
    }

    [Fact]
    public async Task Site_ServerError_Throws()
    {
        await Assert.ThrowsAsync<Exception>(() => CaptureSitePost(3.5, new HttpResponseMessage(HttpStatusCode.InternalServerError)));
    }

    [Theory]
    [InlineData("{\"result\":true}", null)]
    [InlineData("{\"result\":true,\"csrf\":\"x\"}", null)]
    [InlineData("<html></html>", "non-JSON response (likely a sign-in or challenge page)")]
    [InlineData("", "empty response")]
    [InlineData("{}", "result was not true")]
    [InlineData("{\"result\":false}", "result was not true")]
    [InlineData("{\"result\":false,\"messages\":[\"a\",\"b\"]}", "a; b")]
    [InlineData("{not json", "unparseable JSON response")]
    public void Site_SiteActionFailure(string body, string? expected)
    {
        Assert.Equal(expected, LetterboxdDiary.SiteActionFailure(body));
    }

    // ----- Parity -----

    [Theory]
    [InlineData(0.5, 1)]
    [InlineData(1.0, 2)]
    [InlineData(2.5, 5)]
    [InlineData(3.5, 7)]
    [InlineData(4.5, 9)]
    [InlineData(5.0, 10)]
    public async Task Parity_SameHalfStarsLandAsSameRatingOnBothPaths(double stars, int siteValue)
    {
        var (_, _, apiBody) = await CaptureApiPatch(stars,
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"messages\":[]}") });
        var (_, siteBody) = await CaptureSitePost(stars,
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"result\":true}") });

        var apiStars = System.Text.Json.JsonDocument.Parse(apiBody!).RootElement.GetProperty("rating").GetDouble();
        Assert.Equal(stars, apiStars);
        Assert.StartsWith($"rating={siteValue}&", siteBody);
        Assert.Equal(apiStars * 2, siteValue);
    }
}
