using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LetterboxdSync.Api;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// The per-account "Re-request watched titles" toggle (issue #165) saves through the account
/// endpoint and comes back on read, so the dialog shows what is stored.
/// </summary>
[Collection("Plugin")]
public class RequestWatchedTitlesConfigTests
{
    private const string UserId = "aabbccddeeff00112233445566778899";

    [Fact]
    public void PutAccounts_StoresTheToggle_AndANewAccountDefaultsOff()
    {
        using var h = new ControllerTestHarness(UserId);
        h.AddAccount(UserId, "existing");

        h.Controller.PutAccounts(new AccountsUpdateRequest
        {
            Accounts = new List<AccountUpdateRequest>
            {
                new() { LetterboxdUsername = "existing", LetterboxdPassword = "pw", Enabled = true, RequestWatchedTitles = true },
                new() { LetterboxdUsername = "brand-new", LetterboxdPassword = "pw", Enabled = true }
            }
        });

        var mine = h.Config.Accounts.Where(a => a.UserJellyfinId == UserId).ToList();
        Assert.True(mine.Single(a => a.LetterboxdUsername == "existing").RequestWatchedTitles);
        Assert.False(mine.Single(a => a.LetterboxdUsername == "brand-new").RequestWatchedTitles);
    }

    [Fact]
    public void GetAccounts_CarriesTheToggle()
    {
        using var h = new ControllerTestHarness(UserId);
        h.AddAccount(UserId, "existing").RequestWatchedTitles = true;

        var ok = Assert.IsType<OkObjectResult>(h.Controller.GetAccounts());
        var accounts = (IEnumerable)ok.Value!.GetType()
            .GetProperty("accounts", BindingFlags.Public | BindingFlags.Instance)!.GetValue(ok.Value)!;
        var account = accounts.Cast<object>().Single();

        Assert.True((bool)account.GetType().GetProperty("requestWatchedTitles")!.GetValue(account)!);
    }
}
