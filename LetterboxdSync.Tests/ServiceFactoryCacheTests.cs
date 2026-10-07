using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using LetterboxdSync;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// The factory remembers, per account, that the official API login failed while the website
/// worked, and keeps the website session's cookies, so each playback or rating event does not
/// try the API and then log in to the website from scratch.
/// </summary>
[Collection("Plugin")]
public class ServiceFactoryCacheTests : IDisposable
{
    private static readonly ILogger Log = NullLoggerFactory.Instance.CreateLogger("test");
    private static readonly Func<ILogger, ILetterboxdService> DefaultApi = LetterboxdServiceFactory.CreateApiClient;
    private static readonly Func<ILogger, string?, CookieContainer, ILetterboxdService> DefaultWebsite = LetterboxdServiceFactory.CreateWebsiteClient;
    private static readonly Func<DateTime> DefaultClock = LetterboxdServiceFactory.UtcNow;

    private readonly List<CookieContainer> _jars = new();
    private int _apiLogins;
    private DateTime _now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private bool _websiteFails;
    private Exception _apiFailure = new LetterboxdApiAuthException(HttpStatusCode.Unauthorized,
        "Letterboxd API auth failed (Unauthorized): {\"error\":\"invalid_grant\"}");

    public ServiceFactoryCacheTests()
    {
        LetterboxdServiceFactory.ResetCachesForTesting();
        LetterboxdServiceFactory.UtcNow = () => _now;
        LetterboxdServiceFactory.CreateApiClient = _ =>
        {
            var api = Substitute.For<ILetterboxdService>();
            api.AuthenticateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
                .Returns(_ =>
                {
                    _apiLogins++;
                    return Task.FromException(_apiFailure);
                });
            return api;
        };
        LetterboxdServiceFactory.CreateWebsiteClient = (_, _, jar) =>
        {
            _jars.Add(jar);
            var site = Substitute.For<ILetterboxdService>();
            if (_websiteFails)
                site.AuthenticateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
                    .ThrowsAsync(new Exception("Letterboxd returned 403 during login"));
            return site;
        };
    }

    public void Dispose()
    {
        LetterboxdServiceFactory.CreateApiClient = DefaultApi;
        LetterboxdServiceFactory.CreateWebsiteClient = DefaultWebsite;
        LetterboxdServiceFactory.UtcNow = DefaultClock;
        LetterboxdServiceFactory.ResetCachesForTesting();
    }

    private static Task<ILetterboxdService> Create(string password = "secret")
        => LetterboxdServiceFactory.CreateAuthenticatedAsync("demo-cinephile", password, null, Log);

    [Fact]
    public async Task ApiFailedButWebsiteWorked_LaterEventsGoStraightToTheWebsiteSession()
    {
        await Create();
        await Create();
        await Create();

        Assert.Equal(1, _apiLogins);
        Assert.Equal(3, _jars.Count);
        Assert.Same(_jars[0], _jars[1]);
        Assert.Same(_jars[0], _jars[2]);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task ATransientApiFailure_IsNotRemembered(HttpStatusCode status)
    {
        _apiFailure = new LetterboxdApiAuthException(status, $"Letterboxd API auth failed ({status})");
        await Create();
        await Create();

        Assert.Equal(2, _apiLogins);
    }

    [Fact]
    public async Task ANetworkErrorOnTheApi_IsNotRemembered()
    {
        _apiFailure = new System.Net.Http.HttpRequestException("Connection refused");
        await Create();
        await Create();

        Assert.Equal(2, _apiLogins);
    }

    [Fact]
    public async Task ApiIsTriedAgainOnceTheMemoryExpires()
    {
        await Create();
        _now += LetterboxdServiceFactory.ApiUnavailableFor + TimeSpan.FromMinutes(1);
        await Create();

        Assert.Equal(2, _apiLogins);
    }

    [Fact]
    public async Task ANewPassword_GetsANewSessionAndTriesTheApi()
    {
        await Create("secret");
        await Create("new-secret");

        Assert.Equal(2, _apiLogins);
        Assert.NotSame(_jars[0], _jars[1]);
    }

    [Theory]
    [InlineData("other-cookies", null)]
    [InlineData(null, "Mozilla/5.0 (X11; Linux x86_64) Firefox/140.0")]
    public async Task NewRawCookiesOrUserAgent_GetANewSession(string? rawCookies, string? userAgent)
    {
        await LetterboxdServiceFactory.CreateAuthenticatedAsync("demo-cinephile", "secret", null, Log);
        await LetterboxdServiceFactory.CreateAuthenticatedAsync("demo-cinephile", "secret", rawCookies, Log, userAgent);

        Assert.NotSame(_jars[0], _jars[1]);
    }

    [Fact]
    public async Task WebsiteLoginFails_ForgetsTheSessionAndDoesNotBlameTheApi()
    {
        _websiteFails = true;
        await Assert.ThrowsAsync<Exception>(() => Create());
        _websiteFails = false;
        await Create();

        Assert.Equal(2, _apiLogins);
        Assert.NotSame(_jars[0], _jars[1]);
    }

    // What the kept cookie jar buys: a new website service over a signed-in jar checks the session
    // with one page request and never posts the login form again.
    [Fact]
    public async Task WebsiteServiceOverASignedInJar_ReusesTheSessionWithoutLoggingIn()
    {
        var jar = new CookieContainer();
        jar.Add(LetterboxdHttpClient.BaseUri, new Cookie("letterboxd.user.CURRENT", "session-value", "/", "letterboxd.com"));
        jar.Add(LetterboxdHttpClient.BaseUri, new Cookie("com.xk72.webparts.csrf", "csrf-value", "/", "letterboxd.com"));
        var paths = new List<string>();
        var handler = new RecordingHandler(paths);

        using var service = new ScrapingLetterboxdService(Log, handler, null, jar);
        await service.AuthenticateAsync("demo-cinephile", "secret");

        Assert.Equal(new[] { "/" }, paths);
    }

    private sealed class RecordingHandler : System.Net.Http.HttpMessageHandler
    {
        private readonly List<string> _paths;

        public RecordingHandler(List<string> paths) => _paths = paths;

        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
        {
            _paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent("<html></html>")
            });
        }
    }
}
