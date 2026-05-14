using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LetterboxdSync.Tests.Integration;

/// <summary>
/// Live integration tests that talk to letterboxd.com using a real account. Skipped
/// unless LETTERBOXD_TEST_USERNAME and LETTERBOXD_TEST_PASSWORD are set on the
/// environment — see Integration/README.md for setup. Tests are read-only so they
/// can be rerun without polluting the account's diary. Network-dependent and slower
/// than the unit suite: run with `dotnet test --filter Category=Integration`.
/// </summary>
[Trait("Category", "Integration")]
public class LetterboxdLiveTests
{
    private const string EnvUser = "LETTERBOXD_TEST_USERNAME";
    private const string EnvPass = "LETTERBOXD_TEST_PASSWORD";
    private const string EnvCookies = "LETTERBOXD_TEST_RAW_COOKIES";
    private const string EnvUserAgent = "LETTERBOXD_TEST_USER_AGENT";

    private static (string user, string pass, string? cookies, string? ua) RequireCreds()
    {
        var user = Environment.GetEnvironmentVariable(EnvUser);
        var pass = Environment.GetEnvironmentVariable(EnvPass);
        Skip.If(string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass),
            $"Skipping live test: set {EnvUser} and {EnvPass} to run. See LetterboxdSync.Tests/Integration/README.md");
        return (user!, pass!,
            Environment.GetEnvironmentVariable(EnvCookies),
            Environment.GetEnvironmentVariable(EnvUserAgent));
    }

    private static async Task<ILetterboxdService> AuthenticateAsync()
    {
        var (user, pass, cookies, ua) = RequireCreds();
        return await LetterboxdServiceFactory.CreateAuthenticatedAsync(
            user, pass, cookies, NullLogger.Instance, ua).ConfigureAwait(false);
    }

    [SkippableFact]
    public async Task Authenticate_ValidCredentials_ReturnsUsableService()
    {
        using var service = await AuthenticateAsync().ConfigureAwait(false);
        Assert.NotNull(service);
    }

    /// <summary>
    /// Pulp Fiction (TMDb 680) is a stable, well-known film. If Letterboxd's mapping
    /// ever changes for it we have bigger problems than this test failing.
    /// </summary>
    [SkippableFact]
    public async Task LookupFilmByTmdbId_KnownFilm_ReturnsSlug()
    {
        using var service = await AuthenticateAsync().ConfigureAwait(false);

        var film = await service.LookupFilmByTmdbIdAsync(680).ConfigureAwait(false);

        Assert.NotNull(film);
        Assert.False(string.IsNullOrWhiteSpace(film.Slug), "Slug should be populated");
        Assert.Contains("pulp-fiction", film.Slug, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task GetWatchlistTmdbIds_ReturnsListWithoutThrowing()
    {
        var (user, _, _, _) = RequireCreds();
        using var service = await AuthenticateAsync().ConfigureAwait(false);

        var ids = await service.GetWatchlistTmdbIdsAsync(user).ConfigureAwait(false);

        // Test account watchlist may be empty; we only assert the call succeeds and
        // returns a non-null list. Membership is the next test's job.
        Assert.NotNull(ids);
    }

    [SkippableFact]
    public async Task GetDiaryFilmEntries_ReturnsListWithoutThrowing()
    {
        var (user, _, _, _) = RequireCreds();
        using var service = await AuthenticateAsync().ConfigureAwait(false);

        var entries = await service.GetDiaryFilmEntriesAsync(user).ConfigureAwait(false);

        Assert.NotNull(entries);
    }

    /// <summary>
    /// Diary info for a known film returns a well-formed shape even when the user
    /// has never logged the film (LastDate is null in that case). Exercises the
    /// same code path that the same-day duplicate check uses in production.
    /// </summary>
    [SkippableFact]
    public async Task GetDiaryInfo_KnownFilm_ReturnsShape()
    {
        var (user, _, _, _) = RequireCreds();
        using var service = await AuthenticateAsync().ConfigureAwait(false);

        var film = await service.LookupFilmByTmdbIdAsync(680).ConfigureAwait(false);
        var info = await service.GetDiaryInfoAsync(film.FilmId, user).ConfigureAwait(false);

        Assert.NotNull(info);
    }
}
