using System;
using System.IO;
using LetterboxdSync;
using Xunit;

namespace LetterboxdSync.Tests;

// AuthBreaker is static, file-backed global state. Without this the class runs in
// parallel with the other AuthBreaker suites (all of which are in "Plugin") and they
// race on the same DataPathOverride and the same "u1"/"kostadamus" key, which showed
// up as State_SurvivesReload intermittently seeing 6 consecutive failures instead of 3.
[Collection("Plugin")]
public class AuthBreakerTests : IDisposable
{
    private readonly string _path;

    public AuthBreakerTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "lbs-breaker-" + Guid.NewGuid().ToString("N") + ".json");
        AuthBreaker.DataPathOverride = _path;
        AuthBreaker.ResetForTesting();
    }

    public void Dispose()
    {
        AuthBreaker.UtcNow = () => DateTime.UtcNow;
        AuthBreaker.DataPathOverride = null;
        AuthBreaker.ResetForTesting();
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
    }

    [Fact]
    public void ThirdConsecutiveFailure_OpensBreakerExactlyOnce()
    {
        Assert.False(AuthBreaker.RecordFailure("u1", "kostadamus", "bad password"));
        Assert.False(AuthBreaker.RecordFailure("u1", "kostadamus", "bad password"));
        Assert.False(AuthBreaker.IsOpen("u1", "kostadamus"));

        Assert.True(AuthBreaker.RecordFailure("u1", "kostadamus", "bad password"));
        Assert.True(AuthBreaker.IsOpen("u1", "kostadamus"));

        // Further failures while open must not report the transition again.
        Assert.False(AuthBreaker.RecordFailure("u1", "kostadamus", "bad password"));
        Assert.True(AuthBreaker.IsOpen("u1", "kostadamus"));
    }

    [Fact]
    public void OpenedState_RecordsFirstFailureDate()
    {
        var before = DateTime.UtcNow;
        AuthBreaker.RecordFailure("u1", "kostadamus", "e1");
        AuthBreaker.RecordFailure("u1", "kostadamus", "e2");
        AuthBreaker.RecordFailure("u1", "kostadamus", "e3");

        var state = AuthBreaker.GetState("u1", "kostadamus");
        Assert.NotNull(state);
        Assert.Equal(3, state!.ConsecutiveFailures);
        Assert.NotNull(state.OpenedAtUtc);
        Assert.NotNull(state.FirstFailureUtc);
        Assert.True(state.FirstFailureUtc >= before.AddSeconds(-1));
        Assert.Equal("e3", state.LastError);
    }

    [Fact]
    public void SuccessResetsCount_BeforeThreshold()
    {
        AuthBreaker.RecordFailure("u1", "kostadamus", "e");
        AuthBreaker.RecordFailure("u1", "kostadamus", "e");
        AuthBreaker.RecordSuccess("u1", "kostadamus");

        Assert.False(AuthBreaker.IsOpen("u1", "kostadamus"));
        Assert.Null(AuthBreaker.GetState("u1", "kostadamus"));

        // The count restarted: two more failures must not open it.
        AuthBreaker.RecordFailure("u1", "kostadamus", "e");
        Assert.False(AuthBreaker.RecordFailure("u1", "kostadamus", "e"));
        Assert.False(AuthBreaker.IsOpen("u1", "kostadamus"));
    }

    [Fact]
    public void Reset_ClosesAnOpenBreaker()
    {
        for (var i = 0; i < 3; i++) AuthBreaker.RecordFailure("u1", "kostadamus", "e");
        Assert.True(AuthBreaker.IsOpen("u1", "kostadamus"));

        AuthBreaker.Reset("u1", "kostadamus");
        Assert.False(AuthBreaker.IsOpen("u1", "kostadamus"));
        Assert.Null(AuthBreaker.GetState("u1", "kostadamus"));
    }

    [Fact]
    public void State_SurvivesReload()
    {
        for (var i = 0; i < 3; i++) AuthBreaker.RecordFailure("u1", "kostadamus", "e");
        Assert.True(AuthBreaker.IsOpen("u1", "kostadamus"));

        // Simulate a restart: drop the in-memory cache, forcing a re-read from disk.
        AuthBreaker.ResetForTesting();
        Assert.True(AuthBreaker.IsOpen("u1", "kostadamus"));
        Assert.Equal(3, AuthBreaker.GetState("u1", "kostadamus")!.ConsecutiveFailures);
    }

    [Fact]
    public void Accounts_AreIsolated()
    {
        for (var i = 0; i < 3; i++) AuthBreaker.RecordFailure("u1", "kostadamus", "e");

        Assert.True(AuthBreaker.IsOpen("u1", "kostadamus"));
        Assert.False(AuthBreaker.IsOpen("u1", "otheraccount"));
        Assert.False(AuthBreaker.IsOpen("u2", "kostadamus"));

        AuthBreaker.RecordFailure("u2", "kostadamus", "e");
        AuthBreaker.Reset("u1", "kostadamus");
        Assert.Equal(1, AuthBreaker.GetState("u2", "kostadamus")!.ConsecutiveFailures);
    }

    [Fact]
    public void LastError_IsSanitized_SingleLineAndBounded()
    {
        var nasty = "Login failed:\r\nSet-Cookie: session=SECRETVALUE\n" + new string('x', 500);
        for (var i = 0; i < 3; i++) AuthBreaker.RecordFailure("u1", "kostadamus", nasty);

        var stored = AuthBreaker.GetState("u1", "kostadamus")!.LastError!;
        Assert.DoesNotContain("\n", stored);
        Assert.DoesNotContain("\r", stored);
        Assert.True(stored.Length <= 161, $"LastError length {stored.Length} exceeds bound");

        var open = Assert.Single(AuthBreaker.GetOpenEntries());
        Assert.Equal(stored, open.LastError);
    }

    [Fact]
    public void Sanitize_NullOrWhitespace_ReturnsNull()
    {
        Assert.Null(AuthBreaker.Sanitize(null));
        Assert.Null(AuthBreaker.Sanitize("   "));
        Assert.Equal("plain message", AuthBreaker.Sanitize("plain message"));
    }

    [Fact]
    public void UsernameMatch_IsCaseInsensitive()
    {
        for (var i = 0; i < 3; i++) AuthBreaker.RecordFailure("u1", "Kostadamus", "e");
        Assert.True(AuthBreaker.IsOpen("u1", "kostadamus"));
        AuthBreaker.Reset("u1", "KOSTADAMUS");
        Assert.False(AuthBreaker.IsOpen("u1", "Kostadamus"));
    }

    private static void Open(string user = "u1", string account = "demo-cinephile")
    {
        for (var i = 0; i < AuthBreaker.Threshold; i++)
            AuthBreaker.RecordFailure(user, account, "Letterboxd returned 503");
    }

    // An outage during three logins must not pause the account forever: after a day the breaker
    // lets exactly one login through.
    [Fact]
    public void OpenBreaker_LetsOneLoginThroughAfterADay()
    {
        var now = new DateTime(2026, 10, 1, 3, 0, 0, DateTimeKind.Utc);
        AuthBreaker.UtcNow = () => now;
        Open();

        Assert.True(AuthBreaker.BlocksLogin("u1", "demo-cinephile"));
        now += AuthBreaker.HalfOpenAfter - TimeSpan.FromMinutes(1);
        Assert.True(AuthBreaker.BlocksLogin("u1", "demo-cinephile"));

        now += TimeSpan.FromMinutes(2);
        Assert.False(AuthBreaker.BlocksLogin("u1", "demo-cinephile"));
        // A second caller in the same moment is still held back.
        Assert.True(AuthBreaker.BlocksLogin("u1", "demo-cinephile"));
        Assert.True(AuthBreaker.IsOpen("u1", "demo-cinephile"));
    }

    [Fact]
    public void FailedProbe_KeepsItOpenForAnotherDay_AndASuccessfulOneClosesIt()
    {
        var now = new DateTime(2026, 10, 1, 3, 0, 0, DateTimeKind.Utc);
        AuthBreaker.UtcNow = () => now;
        Open();

        now += AuthBreaker.HalfOpenAfter;
        Assert.False(AuthBreaker.BlocksLogin("u1", "demo-cinephile"));
        Assert.False(AuthBreaker.RecordFailure("u1", "demo-cinephile", "Letterboxd returned 503")); // no second notification

        now += TimeSpan.FromHours(12);
        Assert.True(AuthBreaker.BlocksLogin("u1", "demo-cinephile"));

        now += TimeSpan.FromHours(12);
        Assert.False(AuthBreaker.BlocksLogin("u1", "demo-cinephile"));
        AuthBreaker.RecordSuccess("u1", "demo-cinephile");
        Assert.False(AuthBreaker.IsOpen("u1", "demo-cinephile"));
    }

    [Fact]
    public void AWeekOfFailedDailyAttempts_StopsTrying_UntilCredentialsAreResaved()
    {
        var now = new DateTime(2026, 10, 1, 3, 0, 0, DateTimeKind.Utc);
        AuthBreaker.UtcNow = () => now;
        Open();

        for (var day = 0; day < AuthBreaker.MaxFailedProbes; day++)
        {
            now += AuthBreaker.HalfOpenAfter;
            Assert.False(AuthBreaker.BlocksLogin("u1", "demo-cinephile"));
            AuthBreaker.RecordFailure("u1", "demo-cinephile", "Letterboxd returned 403 during login. Likely reCAPTCHA.");
        }

        now += TimeSpan.FromDays(30);
        Assert.True(AuthBreaker.BlocksLogin("u1", "demo-cinephile"));

        AuthBreaker.Reset("u1", "demo-cinephile");
        Assert.False(AuthBreaker.BlocksLogin("u1", "demo-cinephile"));
    }

    // A wrong password is not an outage: retrying it only risks a lockout, so the breaker waits
    // for re-saved credentials.
    [Fact]
    public void RejectedCredentials_AreNeverRetriedOnTheirOwn()
    {
        var now = new DateTime(2026, 10, 1, 3, 0, 0, DateTimeKind.Utc);
        AuthBreaker.UtcNow = () => now;
        for (var i = 0; i < AuthBreaker.Threshold; i++)
            AuthBreaker.RecordFailure("u1", "demo-cinephile", "Letterboxd login error: Your credentials don't match.");

        now += TimeSpan.FromDays(3);

        Assert.True(AuthBreaker.BlocksLogin("u1", "demo-cinephile"));
    }

    [Fact]
    public void ProbeTime_SurvivesARestart()
    {
        var now = new DateTime(2026, 10, 1, 3, 0, 0, DateTimeKind.Utc);
        AuthBreaker.UtcNow = () => now;
        Open();
        now += AuthBreaker.HalfOpenAfter;
        Assert.False(AuthBreaker.BlocksLogin("u1", "demo-cinephile"));

        AuthBreaker.ResetForTesting(); // reload from disk

        Assert.True(AuthBreaker.BlocksLogin("u1", "demo-cinephile"));
    }

    [Fact]
    public void AdminReset_StillClosesItAtOnce()
    {
        Open();
        AuthBreaker.Reset("u1", "demo-cinephile");

        Assert.False(AuthBreaker.BlocksLogin("u1", "demo-cinephile"));
        Assert.False(AuthBreaker.IsOpen("u1", "demo-cinephile"));
    }
}
