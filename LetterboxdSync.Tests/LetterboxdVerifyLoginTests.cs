using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using LetterboxdSync.Api;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// Verify login for Letterboxd accounts: API first, then the website login, reporting which
/// worked or both reasons; and email addresses rejected up front (Letterboxd's API answers
/// "Sign-in via email address has been disabled", verified on a live server 2026-10-04).
/// </summary>
[Collection("Plugin")]
public class LetterboxdVerifyLoginTests : IDisposable
{
    private const string UserId = "aabbccddeeff00112233445566778899";
    private const string EmailRefusal =
        "Letterboxd API auth failed (BadRequest): {\"error\":\"invalid_grant\",\"error_description\":\"Sign-in via email address has been disabled. Please sign in with username and password instead.\"}";

    private int _apiCalls;
    private int _websiteCalls;

    public void Dispose()
    {
        LetterboxdController.VerifyApiLoginForTesting = null;
        LetterboxdController.VerifyWebsiteLoginForTesting = null;
    }

    private void Logins(Exception? api = null, Exception? website = null)
    {
        LetterboxdController.VerifyApiLoginForTesting = (_, _) =>
        {
            _apiCalls++;
            return api == null ? Task.CompletedTask : Task.FromException(api);
        };
        LetterboxdController.VerifyWebsiteLoginForTesting = (_, _, _, _) =>
        {
            _websiteCalls++;
            return website == null ? Task.CompletedTask : Task.FromException(website);
        };
    }

    private static T Prop<T>(object value, string name) => (T)value.GetType().GetProperty(name)!.GetValue(value)!;

    private static async Task<ObjectResult> Verify(string user, string password = "pw")
    {
        using var h = new ControllerTestHarness(UserId);
        var result = await h.Controller.VerifyLogin(new LetterboxdVerifyRequest { LetterboxdUsername = user, LetterboxdPassword = password });
        return Assert.IsAssignableFrom<ObjectResult>(result);
    }

    [Fact]
    public async Task ApiLoginWorks_ReportsApi_AndSkipsWebsite()
    {
        Logins();
        var ok = Assert.IsType<OkObjectResult>(await Verify("8bitproxy"));
        Assert.Equal("api", Prop<string>(ok.Value!, "via"));
        Assert.Equal((1, 0), (_apiCalls, _websiteCalls));
    }

    [Fact]
    public async Task ApiRefused_WebsiteWorks_ReportsWebsiteWithTheApiReason()
    {
        Logins(api: new Exception("Letterboxd API auth failed (Unauthorized): {\"error\":\"invalid_grant\",\"error_description\":\"Bad credentials\"}"));
        var ok = Assert.IsType<OkObjectResult>(await Verify("8bitproxy"));
        Assert.Equal("website", Prop<string>(ok.Value!, "via"));
        Assert.Equal("Bad credentials", Prop<string>(ok.Value!, "apiError"));
    }

    [Fact]
    public async Task BothFail_Returns400WithBothReasons()
    {
        Logins(api: new Exception(EmailRefusal), website: new Exception("Letterboxd login error: The form on this page had expired"));
        var bad = Assert.IsType<BadRequestObjectResult>(await Verify("8bitproxy"));
        Assert.Contains("Sign-in via email address has been disabled", Prop<string>(bad.Value!, "apiError"));
        Assert.Contains("form on this page had expired", Prop<string>(bad.Value!, "websiteError"));
    }

    [Fact]
    public async Task EmailAddress_IsRefusedWithoutAnyLoginAttempt()
    {
        Logins();
        var bad = Assert.IsType<BadRequestObjectResult>(await Verify("someone+lb@example.com"));
        Assert.Contains("Use your Letterboxd username", Prop<string>(bad.Value!, "error"));
        Assert.Equal((0, 0), (_apiCalls, _websiteCalls));
    }

    [Theory]
    [InlineData("", "pw")]
    [InlineData("8bitproxy", "")]
    public async Task MissingCredentials_Returns400(string user, string password)
    {
        Logins();
        Assert.IsType<BadRequestObjectResult>(await Verify(user, password));
        Assert.Equal(0, _apiCalls);
    }

    [Fact]
    public void PutAccounts_EmailAddress_IsRefused()
    {
        using var h = new ControllerTestHarness(UserId);
        var result = h.Controller.PutAccounts(new AccountsUpdateRequest
        {
            Accounts = new List<AccountUpdateRequest> { new() { LetterboxdUsername = "someone@example.com", LetterboxdPassword = "pw", Enabled = true } }
        });

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Use your Letterboxd username", Prop<string>(bad.Value!, "error"));
        Assert.Empty(h.Config.Accounts);
    }

    [Fact]
    public async Task RepeatedFailedChecks_AreRefusedWith429_BeforeAnyLoginAttempt()
    {
        Logins(api: new Exception("bad"), website: new Exception("bad"));
        using var h = new ControllerTestHarness(UserId);

        for (var i = 0; i < LoginCheckLimiter.PerUserLimit; i++)
            Assert.IsType<BadRequestObjectResult>(await h.Controller.VerifyLogin(
                new LetterboxdVerifyRequest { LetterboxdUsername = "8bitproxy", LetterboxdPassword = "guess" + i }));
        var callsBefore = (_apiCalls, _websiteCalls);

        var refused = Assert.IsAssignableFrom<ObjectResult>(await h.Controller.VerifyLogin(
            new LetterboxdVerifyRequest { LetterboxdUsername = "8bitproxy", LetterboxdPassword = "another" }));

        Assert.Equal(429, refused.StatusCode);
        Assert.StartsWith("Too many login checks. Try again in 10 minutes", Prop<string>(refused.Value!, "error"));
        Assert.Equal(callsBefore, (_apiCalls, _websiteCalls));
        Assert.Equal("600", h.Controller.Response.Headers.RetryAfter.ToString());
    }

    [Fact]
    public async Task SuccessfulChecks_DoNotUseUpTheBudget()
    {
        Logins();
        using var h = new ControllerTestHarness(UserId);

        for (var i = 0; i < LoginCheckLimiter.PerUserLimit * 3; i++)
            Assert.IsType<OkObjectResult>(await h.Controller.VerifyLogin(
                new LetterboxdVerifyRequest { LetterboxdUsername = "account" + i, LetterboxdPassword = "pw" }));
    }

    [Fact]
    public async Task WebsiteFallbackSuccesses_AreRefundedToo()
    {
        Logins(api: new Exception("api refused"));
        using var h = new ControllerTestHarness(UserId);

        for (var i = 0; i < LoginCheckLimiter.PerUserLimit * 3; i++)
            Assert.IsType<OkObjectResult>(await h.Controller.VerifyLogin(
                new LetterboxdVerifyRequest { LetterboxdUsername = "account" + i, LetterboxdPassword = "pw" }));
    }

    [Fact]
    public async Task OneUsersFailures_DoNotBlockAnotherUser()
    {
        Logins(api: new Exception("bad"), website: new Exception("bad"));
        using var h = new ControllerTestHarness(UserId);
        for (var i = 0; i < LoginCheckLimiter.PerUserLimit; i++)
            await h.Controller.VerifyLogin(new LetterboxdVerifyRequest { LetterboxdUsername = "x", LetterboxdPassword = "y" + i });

        h.Controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", "00112233445566778899aabbccddeeff") }, "Test"));
        var other = await h.Controller.VerifyLogin(new LetterboxdVerifyRequest { LetterboxdUsername = "x", LetterboxdPassword = "z" });

        Assert.IsType<BadRequestObjectResult>(other);
    }

    [Theory]
    [InlineData(EmailRefusal, "Sign-in via email address has been disabled. Please sign in with username and password instead.")]
    [InlineData("plain failure\nsecond line", "plain failure second line")]
    public void DescribeLoginError_PrefersLetterboxdsOwnReason(string message, string expected)
    {
        Assert.Equal(expected, LetterboxdController.DescribeLoginError(new Exception(message)));
    }
}
