using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using LetterboxdSync;
using LetterboxdSync.Api;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// Auth breaker reset + surfacing through the account endpoints (issue #103):
/// re-saving credentials closes an open breaker, and GET Accounts carries the
/// paused fields the dashboard badge renders from.
/// </summary>
[Collection("Plugin")]
public class AuthBreakerControllerTests : IDisposable
{
    private const string UserId = "aabbccddeeff00112233445566778899";
    private readonly string _path;

    public AuthBreakerControllerTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "lbs-breaker-ctl-" + Guid.NewGuid().ToString("N") + ".json");
        AuthBreaker.DataPathOverride = _path;
        AuthBreaker.ResetForTesting();
    }

    public void Dispose()
    {
        AuthBreaker.DataPathOverride = null;
        AuthBreaker.ResetForTesting();
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
    }

    private static void OpenBreaker(string userId, string lbUsername)
    {
        for (var i = 0; i < AuthBreaker.Threshold; i++)
            AuthBreaker.RecordFailure(userId, lbUsername, "bad password");
        Assert.True(AuthBreaker.IsOpen(userId, lbUsername));
    }

    [Fact]
    public void PutAccounts_CredentialSave_ClosesOpenBreaker()
    {
        using var h = new ControllerTestHarness(UserId);
        OpenBreaker(UserId, "kostadamus");

        var result = h.Controller.PutAccounts(new AccountsUpdateRequest
        {
            Accounts = new List<AccountUpdateRequest>
            {
                new() { LetterboxdUsername = "kostadamus", LetterboxdPassword = "new-password", Enabled = true }
            }
        });

        Assert.IsType<OkObjectResult>(result);
        Assert.False(AuthBreaker.IsOpen(UserId, "kostadamus"));
    }

    [Fact]
    public void PutAccount_CredentialSave_ClosesOpenBreaker()
    {
        using var h = new ControllerTestHarness(UserId);
        OpenBreaker(UserId, "kostadamus");

        var result = h.Controller.PutAccount(new AccountUpdateRequest
        {
            LetterboxdUsername = "kostadamus",
            LetterboxdPassword = "new-password",
            Enabled = true
        });

        Assert.IsType<OkObjectResult>(result);
        Assert.False(AuthBreaker.IsOpen(UserId, "kostadamus"));
    }

    [Fact]
    public void PutAccounts_LeavesOtherUsersBreakersAlone()
    {
        using var h = new ControllerTestHarness(UserId);
        OpenBreaker("otheruser0000000000000000000000ab", "kostadamus");

        h.Controller.PutAccounts(new AccountsUpdateRequest
        {
            Accounts = new List<AccountUpdateRequest>
            {
                new() { LetterboxdUsername = "kostadamus", LetterboxdPassword = "pw", Enabled = true }
            }
        });

        Assert.True(AuthBreaker.IsOpen("otheruser0000000000000000000000ab", "kostadamus"));
    }

    [Fact]
    public void GetAccounts_CarriesPausedFields()
    {
        using var h = new ControllerTestHarness(UserId);
        h.AddAccount(UserId, "kostadamus");
        OpenBreaker(UserId, "kostadamus");

        var ok = Assert.IsType<OkObjectResult>(h.Controller.GetAccounts());
        var accountsProp = ok.Value!.GetType().GetProperty("accounts", BindingFlags.Public | BindingFlags.Instance)!;
        var accounts = (System.Collections.IEnumerable)accountsProp.GetValue(ok.Value)!;

        var found = false;
        foreach (var a in accounts)
        {
            var t = a.GetType();
            if ((string)t.GetProperty("letterboxdUsername")!.GetValue(a)! != "kostadamus") continue;
            found = true;
            Assert.True((bool)t.GetProperty("authPaused")!.GetValue(a)!);
            Assert.NotNull(t.GetProperty("authPausedSince")!.GetValue(a));
        }
        Assert.True(found);
    }

    [Fact]
    public void GetAccounts_ClosedBreaker_PausedFieldsAreFalseAndNull()
    {
        using var h = new ControllerTestHarness(UserId);
        h.AddAccount(UserId, "kostadamus");

        var ok = Assert.IsType<OkObjectResult>(h.Controller.GetAccounts());
        var accounts = (System.Collections.IEnumerable)ok.Value!.GetType()
            .GetProperty("accounts", BindingFlags.Public | BindingFlags.Instance)!.GetValue(ok.Value)!;

        foreach (var a in accounts)
        {
            var t = a.GetType();
            Assert.False((bool)t.GetProperty("authPaused")!.GetValue(a)!);
            Assert.Null(t.GetProperty("authPausedSince")!.GetValue(a));
        }
    }

    [Fact]
    public void GetAuthBreakers_ListsOpenBreakersOnly()
    {
        using var h = new ControllerTestHarness(UserId);
        OpenBreaker(UserId, "kostadamus");
        AuthBreaker.RecordFailure(UserId, "healthyaccount", "one-off blip");

        var ok = Assert.IsType<OkObjectResult>(h.Controller.GetAuthBreakers());
        var breakers = (System.Collections.IEnumerable)ok.Value!.GetType()
            .GetProperty("breakers", BindingFlags.Public | BindingFlags.Instance)!.GetValue(ok.Value)!;

        var names = new List<string>();
        foreach (var b in breakers)
            names.Add((string)b.GetType().GetProperty("letterboxdUsername")!.GetValue(b)!);

        Assert.Equal(new[] { "kostadamus" }, names);
    }

    /// <summary>
    /// Mirrors how Jellyfin actually saves plugin config: UpdatePluginConfiguration deserializes
    /// the request body into a NEW PluginConfiguration, so the plugin always receives a distinct
    /// instance rather than the live one. Tests must do the same or the change comparison has
    /// nothing to compare against.
    /// </summary>
    private static LetterboxdSync.Configuration.PluginConfiguration SaveAsDashboard(
        params LetterboxdSync.Configuration.Account[] accounts)
    {
        var fresh = new LetterboxdSync.Configuration.PluginConfiguration();
        fresh.Accounts.AddRange(accounts);
        Plugin.Instance!.UpdateConfiguration(fresh);
        return fresh;
    }

    private static LetterboxdSync.Configuration.Account Acct(string user, string username, string password, string? cookies = null)
        => new() { UserJellyfinId = user, LetterboxdUsername = username, LetterboxdPassword = password, RawCookies = cookies };

    /// <summary>
    /// Issue #112: the admin dashboard saves accounts through Plugin.UpdateConfiguration, not the
    /// user-facing /Accounts endpoint, and only that endpoint closed breakers. An admin who fixed a
    /// stale Letterboxd password stayed stuck on "Login failing - sync paused" indefinitely.
    /// </summary>
    [Fact]
    public void UpdateConfiguration_PasswordChanged_ClosesBreaker()
    {
        using var h = new ControllerTestHarness(UserId);
        SaveAsDashboard(Acct(UserId, "charlie", "old-password"));

        OpenBreaker(UserId, "charlie");
        Assert.True(AuthBreaker.IsOpen(UserId, "charlie"));

        // Admin edits the password in the dashboard and saves.
        SaveAsDashboard(Acct(UserId, "charlie", "new-password"));

        Assert.False(AuthBreaker.IsOpen(UserId, "charlie"));
    }

    /// <summary>
    /// The reporter also tried deleting the account and creating a fresh one, which didn't help:
    /// the breaker is keyed on (user, Letterboxd username), so a re-created account inherited the
    /// old open breaker. A newly added account must start clean.
    /// </summary>
    [Fact]
    public void UpdateConfiguration_AccountRecreated_ClosesInheritedBreaker()
    {
        using var h = new ControllerTestHarness(UserId);
        SaveAsDashboard(); // no accounts
        OpenBreaker(UserId, "charlie");
        Assert.True(AuthBreaker.IsOpen(UserId, "charlie"));

        SaveAsDashboard(Acct(UserId, "charlie", "fresh"));

        Assert.False(AuthBreaker.IsOpen(UserId, "charlie"));
    }

    /// <summary>
    /// Saving an unrelated setting must NOT reopen the breaker. Blanket-resetting on every config
    /// write would defeat the breaker and reintroduce the retry storm it exists to stop.
    /// </summary>
    [Fact]
    public void UpdateConfiguration_UnrelatedSettingSaved_LeavesBreakerOpen()
    {
        using var h = new ControllerTestHarness(UserId);
        SaveAsDashboard(Acct(UserId, "charlie", "same-password"));

        OpenBreaker(UserId, "charlie");
        Assert.True(AuthBreaker.IsOpen(UserId, "charlie"));

        // Credentials identical; something else changes.
        var fresh = new LetterboxdSync.Configuration.PluginConfiguration { JellyseerrUrl = "http://localhost:5055" };
        fresh.Accounts.Add(Acct(UserId, "charlie", "same-password"));
        Plugin.Instance!.UpdateConfiguration(fresh);

        Assert.True(AuthBreaker.IsOpen(UserId, "charlie"));
    }

    /// <summary>Changing only the raw cookies is a credential change too.</summary>
    [Fact]
    public void UpdateConfiguration_CookiesChanged_ClosesBreaker()
    {
        using var h = new ControllerTestHarness(UserId);
        SaveAsDashboard(Acct(UserId, "charlie", "pw"));

        OpenBreaker(UserId, "charlie");
        SaveAsDashboard(Acct(UserId, "charlie", "pw", "cf_clearance=abc"));

        Assert.False(AuthBreaker.IsOpen(UserId, "charlie"));
    }
}
