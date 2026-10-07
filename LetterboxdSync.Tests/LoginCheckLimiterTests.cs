using System;
using LetterboxdSync.Api;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// The sliding-window budget behind the Verify endpoints. Uses its own instances with a fake
/// clock, so it never touches the shared Letterboxd/Serializd limiters the controller tests use.
/// </summary>
public class LoginCheckLimiterTests
{
    private DateTime _now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private LoginCheckLimiter NewLimiter() => new() { UtcNow = () => _now };

    [Fact]
    public void PerUserLimit_RefusesTheNextCheck_AndSaysWhenToRetry()
    {
        var limiter = NewLimiter();
        for (var i = 0; i < LoginCheckLimiter.PerUserLimit; i++)
        {
            Assert.True(limiter.TryAcquire("alex", out _, out _));
            _now = _now.AddMinutes(1);
        }

        Assert.False(limiter.TryAcquire("alex", out _, out var retryAfter));
        // The first check was 5 minutes ago, so it leaves the 10 minute window in 5.
        Assert.Equal(TimeSpan.FromMinutes(5), retryAfter);
    }

    [Fact]
    public void OldChecksLeaveTheWindow()
    {
        var limiter = NewLimiter();
        for (var i = 0; i < LoginCheckLimiter.PerUserLimit; i++)
            Assert.True(limiter.TryAcquire("alex", out _, out _));

        _now += LoginCheckLimiter.Window;

        Assert.True(limiter.TryAcquire("alex", out _, out _));
    }

    [Fact]
    public void Refund_GivesTheFailedCheckBack()
    {
        var limiter = NewLimiter();
        for (var i = 0; i < LoginCheckLimiter.PerUserLimit * 3; i++)
        {
            Assert.True(limiter.TryAcquire("alex", out var stamp, out _));
            limiter.Refund("alex", stamp);
        }
    }

    [Fact]
    public void SuccessfulChecks_StillHitTheLooserTotalCap()
    {
        var limiter = NewLimiter();
        for (var i = 0; i < LoginCheckLimiter.PerUserTotalLimit; i++)
        {
            Assert.True(limiter.TryAcquire("alex", out var stamp, out _));
            limiter.Refund("alex", stamp);
        }

        Assert.False(limiter.TryAcquire("alex", out _, out var retryAfter));
        Assert.Equal(LoginCheckLimiter.Window, retryAfter);
        Assert.True(limiter.TryAcquire("sam", out _, out _));
    }

    [Fact]
    public void GlobalLimit_RefusesEveryone_OnceTheServerBudgetIsSpent()
    {
        var limiter = NewLimiter();
        for (var i = 0; i < LoginCheckLimiter.GlobalLimit; i++)
            Assert.True(limiter.TryAcquire("user" + i, out _, out _));

        Assert.False(limiter.TryAcquire("someone-new", out _, out var retryAfter));
        Assert.Equal(LoginCheckLimiter.Window, retryAfter);
    }

    [Fact]
    public void UnresolvedCallers_ShareOneBucket()
    {
        var limiter = NewLimiter();
        for (var i = 0; i < LoginCheckLimiter.PerUserLimit; i++)
            Assert.True(limiter.TryAcquire(string.Empty, out _, out _));

        Assert.False(limiter.TryAcquire(string.Empty, out _, out _));
        Assert.True(limiter.TryAcquire("alex", out _, out _));
    }

    [Theory]
    [InlineData(1, "Too many login checks. Try again in 1 minute.")]
    [InlineData(61, "Too many login checks. Try again in 2 minutes.")]
    [InlineData(600, "Too many login checks. Try again in 10 minutes.")]
    public void RefusalMessage_RoundsUpToWholeMinutes(int seconds, string expected)
    {
        Assert.Equal(expected, LoginCheckLimiter.RefusalMessage(TimeSpan.FromSeconds(seconds)));
    }
}
