using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using LetterboxdSync.Api;
using LetterboxdSync.Configuration;
using LetterboxdSync.Serializd;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// The per-account "Re-request watched titles" toggle (issue #165) saves through both account
/// endpoints, turns back off, and comes back on read, so the dialog shows what is stored.
/// </summary>
[Collection("Plugin")]
public class RequestWatchedTitlesConfigTests
{
    private const string UserId = "aabbccddeeff00112233445566778899";
    private const string SzEmail = "tvfan@example.com";

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
    public void PutAccounts_TurnsTheToggleBackOff()
    {
        using var h = new ControllerTestHarness(UserId);
        h.AddAccount(UserId, "existing").RequestWatchedTitles = true;

        h.Controller.PutAccounts(new AccountsUpdateRequest
        {
            Accounts = new List<AccountUpdateRequest>
            {
                new() { LetterboxdUsername = "existing", LetterboxdPassword = "pw", Enabled = true, RequestWatchedTitles = false }
            }
        });

        Assert.False(h.Config.Accounts.Single(a => a.UserJellyfinId == UserId).RequestWatchedTitles);
    }

    [Fact]
    public void GetAccounts_CarriesTheToggle()
    {
        using var h = new ControllerTestHarness(UserId);
        h.AddAccount(UserId, "existing").RequestWatchedTitles = true;

        Assert.True(ReadToggle(h.Controller.GetAccounts()));
    }

    [Fact]
    public void Serializd_PutStoresAndClearsTheToggle_AndGetCarriesIt()
    {
        using var h = new ControllerTestHarness(UserId);
        h.Config.SerializdAccounts.Add(new SerializdAccount { UserJellyfinId = UserId, Email = SzEmail, Password = "pw", Enabled = true });
        var controller = SerializdControllerFor(UserId);

        controller.PutAccounts(new SerializdController.AccountsUpdateRequest
        {
            Accounts = new() { new SerializdController.AccountItem { Email = SzEmail, Enabled = true, RequestWatchedTitles = true } }
        });
        Assert.True(h.Config.SerializdAccounts.Single().RequestWatchedTitles);
        Assert.True(ReadToggle(controller.GetAccounts()));

        controller.PutAccounts(new SerializdController.AccountsUpdateRequest
        {
            Accounts = new() { new SerializdController.AccountItem { Email = SzEmail, Enabled = true, RequestWatchedTitles = false } }
        });
        Assert.False(h.Config.SerializdAccounts.Single().RequestWatchedTitles);
    }

    private static bool ReadToggle(ActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var accounts = (IEnumerable)ok.Value!.GetType()
            .GetProperty("accounts", BindingFlags.Public | BindingFlags.Instance)!.GetValue(ok.Value)!;
        var account = accounts.Cast<object>().Single();
        return (bool)account.GetType().GetProperty("requestWatchedTitles")!.GetValue(account)!;
    }

    private static SerializdController SerializdControllerFor(string userId)
    {
        var controller = new SerializdController(NullLogger<SerializdController>.Instance,
            new SerializdSyncRunner(NullLoggerFactory.Instance, Substitute.For<ILibraryManager>(),
                Substitute.For<IUserManager>(), Substitute.For<IUserDataManager>()),
            new SerializdWatchlistSyncRunner(NullLoggerFactory.Instance, Substitute.For<ILibraryManager>(),
                Substitute.For<IUserManager>(), Substitute.For<ICollectionManager>(), Substitute.For<IPlaylistManager>()),
            Substitute.For<IUserManager>(), Substitute.For<ILibraryManager>());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", userId) }, "Test")) },
            RouteData = new RouteData()
        };
        return controller;
    }
}
