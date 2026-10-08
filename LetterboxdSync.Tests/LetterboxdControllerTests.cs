using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Jellyfin.Database.Implementations.Entities;
using LetterboxdSync;
using LetterboxdSync.Api;
using LetterboxdSync.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// Endpoint-level tests for LetterboxdController. Uses ControllerTestHarness to
/// stand up Plugin.Instance and substitute Jellyfin services. Each test owns its
/// harness via using-disposal so configuration state never leaks between tests.
/// </summary>
[Collection("Plugin")]
public class LetterboxdControllerTests
{
    /// <summary>Reads an anonymous-object property off an OkObjectResult / BadRequestObjectResult.</summary>
    private static T? Prop<T>(IActionResult result, string name)
    {
        var value = result switch
        {
            OkObjectResult ok => ok.Value,
            BadRequestObjectResult bad => bad.Value,
            ObjectResult obj => obj.Value,
            _ => null
        };
        if (value == null) return default;
        var prop = value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        return prop == null ? default : (T?)prop.GetValue(value);
    }

    private const string UserId = "abc123def456abc123def456abc12345"; // 32 hex chars
    private const string OtherUserId = "ffffffffffffffffffffffffffffffff";

    // ----- GetProgress -----

    [Fact]
    public void GetProgress_ReturnsCurrentSyncSnapshot()
    {
        using var h = new ControllerTestHarness();
        SyncProgress.Start(SyncProgress.TrackLetterboxd, "ProgressTest", "p");
        SyncProgress.SetTotal(SyncProgress.TrackLetterboxd, 5);

        var result = h.Controller.GetProgress();

        Assert.IsType<OkObjectResult>(result);
        var ok = (OkObjectResult)result;
        var t = ok.Value!.GetType();
        Assert.Equal("ProgressTest", t.GetProperty("taskName")!.GetValue(ok.Value));
        Assert.Equal(5, t.GetProperty("totalItems")!.GetValue(ok.Value));
    }

    // ----- GetStats / GetHistory -----

    /// <summary>Harness whose caller resolves to a real Jellyfin user, which /Stats and /History require.</summary>
    private static ControllerTestHarness ResolvedUserHarness()
    {
        var user = new User("alice", "test-provider-id", "test-reset-id");
        var h = new ControllerTestHarness(currentUserId: user.Id.ToString("N"));
        h.UserManager.GetUsers().Returns(new List<User> { user });
        return h;
    }

    [Fact]
    public void GetStats_UnresolvedUser_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);

        var result = h.Controller.GetStats();

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Could not determine user", Prop<string>(result, "error"));
    }

    [Fact]
    public void GetHistory_UnresolvedUser_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness(currentUserId: null);

        var result = h.Controller.GetHistory();

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Could not determine user", Prop<string>(result, "error"));
    }

    [Fact]
    public void GetStats_ReturnsCurrentStats()
    {
        using var h = ResolvedUserHarness();

        var result = h.Controller.GetStats();

        var ok = Assert.IsType<OkObjectResult>(result);
        var t = ok.Value!.GetType();
        // We don't assert exact numbers because SyncHistory persists across tests;
        // we assert only the response shape (all six stat keys present, integers).
        Assert.NotNull(t.GetProperty("total"));
        Assert.NotNull(t.GetProperty("success"));
        Assert.NotNull(t.GetProperty("failed"));
        Assert.NotNull(t.GetProperty("skipped"));
        Assert.NotNull(t.GetProperty("rewatches"));
        Assert.NotNull(t.GetProperty("requested"));
    }

    [Fact]
    public void GetStats_RequestedEvents_AreCountedSeparatelyFromOtherStatuses()
    {
        // Unlike GetStats_ReturnsCurrentStats above, this isolates SyncHistory to a temp
        // file so it can assert an exact requested count against a known mix of statuses,
        // Seerr auto-request activity (Status=Requested) must not bleed into success/failed/
        // skipped/rewatches, and vice versa.
        var dataPath = Path.Combine(Path.GetTempPath(), "lbs-stats-" + Guid.NewGuid().ToString("N") + ".jsonl");
        SyncHistory.DataPathOverride = dataPath;
        SyncHistory.ResetForTesting();
        try
        {
            // ControllerTestHarness.SetUsers substitutes the concrete Jellyfin User class,
            // which NSubstitute can't proxy (no parameterless constructor). Build a real
            // User instead, matching the pattern WatchlistSyncRunnerTests uses, and derive
            // the harness's currentUserId from its actual generated Id.
            var user = new User("alice", "test-provider-id", "test-reset-id");
            var userIdHex = user.Id.ToString("N");

            using var h = new ControllerTestHarness(currentUserId: userIdHex);
            h.UserManager.GetUsers().Returns(new List<User> { user });

            SyncHistory.Record(new SyncEvent
            {
                FilmTitle = "Sinners",
                TmdbId = 1,
                Username = "alice",
                Timestamp = DateTime.UtcNow,
                Status = SyncStatus.Success
            });
            SyncHistory.Record(new SyncEvent
            {
                FilmTitle = "Requested Film",
                TmdbId = 2,
                Username = "alice",
                Timestamp = DateTime.UtcNow,
                Status = SyncStatus.Requested,
                Source = SyncEventSources.SeerrAutoRequestFilm
            });
            SyncHistory.Record(new SyncEvent
            {
                FilmTitle = "Requested Show",
                TmdbId = 3,
                Username = "alice",
                Timestamp = DateTime.UtcNow,
                Status = SyncStatus.Requested,
                Source = SyncEventSources.SeerrAutoRequestTv
            });

            var result = h.Controller.GetStats();

            var ok = Assert.IsType<OkObjectResult>(result);
            var t = ok.Value!.GetType();
            Assert.Equal(3, (int)t.GetProperty("total")!.GetValue(ok.Value)!);
            Assert.Equal(1, (int)t.GetProperty("success")!.GetValue(ok.Value)!);
            Assert.Equal(2, (int)t.GetProperty("requested")!.GetValue(ok.Value)!);
        }
        finally
        {
            SyncHistory.DataPathOverride = null;
            SyncHistory.ResetForTesting();
            try { File.Delete(dataPath); } catch { }
        }
    }

    [Fact]
    public void GetHistory_DefaultParams_ReturnsPageWithCount()
    {
        using var h = ResolvedUserHarness();

        var result = h.Controller.GetHistory();

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(0, Prop<int>(ok, "offset"));
        Assert.Equal(50, Prop<int>(ok, "count"));
    }

    [Fact]
    public void GetHistory_CapsCountAt250()
    {
        using var h = ResolvedUserHarness();

        var result = h.Controller.GetHistory(count: 9999);

        Assert.Equal(250, Prop<int>(result, "count"));
    }

    [Fact]
    public void GetHistory_EveryPageSizeTheDashboardsAskFor_IsServedInFull()
    {
        var asm = typeof(Plugin).Assembly;
        var requested = new List<int>();
        foreach (var page in new[] { "userPage.html", "configPage.html", "jellyscribe.js" })
        {
            var resource = asm.GetManifestResourceNames().Single(n => n.EndsWith(".Web." + page, StringComparison.Ordinal));
            using var reader = new StreamReader(asm.GetManifestResourceStream(resource)!);
            var html = reader.ReadToEnd();
            // Literal sizes in the request ("/History?count=250", "/History', { count: 250") and the
            // page-size settings the requests use ("histChunk: 200", "pageSize: 25").
            requested.AddRange(System.Text.RegularExpressions.Regex
                .Matches(html, @"/History(?:\?count=|'\s*,\s*\{\s*count:\s*)(\d+)|\b(?:histChunk|pageSize):\s*(\d+)")
                .Select(m => int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)));
        }

        Assert.NotEmpty(requested);
        Assert.All(requested, n => Assert.True(n <= LetterboxdController.MaxHistoryPage,
            $"a dashboard asks for {n} history rows but the endpoint serves at most {LetterboxdController.MaxHistoryPage}"));
    }

    [Fact]
    public void GetHistory_NegativeOffset_ClampedToZero()
    {
        using var h = ResolvedUserHarness();

        var result = h.Controller.GetHistory(offset: -50);

        Assert.Equal(0, Prop<int>(result, "offset"));
    }

    // ----- GetAccounts / PutAccounts (multi-account, per-user) -----

    [Fact]
    public void GetAccounts_NoUserClaim_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness(currentUserId: null);

        var result = h.Controller.GetAccounts();

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void GetAccounts_ReturnsOnlyCallingUsersAccounts()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "mine-a");
        h.AddAccount(UserId, "mine-b");
        h.AddAccount(OtherUserId, "someone-else");

        var result = h.Controller.GetAccounts();

        var ok = Assert.IsType<OkObjectResult>(result);
        var accounts = Prop<System.Collections.IEnumerable>(ok, "accounts")!;
        var list = accounts.Cast<object>().ToList();
        Assert.Equal(2, list.Count);
        var usernames = list.Select(a => a.GetType().GetProperty("letterboxdUsername")!.GetValue(a)?.ToString()).ToHashSet();
        Assert.Contains("mine-a", usernames);
        Assert.Contains("mine-b", usernames);
        Assert.DoesNotContain("someone-else", usernames);
    }

    private static void SetRole(ControllerTestHarness h, bool admin)
        => h.Controller.ControllerContext.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(new[]
            {
                new System.Security.Claims.Claim("Jellyfin-UserId", UserId),
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, admin ? "Administrator" : "User"),
            }, "Test"));

    [Fact]
    public void PutAccounts_NonAdmin_CannotNameThePlaylist_KeepsTheStoredName()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "mine").PlaylistName = "Named By Admin";
        SetRole(h, admin: false);

        var result = h.Controller.PutAccounts(new AccountsUpdateRequest
        {
            Accounts = new List<AccountUpdateRequest>
            {
                new() { LetterboxdUsername = "mine", Enabled = true, PlaylistName = "Staff Picks" },
                new() { LetterboxdUsername = "fresh", Enabled = true, PlaylistName = "Staff Picks" },
            }
        });

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("Named By Admin", h.Config.Accounts.Single(a => a.LetterboxdUsername == "mine").PlaylistName);
        Assert.Null(h.Config.Accounts.Single(a => a.LetterboxdUsername == "fresh").PlaylistName);
    }

    [Fact]
    public void PutAccounts_Admin_CanNameThePlaylist()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "mine");
        SetRole(h, admin: true);

        h.Controller.PutAccounts(new AccountsUpdateRequest
        {
            Accounts = new List<AccountUpdateRequest> { new() { LetterboxdUsername = "mine", PlaylistName = " Our Films " } }
        });

        Assert.Equal("Our Films", h.Config.Accounts.Single(a => a.LetterboxdUsername == "mine").PlaylistName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetAccounts_TellsThePageWhetherTheCallerMayNameThePlaylist(bool admin)
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "mine");
        SetRole(h, admin);

        var ok = Assert.IsType<OkObjectResult>(h.Controller.GetAccounts());
        Assert.Equal(admin, Prop<bool>(ok, "canSetWatchlistName"));
    }

    [Fact]
    public void GetAccounts_OrdersPrimaryFirst()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "secondary");
        var primary = h.AddAccount(UserId, "primary");
        primary.IsPrimary = true;

        var result = h.Controller.GetAccounts();

        var ok = Assert.IsType<OkObjectResult>(result);
        var accounts = Prop<System.Collections.IEnumerable>(ok, "accounts")!;
        var first = accounts.Cast<object>().First();
        Assert.Equal("primary", first.GetType().GetProperty("letterboxdUsername")!.GetValue(first));
    }

    [Fact]
    public void PutAccounts_NoUserClaim_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness(currentUserId: null);

        var result = h.Controller.PutAccounts(new AccountsUpdateRequest());

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void PutAccounts_NullAccountsList_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);

        var result = h.Controller.PutAccounts(new AccountsUpdateRequest { Accounts = null! });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void PutAccounts_EmptyUsername_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);

        var result = h.Controller.PutAccounts(new AccountsUpdateRequest
        {
            Accounts = new()
            {
                new AccountUpdateRequest { LetterboxdUsername = "ok" },
                new AccountUpdateRequest { LetterboxdUsername = "   " }
            }
        });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("#2", Prop<string>(result, "error") ?? string.Empty);
    }

    [Fact]
    public void PutAccounts_ReplacesCallingUsersAccounts()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "old-a");
        h.AddAccount(UserId, "old-b");

        var result = h.Controller.PutAccounts(new AccountsUpdateRequest
        {
            Accounts = new()
            {
                new AccountUpdateRequest { LetterboxdUsername = "new-1", Enabled = true, IsPrimary = true },
                new AccountUpdateRequest { LetterboxdUsername = "new-2", Enabled = true }
            }
        });

        Assert.IsType<OkObjectResult>(result);
        var mine = h.Config.Accounts.Where(a => a.UserJellyfinId == UserId).ToList();
        Assert.Equal(2, mine.Count);
        var names = mine.Select(a => a.LetterboxdUsername).ToHashSet();
        Assert.Equal(new[] { "new-1", "new-2" }.ToHashSet(), names);
    }

    [Fact]
    public void PutAccounts_PreservesOtherUsersAccounts()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(OtherUserId, "untouched", enabled: true);

        h.Controller.PutAccounts(new AccountsUpdateRequest
        {
            Accounts = new() { new AccountUpdateRequest { LetterboxdUsername = "mine", Enabled = true } }
        });

        var other = h.Config.Accounts.Single(a => a.UserJellyfinId == OtherUserId);
        Assert.Equal("untouched", other.LetterboxdUsername);
        Assert.True(other.Enabled);
    }

    [Fact]
    public void PutAccounts_StampsCallingUserIdOntoEveryRow()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);

        // The request shape doesn't even carry UserJellyfinId, but a hostile or
        // confused client might try to spoof one via reflection or a custom payload.
        // The endpoint must overwrite to the calling user's id regardless.
        h.Controller.PutAccounts(new AccountsUpdateRequest
        {
            Accounts = new() { new AccountUpdateRequest { LetterboxdUsername = "mine", Enabled = true } }
        });

        var mine = h.Config.Accounts.Single(a => a.LetterboxdUsername == "mine");
        Assert.Equal(UserId, mine.UserJellyfinId);
    }

    [Fact]
    public void PutAccounts_NormalisesPrimaryFlag_AutoPromotesFirstWhenNoneMarked()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);

        h.Controller.PutAccounts(new AccountsUpdateRequest
        {
            Accounts = new()
            {
                new AccountUpdateRequest { LetterboxdUsername = "a", Enabled = true },
                new AccountUpdateRequest { LetterboxdUsername = "b", Enabled = true }
            }
        });

        var mine = h.Config.Accounts.Where(a => a.UserJellyfinId == UserId).ToList();
        Assert.Single(mine, a => a.IsPrimary);
    }

    [Fact]
    public void PutAccounts_NormalisesPrimaryFlag_DemotesExtras()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);

        h.Controller.PutAccounts(new AccountsUpdateRequest
        {
            Accounts = new()
            {
                new AccountUpdateRequest { LetterboxdUsername = "a", Enabled = true, IsPrimary = true },
                new AccountUpdateRequest { LetterboxdUsername = "b", Enabled = true, IsPrimary = true }
            }
        });

        var mine = h.Config.Accounts.Where(a => a.UserJellyfinId == UserId).ToList();
        Assert.Single(mine, a => a.IsPrimary);
    }

    // Households share one Letterboxd account across several Jellyfin users. The token cache is
    // keyed by username + password hash, so saving a username another user already linked is safe
    // and must keep working; don't reintroduce an "already linked" rejection.
    [Fact]
    public void PutAccounts_UsernameAlsoLinkedByOtherUser_Saves()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(OtherUserId, "household");

        var result = h.Controller.PutAccounts(new AccountsUpdateRequest
        {
            Accounts = new() { new AccountUpdateRequest { LetterboxdUsername = "household", LetterboxdPassword = "pw", Enabled = true } }
        });

        Assert.IsType<OkObjectResult>(result);
        Assert.Single(h.Config.Accounts, a => a.UserJellyfinId == UserId && a.LetterboxdUsername == "household");
        Assert.Single(h.Config.Accounts, a => a.UserJellyfinId == OtherUserId && a.LetterboxdUsername == "household");
    }

    [Fact]
    public void PutAccounts_SameUserResavesOwnUsername_Succeeds()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "mine");

        var result = h.Controller.PutAccounts(new AccountsUpdateRequest
        {
            Accounts = new() { new AccountUpdateRequest { LetterboxdUsername = "mine", LetterboxdPassword = "new", Enabled = true } }
        });

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("new", h.Config.Accounts.Single(a => a.UserJellyfinId == UserId).LetterboxdPassword);
    }

    // ----- StartSync -----

    [Fact]
    public void StartSync_NoUserClaim_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness(currentUserId: null);

        var result = h.Controller.StartSync();

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void StartSync_NoEnabledAccount_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        // No accounts at all.

        var result = h.Controller.StartSync();

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("No enabled Letterboxd account", Prop<string>(result, "error") ?? string.Empty);
    }

    [Fact]
    public void StartSync_DisabledAccount_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "8bitproxy", enabled: false);

        var result = h.Controller.StartSync();

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void StartSync_EnabledAccount_Returns202Accepted()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "8bitproxy");

        var result = h.Controller.StartSync();

        var accepted = Assert.IsType<AcceptedResult>(result);
        Assert.True(Prop<bool>(accepted, "started"));
    }

    // ----- StartWatchlistSync -----

    [Fact]
    public void StartWatchlistSync_NoEnabledAccount_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);

        var result = h.Controller.StartWatchlistSync();

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void StartWatchlistSync_WatchlistDisabled_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "8bitproxy", watchlistSync: false);

        var result = h.Controller.StartWatchlistSync();

        Assert.IsType<BadRequestObjectResult>(result);
        // Without a specific letterboxdUsername, the multi-account controller returns
        // the broader "no eligible accounts" error rather than the per-account
        // "Watchlist sync is disabled" message.
        Assert.Contains("No enabled accounts with watchlist sync",
            Prop<string>(result, "error") ?? string.Empty);
    }

    [Fact]
    public void StartWatchlistSync_WatchlistEnabled_Returns202Accepted()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "8bitproxy", watchlistSync: true);

        var result = h.Controller.StartWatchlistSync();

        Assert.IsType<AcceptedResult>(result);
    }

    // ----- PostReview validation -----

    [Fact]
    public async Task PostReview_MissingFilmSlug_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "8bitproxy");

        var result = await h.Controller.PostReview(new ReviewRequest { FilmSlug = "" });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("filmSlug", Prop<string>(result, "error") ?? string.Empty);
    }

    [Fact]
    public async Task PostReview_MissingTextNotRewatch_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "8bitproxy");

        var result = await h.Controller.PostReview(new ReviewRequest
        {
            FilmSlug = "sinners",
            ReviewText = null,
            IsRewatch = false
        });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("reviewText", Prop<string>(result, "error") ?? string.Empty);
    }

    [Fact]
    public async Task PostReview_NoUserClaim_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness(currentUserId: null);

        var result = await h.Controller.PostReview(new ReviewRequest
        {
            FilmSlug = "sinners",
            ReviewText = "good"
        });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task PostReview_NoEnabledAccountForUser_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "8bitproxy", enabled: false);

        var result = await h.Controller.PostReview(new ReviewRequest
        {
            FilmSlug = "sinners",
            ReviewText = "good"
        });

        Assert.IsType<BadRequestObjectResult>(result);
        // Multi-account controller pluralises ("No Letterboxd accounts configured"),
        // singular form remained on main pre-multi-account; assert on the shared prefix.
        Assert.Contains("No Letterboxd account", Prop<string>(result, "error") ?? string.Empty);
    }

    [Fact]
    public async Task PostReview_SuccessfulFlow_ReturnsOkAndCallsService()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "8bitproxy");

        var service = NSubstitute.Substitute.For<ILetterboxdService>();
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => System.Threading.Tasks.Task.FromResult(service);
        try
        {
            var result = await h.Controller.PostReview(new ReviewRequest
            {
                FilmSlug = "sinners",
                ReviewText = "great",
                Rating = 4.5,
                TmdbId = 1233413
            });

            Assert.IsType<OkObjectResult>(result);
            Assert.True(Prop<bool>(result, "success"));
            // With no earlier diary entry it is a new one, dated today, and the date is sent
            // explicitly so the history row records the same day the entry has.
            await service.Received(1).PostReviewAsync(
                "sinners", "great", false, false, Helpers.ToLocalViewingDate(System.DateTime.UtcNow).ToString("yyyy-MM-dd"), 4.5, 1233413);
        }
        finally
        {
            LetterboxdServiceFactory.OverrideForTesting = null;
        }
    }

    [Fact]
    public async Task PostReview_ServiceThrows_ReturnsBadRequestWithError()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "8bitproxy");

        var service = NSubstitute.Substitute.For<ILetterboxdService>();
        service.When(s => s.PostReviewAsync(
            NSubstitute.Arg.Any<string>(), NSubstitute.Arg.Any<string?>(),
            NSubstitute.Arg.Any<bool>(), NSubstitute.Arg.Any<bool>(),
            NSubstitute.Arg.Any<string?>(), NSubstitute.Arg.Any<double?>(),
            NSubstitute.Arg.Any<int?>())).Do(_ => throw new System.Exception("Cloudflare 403"));
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => System.Threading.Tasks.Task.FromResult(service);
        try
        {
            var result = await h.Controller.PostReview(new ReviewRequest
            {
                FilmSlug = "sinners",
                ReviewText = "great",
                TmdbId = 1233413
            });

            Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains("Cloudflare", Prop<string>(result, "error") ?? string.Empty);
        }
        finally
        {
            LetterboxdServiceFactory.OverrideForTesting = null;
        }
    }

    [Fact]
    public async Task PostReview_RewatchWithoutText_AllowedThroughValidation()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "8bitproxy");

        var service = NSubstitute.Substitute.For<ILetterboxdService>();
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) => System.Threading.Tasks.Task.FromResult(service);
        try
        {
            var result = await h.Controller.PostReview(new ReviewRequest
            {
                FilmSlug = "sinners",
                ReviewText = null,
                IsRewatch = true,
                TmdbId = 1233413
            });

            Assert.IsType<OkObjectResult>(result);
            // Every argument is a matcher: NSubstitute cannot tell a literal null from a matcher's slot.
            await service.Received(1).PostReviewAsync(
                NSubstitute.Arg.Is("sinners"), NSubstitute.Arg.Is<string?>(t => t == null),
                NSubstitute.Arg.Is(false), NSubstitute.Arg.Is(true),
                NSubstitute.Arg.Any<string?>(), NSubstitute.Arg.Any<double?>(), NSubstitute.Arg.Is<int?>(1233413));
        }
        finally
        {
            LetterboxdServiceFactory.OverrideForTesting = null;
        }
    }

    // ----- TestJellyseerr -----

    [Fact]
    public void TestJellyseerr_RequiresElevation()
    {
        var method = typeof(LetterboxdController).GetMethod(nameof(LetterboxdController.TestJellyseerr));
        var attr = method!.GetCustomAttribute<AuthorizeAttribute>();

        Assert.Equal("RequiresElevation", attr?.Policy);
    }

    [Fact]
    public async Task TestJellyseerr_NotConfigured_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness();

        var result = await h.Controller.TestJellyseerr(new JellyseerrTestRequest());

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.False(Prop<bool>(result, "success"));
    }

    [Fact]
    public async Task TestJellyseerr_OnlyUrl_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness();

        var result = await h.Controller.TestJellyseerr(new JellyseerrTestRequest
        {
            Url = "http://localhost:5055"
        });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    private sealed class ThrowingHandler : System.Net.Http.HttpMessageHandler
    {
        public const string Detail = "No connection could be made to seerr-internal.example:5055 (Connection refused)";

        protected override System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
            => throw new System.Net.Http.HttpRequestException(Detail);
    }

    [Fact]
    public async Task TestJellyseerr_ConnectionFails_ReturnsGenericErrorWithoutExceptionText()
    {
        using var h = new ControllerTestHarness();
        // The real failure is an HttpRequestException whose message names the host and port.
        LetterboxdController.SeerrTestHandlerForTesting = new ThrowingHandler();
        try
        {
            var result = await h.Controller.TestJellyseerr(new JellyseerrTestRequest
            {
                Url = "http://seerr-internal.example:5055",
                ApiKey = "test-key"
            });

            Assert.IsType<BadRequestObjectResult>(result);
            Assert.False(Prop<bool>(result, "success"));
            var error = Prop<string>(result, "error");
            Assert.False(string.IsNullOrEmpty(error));
            Assert.DoesNotContain("seerr-internal", error!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("refused", error!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            LetterboxdController.SeerrTestHandlerForTesting = null;
        }
    }

    // ----- GetLogs -----

    [Fact]
    public void GetLogs_LogDirectoryEmpty_ReturnsNoFiles()
    {
        using var h = new ControllerTestHarness();

        var result = h.Controller.GetLogs();

        Assert.IsType<OkObjectResult>(result);
        var lines = Prop<string[]>(result, "lines");
        Assert.NotNull(lines);
        Assert.Empty(lines!);
    }

    [Fact]
    public void GetLogs_FiltersLetterboxdSyncLines()
    {
        using var h = new ControllerTestHarness();
        var logFile = System.IO.Path.Combine(h.LogDir, "log_20260505.log");
        System.IO.File.WriteAllText(logFile,
            "[2026-05-05 10:00:00.000 +00:00] [INF] [1] Some.Other: unrelated line\n" +
            "[2026-05-05 10:00:01.000 +00:00] [INF] [1] LetterboxdSync.Foo: relevant 1\n" +
            "[2026-05-05 10:00:02.000 +00:00] [INF] [1] Another.Thing: unrelated\n" +
            "[2026-05-05 10:00:03.000 +00:00] [INF] [1] LetterboxdSync.Bar: relevant 2\n");

        var result = h.Controller.GetLogs();

        var lines = Prop<System.Collections.Generic.List<string>>(result, "lines");
        Assert.NotNull(lines);
        Assert.Equal(2, lines!.Count);
        Assert.Contains(lines, l => l.Contains("relevant 1"));
        Assert.Contains(lines, l => l.Contains("relevant 2"));
    }

    [Fact]
    public void GetLogs_CapturesMultiLineExceptionContinuations()
    {
        using var h = new ControllerTestHarness();
        var logFile = System.IO.Path.Combine(h.LogDir, "log_20260505.log");
        // A real multi-line error: the header carries the tag; the exception message
        // and stack frames continue on lines that do NOT carry it.
        System.IO.File.WriteAllText(logFile,
            "[2026-05-05 10:00:00.000 +00:00] [ERR] [1] LetterboxdSync.PlaybackHandler: sync failed\n" +
            "System.Exception: something broke\n" +
            "   at LetterboxdSync.PlaybackHandler.Do()\n" +
            "   at System.Threading.Tasks.Task.Run()\n" +
            "[2026-05-05 10:00:01.000 +00:00] [INF] [1] Other.Thing: unrelated, must NOT be captured\n");

        var result = h.Controller.GetLogs();
        var lines = Prop<System.Collections.Generic.List<string>>(result, "lines");

        Assert.NotNull(lines);
        // Header + 3 continuation lines, and nothing from the unrelated entry after it.
        Assert.Equal(4, lines!.Count);
        Assert.Contains(lines, l => l.Contains("System.Exception: something broke"));
        Assert.Contains(lines, l => l.Contains("at LetterboxdSync.PlaybackHandler.Do()"));
        Assert.DoesNotContain(lines, l => l.Contains("must NOT be captured"));
    }

    [Fact]
    public void GetLogs_NegativeMaxLines_DoesNotThrow()
    {
        using var h = new ControllerTestHarness();
        var logFile = System.IO.Path.Combine(h.LogDir, "log_20260505.log");
        System.IO.File.WriteAllText(logFile,
            "[2026-05-05 10:00:00.000 +00:00] [INF] [1] LetterboxdSync.Foo: a\n" +
            "[2026-05-05 10:00:01.000 +00:00] [INF] [1] LetterboxdSync.Foo: b\n");

        // Negative maxLines must clamp, not crash with ArgumentOutOfRangeException.
        var result = h.Controller.GetLogs(maxLines: -1);
        Assert.IsType<OkObjectResult>(result);
        var lines = Prop<System.Collections.Generic.List<string>>(result, "lines");
        Assert.NotNull(lines);
        Assert.NotEmpty(lines!);
    }

    [Fact]
    public void GetLogs_RespectsMaxLinesCap()
    {
        using var h = new ControllerTestHarness();
        var logFile = System.IO.Path.Combine(h.LogDir, "log_20260505.log");
        var lines = string.Join("\n",
            Enumerable.Range(0, 100).Select(i => $"[2026-05-05 10:00:{i % 60:D2}.000 +00:00] [INF] [1] LetterboxdSync.X: line {i}"));
        System.IO.File.WriteAllText(logFile, lines);

        var result = h.Controller.GetLogs(maxLines: 10);

        var returned = Prop<System.Collections.Generic.List<string>>(result, "lines");
        Assert.NotNull(returned);
        Assert.Equal(10, returned!.Count);
        // The last 10 lines should be returned, ordered.
        Assert.Contains("line 99", returned[^1]);
        Assert.Equal(100, Prop<int>(result, "totalMatches"));
    }

    [Fact]
    public void GetLogs_LogDirectoryMissing_ReturnsErrorPayload()
    {
        using var h = new ControllerTestHarness();
        // Point the log directory at a path that doesn't exist → directory-not-found branch.
        h.AppPaths.LogDirectoryPath.Returns(
            System.IO.Path.Combine(h.TempDir, "does-not-exist-" + Guid.NewGuid().ToString("N")));

        var result = h.Controller.GetLogs();

        Assert.IsType<OkObjectResult>(result);
        Assert.Empty(Prop<string[]>(result, "lines")!);
        Assert.Contains("log directory not found", Prop<string>(result, "error") ?? string.Empty);
    }

    // ----- StartSync: conflict + named-account targeting -----

    [Fact]
    public void StartSync_SyncAlreadyRunning_ReturnsConflict()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "8bitproxy");

        // Hold the global gate so LetterboxdSyncRunner.IsRunning reports true.
        Assert.True(SyncGate.Instance.Wait(0));
        try
        {
            var result = h.Controller.StartSync();
            Assert.IsType<ConflictObjectResult>(result);
        }
        finally
        {
            SyncGate.Instance.Release();
        }
    }

    [Fact]
    public void StartSync_NamedAccountNotFound_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "8bitproxy");

        var result = h.Controller.StartSync(letterboxdUsername: "someone-else");

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("someone-else", Prop<string>(result, "error") ?? string.Empty);
    }

    [Fact]
    public void StartSync_NamedAccountFound_Returns202Accepted()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "8bitproxy");

        var result = h.Controller.StartSync(letterboxdUsername: "8bitproxy");

        Assert.IsType<AcceptedResult>(result);
    }

    // ----- StartWatchlistSync: user, conflict, named-account -----

    [Fact]
    public void StartWatchlistSync_NoUserClaim_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness(currentUserId: null);

        var result = h.Controller.StartWatchlistSync();

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void StartWatchlistSync_SyncAlreadyRunning_ReturnsConflict()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "8bitproxy", watchlistSync: true);

        Assert.True(SyncGate.Instance.Wait(0));
        try
        {
            var result = h.Controller.StartWatchlistSync();
            Assert.IsType<ConflictObjectResult>(result);
        }
        finally
        {
            SyncGate.Instance.Release();
        }
    }

    [Fact]
    public void StartWatchlistSync_NamedAccountNotFound_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "8bitproxy", watchlistSync: true);

        var result = h.Controller.StartWatchlistSync(letterboxdUsername: "ghost");

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("ghost", Prop<string>(result, "error") ?? string.Empty);
    }

    [Fact]
    public void StartWatchlistSync_NamedAccountWatchlistDisabled_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "8bitproxy", watchlistSync: false);

        var result = h.Controller.StartWatchlistSync(letterboxdUsername: "8bitproxy");

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("disabled", Prop<string>(result, "error") ?? string.Empty);
    }

    [Fact]
    public void StartWatchlistSync_NamedAccountEnabled_Returns202Accepted()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "8bitproxy", watchlistSync: true);

        var result = h.Controller.StartWatchlistSync(letterboxdUsername: "8bitproxy");

        Assert.IsType<AcceptedResult>(result);
    }

    // ----- PostReview: named-account targeting + Jellyfin rating writeback -----

    [Fact]
    public async Task PostReview_NamedAccountNotFound_ReturnsBadRequest()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "8bitproxy");

        var result = await h.Controller.PostReview(new ReviewRequest
        {
            FilmSlug = "sinners",
            ReviewText = "great",
            LetterboxdUsername = "not-mine"
        });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("not-mine", Prop<string>(result, "error") ?? string.Empty);
    }

    [Fact]
    public async Task PostReview_NamedAccountFound_PostsToThatAccountOnly()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        h.AddAccount(UserId, "8bitproxy");
        h.AddAccount(UserId, "deb");

        var service = Substitute.For<ILetterboxdService>();
        // Capture which Letterboxd username the factory was asked to authenticate.
        var authedUsernames = new System.Collections.Generic.List<string>();
        LetterboxdServiceFactory.OverrideForTesting = (u, _, _, _, _) =>
        {
            authedUsernames.Add(u);
            return System.Threading.Tasks.Task.FromResult(service);
        };
        try
        {
            var result = await h.Controller.PostReview(new ReviewRequest
            {
                FilmSlug = "sinners",
                ReviewText = "great",
                LetterboxdUsername = "deb"
            });

            Assert.IsType<OkObjectResult>(result);
            // Only the named account was targeted, not the fan-out across both.
            Assert.Equal(new[] { "deb" }, authedUsernames);
        }
        finally
        {
            LetterboxdServiceFactory.OverrideForTesting = null;
        }
    }

    [Fact]
    public async Task PostReview_SuccessWithRating_MirrorsRatingIntoJellyfin()
    {
        // Real User/Movie instances: the controller matches the claim id against
        // User.Id.ToString("N"), and User has no parameterless ctor to substitute.
        var user = new User("lachlan", "test-provider-id", "test-reset-id");
        var userId = user.Id.ToString("N");

        using var h = new ControllerTestHarness(currentUserId: userId);
        h.AddAccount(userId, "8bitproxy");
        h.UserManager.GetUsers().Returns(new[] { user });

        var movie = new Movie { Name = "Sinners" };
        movie.SetProviderId(MetadataProvider.Tmdb, "1233413");
        h.LibraryManager.GetItemList(Arg.Any<InternalItemsQuery>())
            .Returns(new List<BaseItem> { movie });

        var userData = new UserItemData { Key = "k" };
        h.UserDataManager.GetUserData(user, movie).Returns(userData);

        var service = Substitute.For<ILetterboxdService>();
        LetterboxdServiceFactory.OverrideForTesting = (_, _, _, _, _) =>
            System.Threading.Tasks.Task.FromResult(service);
        try
        {
            var result = await h.Controller.PostReview(new ReviewRequest
            {
                FilmSlug = "sinners",
                ReviewText = "great",
                Rating = 4.5,
                TmdbId = 1233413
            });

            Assert.IsType<OkObjectResult>(result);
            // 4.5 Letterboxd stars → 9.0 Jellyfin rating, written back and persisted.
            // Pinned to Import: RatingSyncHandler ignores Import saves, so this mirror can never
            // echo back out as a second Letterboxd push. Any other reason reopens that loop.
            Assert.Equal(9.0, userData.Rating);
            h.UserDataManager.Received(1).SaveUserData(
                user, movie, userData,
                MediaBrowser.Model.Entities.UserDataSaveReason.Import,
                Arg.Any<System.Threading.CancellationToken>());
        }
        finally
        {
            LetterboxdServiceFactory.OverrideForTesting = null;
        }
    }

    // ----- Excluded libraries (issue #124) -----

    [Fact]
    public void PutAccounts_StoresNormalisedExcludedLibraryIds_AndGetAccountsEchoesThem()
    {
        using var h = new ControllerTestHarness(currentUserId: UserId);
        const string anime = "0c5b2a1e9f3d4c7a8b6e5d4c3b2a1f0e";

        var result = h.Controller.PutAccounts(new AccountsUpdateRequest
        {
            Accounts = new()
            {
                new AccountUpdateRequest
                {
                    LetterboxdUsername = "mine",
                    // Dashed and upper-case duplicates collapse to one N-format id; junk is dropped.
                    ExcludedLibraryIds = new() { "0C5B2A1E-9F3D-4C7A-8B6E-5D4C3B2A1F0E", anime, "not-a-guid" }
                },
                new AccountUpdateRequest { LetterboxdUsername = "other", ExcludedLibraryIds = null }
            }
        });

        Assert.IsType<OkObjectResult>(result);
        var mine = h.Config.Accounts.Where(a => a.UserJellyfinId == UserId).ToList();
        Assert.Equal(new[] { anime }, mine.Single(a => a.LetterboxdUsername == "mine").ExcludedLibraryIds);
        Assert.Empty(mine.Single(a => a.LetterboxdUsername == "other").ExcludedLibraryIds);

        var get = Assert.IsType<OkObjectResult>(h.Controller.GetAccounts());
        var echoed = Prop<System.Collections.IEnumerable>(get, "accounts")!.Cast<object>()
            .Single(a => (string?)a.GetType().GetProperty("letterboxdUsername")!.GetValue(a) == "mine");
        Assert.Equal(new[] { anime },
            (IEnumerable<string>)echoed.GetType().GetProperty("excludedLibraryIds")!.GetValue(echoed)!);
    }

    [Fact]
    public void PutAccounts_OmittedExcludedLibraryIds_KeepsStoredListPerAccount()
    {
        // A client that predates the field must not clear what an account keeps off Letterboxd.
        using var h = new ControllerTestHarness(currentUserId: UserId);
        const string anime = "0c5b2a1e9f3d4c7a8b6e5d4c3b2a1f0e";
        h.AddAccount(UserId, "Mine").ExcludedLibraryIds.Add(anime);

        h.Controller.PutAccounts(new AccountsUpdateRequest
        {
            Accounts = new()
            {
                new AccountUpdateRequest { LetterboxdUsername = "mine", ExcludedLibraryIds = null },
                new AccountUpdateRequest { LetterboxdUsername = "brand-new", ExcludedLibraryIds = null }
            }
        });

        var mine = h.Config.Accounts.Where(a => a.UserJellyfinId == UserId).ToList();
        Assert.Equal(new[] { anime }, mine.Single(a => a.LetterboxdUsername == "mine").ExcludedLibraryIds);
        Assert.Empty(mine.Single(a => a.LetterboxdUsername == "brand-new").ExcludedLibraryIds);
    }
}
