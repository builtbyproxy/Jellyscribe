using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

public static class LetterboxdServiceFactory
{
    /// <summary>
    /// Test-only override. When non-null, CreateAuthenticatedAsync delegates to this
    /// instead of constructing a real LetterboxdApiClient/ScrapingLetterboxdService.
    /// Tests in this assembly's InternalsVisibleTo target set it to inject mock
    /// ILetterboxdService instances; production never assigns it.
    /// </summary>
    internal static Func<string, string, string?, ILogger, string?, Task<ILetterboxdService>>? OverrideForTesting;

    // Test seams for the two implementations; production never assigns them.
    internal static Func<ILogger, ILetterboxdService> CreateApiClient { get; set; } = logger => new LetterboxdApiClient(logger);

    internal static Func<ILogger, string?, CookieContainer, ILetterboxdService> CreateWebsiteClient { get; set; }
        = (logger, userAgent, cookies) => new ScrapingLetterboxdService(logger, userAgent, cookies);

    internal static Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    /// <summary>
    /// How long an account whose official API login failed (while the website login worked) goes
    /// straight to the website. Without it every playback or rating event pays a failed API login
    /// first.
    /// </summary>
    internal static readonly TimeSpan ApiUnavailableFor = TimeSpan.FromHours(2);

    // Keyed by Helpers.TokenCacheKey (account + password hash), so a re-saved password retries
    // the API at once. In memory only.
    private static readonly ConcurrentDictionary<string, DateTime> ApiUnavailableUntil = new();

    // The website session (its cookies) per account, password, raw cookies and User-Agent. A new
    // service reuses it, so an event checks the session with one request instead of logging in
    // again, which is what trips Letterboxd's captcha and the auth breaker. In memory only.
    private static readonly ConcurrentDictionary<string, CookieContainer> WebsiteSessions = new();

    internal static void ResetCachesForTesting()
    {
        ApiUnavailableUntil.Clear();
        WebsiteSessions.Clear();
    }

    public static async Task<ILetterboxdService> CreateAuthenticatedAsync(
        string username, string password, string? rawCookies, ILogger logger, string? userAgent = null)
    {
        if (OverrideForTesting != null)
            return await OverrideForTesting(username, password, rawCookies, logger, userAgent).ConfigureAwait(false);

        var accountKey = Helpers.TokenCacheKey(username, password);
        var apiRejected = false;
        if (ApiUnavailableUntil.TryGetValue(accountKey, out var until) && until > UtcNow())
        {
            logger.LogDebug("Official API was unavailable for {Username} recently; using the website until {Until:u}", username, until);
        }
        else
        {
            var apiClient = CreateApiClient(logger);
            try
            {
                await apiClient.AuthenticateAsync(username, password).ConfigureAwait(false);
                ApiUnavailableUntil.TryRemove(accountKey, out _);
                logger.LogInformation("Using official Letterboxd API for {Username}", username);
                return apiClient;
            }
            catch (Exception ex)
            {
                apiClient.Dispose();
                // Only a definite answer from the API is worth remembering; a timeout, a 5xx or a
                // rate limit says nothing about the next attempt.
                apiRejected = ex is LetterboxdApiAuthException { IsRejection: true };
                logger.LogWarning("Official API auth failed for {Username}, falling back to scraping: {Message}",
                    username, ex.Message);
            }
        }

        var sessionKey = Helpers.TokenCacheKey(username, $"{password}\n{rawCookies}\n{userAgent}");
        if (!WebsiteSessions.ContainsKey(sessionKey))
        {
            // New credentials, cookies or User-Agent for the account: drop its older sessions.
            // TokenCacheKey starts with the account name and a newline.
            var accountPrefix = username + "\n";
            foreach (var stale in WebsiteSessions.Keys)
            {
                if (stale.StartsWith(accountPrefix, StringComparison.Ordinal))
                    WebsiteSessions.TryRemove(stale, out _);
            }
        }

        // Services for one account can overlap (a scheduled run and a playback event). They share
        // the jar, which is thread-safe; if one logs in again, the other picks up the new session
        // cookies, and a stale CSRF token on its side takes the existing re-login path.
        var cookies = WebsiteSessions.GetOrAdd(sessionKey, _ => new CookieContainer());
        var scraping = CreateWebsiteClient(logger, userAgent, cookies);
        try
        {
            await scraping.AuthenticateAsync(username, password, rawCookies).ConfigureAwait(false);
        }
        catch
        {
            // A session that cannot log in is not worth keeping.
            WebsiteSessions.TryRemove(sessionKey, out _);
            scraping.Dispose();
            throw;
        }

        // Only remember the API as unavailable when the website worked: if both failed, the cause
        // is the account or the network, and the next attempt should try the API again.
        if (apiRejected)
            ApiUnavailableUntil[accountKey] = UtcNow() + ApiUnavailableFor;

        logger.LogInformation("Using web scraping fallback for {Username}", username);
        return scraping;
    }
}
