using System;
using Xunit;

namespace LetterboxdSync.Tests.Integration;

/// <summary>
/// Shared gate for the live tests that sign in through the website (the scraping fallback).
/// On GitHub Actions, Cloudflare refuses that sign-in unless the browser cookies in
/// LETTERBOXD_TEST_RAW_COOKIES are supplied, so without them these tests skip there with one
/// recognisable message instead of a Cloudflare error. The live-tests workflow reads that skip
/// from the test results and reports the scraping path as not covered. Local runs keep trying
/// the plain sign-in, which often works from a home connection.
/// </summary>
internal static class ScrapingLiveTest
{
    internal const string RawCookiesVariable = "LETTERBOXD_TEST_RAW_COOKIES";

    // .github/scripts/live_test_summary.py matches "LETTERBOXD_TEST_RAW_COOKIES is not set" in
    // this message; keep that phrase if you reword it.
    internal const string NoCookiesSkipMessage =
        "Scraping path not covered: " + RawCookiesVariable + " is not set, and Cloudflare refuses the " +
        "website sign-in from CI without it. See LetterboxdSync.Tests/Integration/README.md to refresh it.";

    /// <summary>
    /// Returns the raw cookie header (null when unset). On GitHub Actions, skips the calling test
    /// when it is not set.
    /// </summary>
    internal static string? RawCookiesOrSkipInCi()
    {
        var cookies = Environment.GetEnvironmentVariable(RawCookiesVariable);
        var onCi = string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);
        Skip.If(onCi && string.IsNullOrWhiteSpace(cookies), NoCookiesSkipMessage);
        return string.IsNullOrWhiteSpace(cookies) ? null : cookies;
    }
}
