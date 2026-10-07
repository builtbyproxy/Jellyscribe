using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using LetterboxdSync.Api;
using LetterboxdSync.Configuration;
using LetterboxdSync.Serializd;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace LetterboxdSync.Tests.Serializd;

/// <summary>
/// PostReview needs a real Plugin.Instance (Config.GetEnabledSerializdAccountsForUser) and
/// writes to SerializdActivity, so this class joins the "Plugin" collection (like
/// LetterboxdControllerTests) to serialise against every other test touching the same
/// static singleton, and points SerializdActivity at a per-test temp file.
/// </summary>
[Collection("Plugin")]
public class SerializdControllerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager = Substitute.For<ILibraryManager>();
    private readonly SerializdController _controller;

    public SerializdControllerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-szctrl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var paths = Substitute.For<IApplicationPaths>();
        paths.PluginConfigurationsPath.Returns(_tempDir);
        paths.LogDirectoryPath.Returns(_tempDir);
        paths.DataPath.Returns(_tempDir);
        paths.CachePath.Returns(_tempDir);

        var xml = Substitute.For<IXmlSerializer>();
        xml.DeserializeFromFile(typeof(PluginConfiguration), Arg.Any<string>())
            .Returns(_ => new PluginConfiguration());

        new Plugin(paths, xml);

        SerializdActivity.DataPathOverride = Path.Combine(_tempDir, "serializd-activity.jsonl");
        SerializdActivity.ResetForTesting();

        _userManager = Substitute.For<IUserManager>();
        var runner = new SerializdSyncRunner(new LoggerFactory(), Substitute.For<ILibraryManager>(),
            Substitute.For<IUserManager>(), Substitute.For<IUserDataManager>());
        var watchlistRunner = new SerializdWatchlistSyncRunner(new LoggerFactory(), Substitute.For<ILibraryManager>(),
            Substitute.For<IUserManager>(), Substitute.For<ICollectionManager>(), Substitute.For<IPlaylistManager>());

        _controller = new SerializdController(new NullLogger<SerializdController>(), runner, watchlistRunner, _userManager, _libraryManager);
        LoginCheckLimiter.Serializd.ResetForTesting();
    }

    public void Dispose()
    {
        SerializdController.VerifyOverrideForTesting = null;
        SerializdServiceFactory.OverrideForTesting = null;
        SerializdSeasonFallback.SeasonLengthsReader = SerializdSeasonFallback.ReadSeasonLengths;
        SerializdActivity.DataPathOverride = null;
        SerializdActivity.ResetForTesting();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    /// <summary>Registers a Jellyfin user with one enabled Serializd account and returns both.</summary>
    private (User User, string IdHex) AddUserWithAccount(string email = "user@example.com", bool enabled = true)
    {
        var user = new User("lachlan", "test-provider-id", "test-reset-id");
        var idHex = user.Id.ToString("N");
        _userManager.GetUsers().Returns(new[] { user });
        Plugin.Instance!.Configuration.SerializdAccounts.Add(new SerializdAccount
        {
            UserJellyfinId = idHex,
            Email = email,
            Password = "pw",
            Enabled = enabled,
        });
        return (user, idHex);
    }

    /// <summary>
    /// Points the controller's ControllerContext at an authenticated Jellyfin-UserId claim, with the
    /// role claim Jellyfin's authentication handler issues ("Administrator" or "User").
    /// </summary>
    private void Authenticate(string userIdHex, bool admin = false)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("Jellyfin-UserId", userIdHex),
            new Claim(ClaimTypes.Role, admin ? "Administrator" : "User"),
        }, "Test"));
        _controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } };
    }

    /// <summary>Reads an anonymous-object property off an OkObjectResult / BadRequestObjectResult.</summary>
    private static T? Prop<T>(ActionResult result, string name)
    {
        var value = result switch
        {
            OkObjectResult ok => ok.Value,
            BadRequestObjectResult bad => bad.Value,
            ObjectResult obj => obj.Value,
            _ => null
        };
        if (value == null) return default;
        var prop = value.GetType().GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        return prop == null ? default : (T?)prop.GetValue(value);
    }

    [Fact]
    public async Task Verify_MissingCredentials_ReturnsBadRequest()
    {
        var result = await _controller.Verify(new SerializdController.VerifyRequest { Email = "", Password = "" });
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Verify_GoodLogin_ReturnsOkWithUsername()
    {
        Authenticate("aabbccddeeff00112233445566778899");
        SerializdController.VerifyOverrideForTesting = (_, _, _) => Task.FromResult<string?>("8bitproxy");

        var result = await _controller.Verify(
            new SerializdController.VerifyRequest { Email = "me@example.com", Password = "pw" });

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Contains("8bitproxy", ok.Value!.ToString());
    }

    [Fact]
    public async Task Verify_RepeatedFailures_AreRefusedWith429()
    {
        var (_, idHex) = AddUserWithAccount();
        Authenticate(idHex);
        var attempts = 0;
        SerializdController.VerifyOverrideForTesting = (_, _, _) =>
        {
            attempts++;
            throw new Exception("Serializd login failed (401): Incorrect password.");
        };

        for (var i = 0; i < LoginCheckLimiter.PerUserLimit; i++)
            Assert.IsType<BadRequestObjectResult>(await _controller.Verify(
                new SerializdController.VerifyRequest { Email = "me@example.com", Password = "wrong" + i }));

        var refused = await _controller.Verify(
            new SerializdController.VerifyRequest { Email = "me@example.com", Password = "wrong-again" });

        var obj = Assert.IsAssignableFrom<ObjectResult>(refused);
        Assert.Equal(StatusCodes.Status429TooManyRequests, obj.StatusCode);
        Assert.StartsWith("Too many login checks", Prop<string>(refused, "error"));
        Assert.Equal(LoginCheckLimiter.PerUserLimit, attempts);
    }

    [Fact]
    public async Task Verify_BadLogin_ReturnsBadRequest()
    {
        Authenticate("aabbccddeeff00112233445566778899");
        SerializdController.VerifyOverrideForTesting = (_, _, _) =>
            throw new Exception("Serializd login failed (401): Incorrect password.");

        var result = await _controller.Verify(
            new SerializdController.VerifyRequest { Email = "me@example.com", Password = "wrong" });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void SyncNow_NoAuthenticatedUser_ReturnsBadRequest()
    {
        // Empty HttpContext → no Jellyfin-UserId claim → can't determine the user.
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext(),
        };

        var result = _controller.SyncNow();
        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ----- PostReview validation -----

    [Fact]
    public async Task PostReview_NoUserClaim_ReturnsBadRequest()
    {
        // No Authenticate() call → no ControllerContext → GetCurrentUserId sees no claim.
        _controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        var result = await _controller.PostReview(new SerializdController.ReviewRequest { TmdbId = 1396, Rating = 8 });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task PostReview_MissingTmdbId_ReturnsBadRequest()
    {
        var (_, idHex) = AddUserWithAccount();
        Authenticate(idHex);

        var result = await _controller.PostReview(new SerializdController.ReviewRequest { TmdbId = 0, Rating = 8 });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task PostReview_MissingRatingAndText_ReturnsBadRequest()
    {
        var (_, idHex) = AddUserWithAccount();
        Authenticate(idHex);

        var result = await _controller.PostReview(new SerializdController.ReviewRequest { TmdbId = 1396 });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task PostReview_NoEnabledAccountForUser_ReturnsBadRequest()
    {
        // Registers the user but no Serializd account at all.
        var user = new User("lachlan", "test-provider-id", "test-reset-id");
        var idHex = user.Id.ToString("N");
        _userManager.GetUsers().Returns(new[] { user });
        Authenticate(idHex);

        var result = await _controller.PostReview(new SerializdController.ReviewRequest { TmdbId = 1396, Rating = 8 });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task PostReview_DisabledAccount_ReturnsBadRequest()
    {
        var (_, idHex) = AddUserWithAccount(enabled: false);
        Authenticate(idHex);

        var result = await _controller.PostReview(new SerializdController.ReviewRequest { TmdbId = 1396, Rating = 8 });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ----- PostReview success paths -----

    [Fact]
    public async Task PostReview_ShowLevel_Success_CallsCreateShowReviewNotEpisode()
    {
        var (_, idHex) = AddUserWithAccount();
        Authenticate(idHex);
        var service = Substitute.For<ISerializdService>();
        SerializdServiceFactory.OverrideForTesting = (_, _, _) => Task.FromResult(service);

        var result = await _controller.PostReview(new SerializdController.ReviewRequest
        {
            TmdbId = 1396,
            Rating = 9,
            ReviewText = "loved it",
            ContainsSpoilers = true,
            Title = "The Wire",
        });

        Assert.IsType<OkObjectResult>(result);
        await service.Received(1).CreateShowReviewAsync(1396, 9, "loved it", true);
        await service.DidNotReceive().CreateEpisodeReviewAsync(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task PostReview_EpisodeLevel_Success_CallsCreateEpisodeReviewNotShow()
    {
        var (_, idHex) = AddUserWithAccount();
        Authenticate(idHex);
        var service = Substitute.For<ISerializdService>();
        service.ResolveSeasonIdAsync(1396, 2).Returns(Task.FromResult<int?>(3573));
        SerializdServiceFactory.OverrideForTesting = (_, _, _) => Task.FromResult(service);

        var result = await _controller.PostReview(new SerializdController.ReviewRequest
        {
            TmdbId = 1396,
            Rating = 7,
            SeasonNumber = 2,
            EpisodeNumber = 5,
        });

        Assert.IsType<OkObjectResult>(result);
        // Serializd's own season id for season 2, the episode number unchanged.
        await service.Received(1).CreateEpisodeReviewAsync(1396, 3573, 5, 7, null, false);
        await service.DidNotReceive().CreateShowReviewAsync(
            Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<bool>());
    }

    /// <summary>A Serializd show listing one season: only season 1 resolves, with this many episodes.</summary>
    private static ISerializdService SingleSeasonShow(int showTmdbId, int seasonOneId, int seasonOneEpisodes)
    {
        var service = Substitute.For<ISerializdService>();
        service.ResolveSeasonIdAsync(showTmdbId, Arg.Any<int>()).Returns(Task.FromResult<int?>(null));
        service.ResolveSeasonIdAsync(showTmdbId, 1).Returns(Task.FromResult<int?>(seasonOneId));
        service.GetSeasonEpisodeCountAsync(showTmdbId, 1).Returns(Task.FromResult<int?>(seasonOneEpisodes));
        return service;
    }

    /// <summary>The caller's library holds the series, so its season lengths can be read.</summary>
    private Series SeriesInLibrary(int tmdbId)
    {
        var series = new Series { Name = "Show", Id = Guid.NewGuid() };
        series.SetProviderId(MetadataProvider.Tmdb, tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem> { series });
        return series;
    }

    [Fact]
    public async Task PostReview_EpisodeOfAShowSerializdListsAsOneSeason_PostsOnSeasonOneAtTheAbsoluteNumber()
    {
        var (caller, idHex) = AddUserWithAccount();
        Authenticate(idHex);
        var series = SeriesInLibrary(220542);
        Series? readFor = null;
        SerializdSeasonFallback.SeasonLengthsReader = s =>
        {
            readFor = s;
            return new Dictionary<int, int> { [1] = 10, [2] = 10 };
        };
        var service = SingleSeasonShow(220542, seasonOneId: 9001, seasonOneEpisodes: 20);
        SerializdServiceFactory.OverrideForTesting = (_, _, _) => Task.FromResult(service);

        var result = await _controller.PostReview(new SerializdController.ReviewRequest
        {
            TmdbId = 220542,
            ReviewText = "great episode",
            SeasonNumber = 2,
            EpisodeNumber = 3,
        });

        Assert.IsType<OkObjectResult>(result);
        Assert.Same(series, readFor);
        // The series is looked up in the caller's own library, never another user's.
        _libraryManager.Received().GetItemList(Arg.Is<InternalItemsQuery>(q => q.User != null && q.User.Id == caller.Id));
        await service.Received(1).CreateEpisodeReviewAsync(220542, 9001, 13, null, "great episode", false);
    }

    [Fact]
    public async Task PostReview_EpisodePastTheEndOfSerializdsSingleSeason_IsRefused()
    {
        var (_, idHex) = AddUserWithAccount();
        Authenticate(idHex);
        SeriesInLibrary(220542);
        SerializdSeasonFallback.SeasonLengthsReader = _ => new Dictionary<int, int> { [1] = 10, [2] = 10 };
        // Serializd's season 1 has 12 episodes: S2E3 would be S1E13, past its end.
        var service = SingleSeasonShow(220542, seasonOneId: 9001, seasonOneEpisodes: 12);
        SerializdServiceFactory.OverrideForTesting = (_, _, _) => Task.FromResult(service);

        var result = await _controller.PostReview(new SerializdController.ReviewRequest
        {
            TmdbId = 220542,
            Rating = 8,
            SeasonNumber = 2,
            EpisodeNumber = 3,
        });

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("no episode matching S2E3", System.Text.Json.JsonSerializer.Serialize(bad.Value));
        await service.DidNotReceive().CreateEpisodeReviewAsync(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<bool>());
        Assert.Equal(0, SerializdActivity.GetPage(0, 10).Total);
    }

    [Fact]
    public async Task PostReview_RatingOnlyNoText_IsAllowed()
    {
        var (_, idHex) = AddUserWithAccount();
        Authenticate(idHex);
        var service = Substitute.For<ISerializdService>();
        SerializdServiceFactory.OverrideForTesting = (_, _, _) => Task.FromResult(service);

        var result = await _controller.PostReview(new SerializdController.ReviewRequest { TmdbId = 1396, Rating = 5 });

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task PostReview_Success_RecordsReviewSourceActivityEvent()
    {
        var (user, idHex) = AddUserWithAccount();
        Authenticate(idHex);
        var service = Substitute.For<ISerializdService>();
        SerializdServiceFactory.OverrideForTesting = (_, _, _) => Task.FromResult(service);

        await _controller.PostReview(new SerializdController.ReviewRequest
        {
            TmdbId = 1396,
            Rating = 9,
            ReviewText = "loved it",
            Title = "The Wire",
        });

        var (events, total) = SerializdActivity.GetPage(0, 10, user.Username);
        Assert.Equal(1, total);
        Assert.Equal("review", events[0].Source);
        Assert.Equal("The Wire", events[0].FilmTitle);
    }

    // ----- PostReview per-account failure isolation -----

    [Fact]
    public async Task PostReview_AllAccountsFail_ReturnsBadRequest()
    {
        var (_, idHex) = AddUserWithAccount();
        Authenticate(idHex);
        SerializdServiceFactory.OverrideForTesting = (_, _, _) => throw new Exception("Serializd 500");

        var result = await _controller.PostReview(new SerializdController.ReviewRequest { TmdbId = 1396, Rating = 7 });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task PostReview_OneAccountFails_OtherStillPosts_ReturnsOk()
    {
        var (_, idHex) = AddUserWithAccount(email: "first@example.com");
        Plugin.Instance!.Configuration.SerializdAccounts.Add(new SerializdAccount
        {
            UserJellyfinId = idHex,
            Email = "second@example.com",
            Password = "pw2",
            Enabled = true,
        });
        Authenticate(idHex);

        var goodService = Substitute.For<ISerializdService>();
        SerializdServiceFactory.OverrideForTesting = (email, _, _) =>
        {
            if (email == "first@example.com")
                throw new Exception("Serializd 500");
            return Task.FromResult(goodService);
        };

        var result = await _controller.PostReview(new SerializdController.ReviewRequest { TmdbId = 1396, Rating = 8 });

        Assert.IsType<OkObjectResult>(result);
        await goodService.Received(1).CreateShowReviewAsync(1396, 8, null, false);
    }

    // ----- GetAccounts / PutAccounts -----

    [Fact]
    public void GetAccounts_NoUserClaim_ReturnsBadRequest()
    {
        _controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        var result = _controller.GetAccounts();

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void GetAccounts_ReturnsOnlyCallingUsersAccounts_PrimaryFirst()
    {
        var (_, idHex) = AddUserWithAccount(email: "secondary@example.com");
        Plugin.Instance!.Configuration.SerializdAccounts.Add(new SerializdAccount
        {
            UserJellyfinId = idHex,
            Email = "primary@example.com",
            Password = "pw",
            Enabled = true,
            IsPrimary = true,
        });
        Plugin.Instance!.Configuration.SerializdAccounts.Add(new SerializdAccount
        {
            UserJellyfinId = "someone-else",
            Email = "not-mine@example.com",
            Password = "pw",
            Enabled = true,
        });
        Authenticate(idHex);

        var result = _controller.GetAccounts();

        var ok = Assert.IsType<OkObjectResult>(result);
        var accounts = Prop<System.Collections.IEnumerable>(ok, "accounts")!.Cast<object>().ToList();
        Assert.Equal(2, accounts.Count);
        var emails = accounts.Select(a => a.GetType().GetProperty("email")!.GetValue(a)?.ToString()).ToList();
        Assert.Equal("primary@example.com", emails[0]);
        Assert.DoesNotContain("not-mine@example.com", emails);
    }

    [Fact]
    public void PutAccounts_NoUserClaim_ReturnsBadRequest()
    {
        _controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        var result = _controller.PutAccounts(new SerializdController.AccountsUpdateRequest());

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void PutAccounts_NullAccountsList_ReturnsBadRequest()
    {
        var (_, idHex) = AddUserWithAccount();
        Authenticate(idHex);

        var result = _controller.PutAccounts(new SerializdController.AccountsUpdateRequest { Accounts = null });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void PutAccounts_MissingEmail_ReturnsBadRequestNamingTheRow()
    {
        var (_, idHex) = AddUserWithAccount();
        Authenticate(idHex);

        var result = _controller.PutAccounts(new SerializdController.AccountsUpdateRequest
        {
            Accounts = new()
            {
                new SerializdController.AccountItem { Email = "ok@example.com" },
                new SerializdController.AccountItem { Email = "   " },
            }
        });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("#2", Prop<string>(result, "error") ?? string.Empty);
    }

    [Fact]
    public void PutAccounts_ReplacesCallingUsersAccounts_PreservesOthers()
    {
        var (_, idHex) = AddUserWithAccount(email: "old@example.com");
        Plugin.Instance!.Configuration.SerializdAccounts.Add(new SerializdAccount
        {
            UserJellyfinId = "someone-else",
            Email = "untouched@example.com",
            Password = "pw",
            Enabled = true,
        });
        Authenticate(idHex);

        var result = _controller.PutAccounts(new SerializdController.AccountsUpdateRequest
        {
            Accounts = new()
            {
                new SerializdController.AccountItem { Email = " new@example.com ", Enabled = true, DateFilterDays = 3 },
            }
        });

        Assert.IsType<OkObjectResult>(result);
        var mine = Plugin.Instance!.Configuration.SerializdAccounts.Where(a => a.UserJellyfinId == idHex).ToList();
        Assert.Single(mine);
        Assert.Equal("new@example.com", mine[0].Email); // trimmed
        Assert.Equal(3, mine[0].DateFilterDays);

        var other = Plugin.Instance!.Configuration.SerializdAccounts.Single(a => a.UserJellyfinId == "someone-else");
        Assert.Equal("untouched@example.com", other.Email);
    }

    // A household can share one Serializd account across Jellyfin users; the token cache is keyed
    // by email + password hash, so this stays allowed.
    [Fact]
    public void PutAccounts_EmailAlsoLinkedByOtherUser_Saves()
    {
        var (_, idHex) = AddUserWithAccount(email: "mine@example.com");
        Plugin.Instance!.Configuration.SerializdAccounts.Add(new SerializdAccount
        {
            UserJellyfinId = "someone-else",
            Email = "household@example.com",
            Password = "pw",
            Enabled = true,
        });
        Authenticate(idHex);

        var result = _controller.PutAccounts(new SerializdController.AccountsUpdateRequest
        {
            Accounts = new() { new SerializdController.AccountItem { Email = "household@example.com", Password = "pw", Enabled = true } }
        });

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("household@example.com", Plugin.Instance!.Configuration.SerializdAccounts.Single(a => a.UserJellyfinId == idHex).Email);
        Assert.Contains(Plugin.Instance!.Configuration.SerializdAccounts, a => a.UserJellyfinId == "someone-else");
    }

    [Fact]
    public void PutAccounts_SameUserResavesOwnEmail_Succeeds()
    {
        var (_, idHex) = AddUserWithAccount(email: "mine@example.com");
        Authenticate(idHex);

        var result = _controller.PutAccounts(new SerializdController.AccountsUpdateRequest
        {
            Accounts = new() { new SerializdController.AccountItem { Email = "mine@example.com", Password = "new", Enabled = true } }
        });

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("new", Plugin.Instance!.Configuration.SerializdAccounts.Single(a => a.UserJellyfinId == idHex).Password);
    }

    // ----- GetStats / GetHistory -----

    [Fact]
    public void GetStats_UnresolvedUser_ReturnsBadRequest()
    {
        AddUserWithAccount();
        Authenticate("ffffffffffffffffffffffffffffffff");

        var result = _controller.GetStats();

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void GetHistory_UnresolvedUser_ReturnsBadRequest()
    {
        var (user, _) = AddUserWithAccount();
        SerializdActivity.Record(new SyncEvent
        {
            FilmTitle = "Silo · S1E1",
            TmdbId = 1,
            Username = user.Username!,
            Timestamp = DateTime.UtcNow,
            Status = SyncStatus.Success,
            Source = "playback",
        });
        _controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        var result = _controller.GetHistory();

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void GetStats_ReturnsAggregateShape()
    {
        var (user, idHex) = AddUserWithAccount();
        Authenticate(idHex);
        SerializdActivity.Record(new SyncEvent
        {
            FilmTitle = "Silo · S1E1",
            TmdbId = 1,
            Username = user.Username!,
            Timestamp = DateTime.UtcNow,
            Status = SyncStatus.Success,
            Source = "playback",
        });

        var result = _controller.GetStats();

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, Prop<int>(ok, "total"));
        Assert.Equal(1, Prop<int>(ok, "success"));
    }

    [Fact]
    public void GetHistory_DefaultPaging_ReturnsRecordedEvents()
    {
        var (user, idHex) = AddUserWithAccount();
        Authenticate(idHex);
        for (var i = 0; i < 3; i++)
        {
            SerializdActivity.Record(new SyncEvent
            {
                FilmTitle = $"Silo · S1E{i + 1}",
                TmdbId = 1,
                Username = user.Username!,
                Timestamp = DateTime.UtcNow,
                Status = SyncStatus.Success,
                Source = "playback",
            });
        }

        var result = _controller.GetHistory();

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(3, Prop<int>(ok, "total"));
        var events = Prop<System.Collections.IEnumerable>(ok, "events")!.Cast<object>().ToList();
        Assert.Equal(3, events.Count);
    }

    [Fact]
    public void GetHistory_CountClampedToValidRange()
    {
        var (_, idHex) = AddUserWithAccount();
        Authenticate(idHex);

        // Out-of-range count must not throw; the endpoint clamps it internally.
        var result = _controller.GetHistory(count: 99999, offset: -5);

        Assert.IsType<OkObjectResult>(result);
    }

    // ----- SyncNow -----

    [Fact]
    public void SyncNow_GateAlreadyRunning_ReturnsConflict()
    {
        var (_, idHex) = AddUserWithAccount();
        Authenticate(idHex);
        SerializdSyncGate.Instance.Wait();
        try
        {
            var result = _controller.SyncNow();
            Assert.IsType<ConflictObjectResult>(result);
        }
        finally
        {
            SerializdSyncGate.Instance.Release();
        }
    }

    [Fact]
    public void SyncNow_NoEnabledAccounts_ReturnsBadRequest()
    {
        var user = new User("lachlan", "test-provider-id", "test-reset-id");
        var idHex = user.Id.ToString("N");
        _userManager.GetUsers().Returns(new[] { user });
        Authenticate(idHex);

        var result = _controller.SyncNow();

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task SyncNow_Success_ReturnsAcceptedAndRunsInBackground()
    {
        var (_, idHex) = AddUserWithAccount();
        Authenticate(idHex);

        var result = _controller.SyncNow();

        Assert.IsType<AcceptedResult>(result);
        Assert.NotNull(_controller.LastBackgroundSync);
        await _controller.LastBackgroundSync!; // no exception = the background task completed cleanly
    }

    // ----- SyncWatchlistNow -----

    [Fact]
    public void SyncWatchlistNow_NoUserClaim_ReturnsBadRequest()
    {
        _controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        var result = _controller.SyncWatchlistNow();

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void SyncWatchlistNow_WatchlistRunInProgress_ReturnsConflict()
    {
        var (_, idHex) = AddUserWithAccount();
        Plugin.Instance!.Configuration.SerializdAccounts.Single(a => a.UserJellyfinId == idHex).SyncWatchlist = true;
        Authenticate(idHex);
        SerializdWatchlistSyncGate.Instance.Wait();
        try
        {
            var result = _controller.SyncWatchlistNow();

            Assert.IsType<ConflictObjectResult>(result);
            Assert.Null(_controller.LastBackgroundSync);
        }
        finally
        {
            SerializdWatchlistSyncGate.Instance.Release();
        }
    }

    [Fact]
    public void SyncWatchlistNow_NoAccountHasWatchlistSyncEnabled_ReturnsBadRequest()
    {
        // Enabled account exists, but SyncWatchlist is off (the default).
        var (_, idHex) = AddUserWithAccount();
        Authenticate(idHex);

        var result = _controller.SyncWatchlistNow();

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task SyncWatchlistNow_Success_ReturnsAcceptedAndRunsInBackground()
    {
        var (_, idHex) = AddUserWithAccount();
        Plugin.Instance!.Configuration.SerializdAccounts.Single(a => a.UserJellyfinId == idHex).SyncWatchlist = true;
        Authenticate(idHex);

        var result = _controller.SyncWatchlistNow();

        Assert.IsType<AcceptedResult>(result);
        Assert.NotNull(_controller.LastBackgroundSync);
        await _controller.LastBackgroundSync!;
    }

    // ----- Excluded libraries (issue #124) -----

    [Fact]
    public void PutAccounts_StoresExcludedLibraryIds_AndGetAccountsEchoesThem()
    {
        var (_, idHex) = AddUserWithAccount();
        Authenticate(idHex);
        const string anime = "0c5b2a1e9f3d4c7a8b6e5d4c3b2a1f0e";

        var result = _controller.PutAccounts(new SerializdController.AccountsUpdateRequest
        {
            Accounts = new()
            {
                new SerializdController.AccountItem
                {
                    Email = "user@example.com",
                    Enabled = true,
                    ExcludedLibraryIds = new() { anime, "0c5b2a1e-9f3d-4c7a-8b6e-5d4c3b2a1f0e", "junk" },
                },
            }
        });

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(new[] { anime },
            Plugin.Instance!.Configuration.SerializdAccounts.Single(a => a.UserJellyfinId == idHex).ExcludedLibraryIds);

        var get = _controller.GetAccounts();
        var echoed = Prop<System.Collections.IEnumerable>(get, "accounts")!.Cast<object>().Single();
        Assert.Equal(new[] { anime },
            (IEnumerable<string>)echoed.GetType().GetProperty("excludedLibraryIds")!.GetValue(echoed)!);
    }

    [Fact]
    public void PutAccounts_OmittedExcludedLibraryIds_KeepsStoredList()
    {
        var (_, idHex) = AddUserWithAccount(email: "User@Example.com");
        const string anime = "0c5b2a1e9f3d4c7a8b6e5d4c3b2a1f0e";
        Plugin.Instance!.Configuration.SerializdAccounts.Single(a => a.UserJellyfinId == idHex).ExcludedLibraryIds.Add(anime);
        Authenticate(idHex);

        _controller.PutAccounts(new SerializdController.AccountsUpdateRequest
        {
            Accounts = new() { new SerializdController.AccountItem { Email = "user@example.com", Enabled = true } }
        });

        Assert.Equal(new[] { anime },
            Plugin.Instance!.Configuration.SerializdAccounts.Single(a => a.UserJellyfinId == idHex).ExcludedLibraryIds);
    }

    // ----- Watchlist name is admin-only -----

    [Fact]
    public void PutAccounts_NonAdmin_CannotNameTheCollection_KeepsTheStoredName()
    {
        var (_, idHex) = AddUserWithAccount(email: "me@example.com");
        Plugin.Instance!.Configuration.SerializdAccounts[0].WatchlistName = "Named By Admin";
        Authenticate(idHex);

        var result = _controller.PutAccounts(new SerializdController.AccountsUpdateRequest
        {
            Accounts = new()
            {
                new SerializdController.AccountItem { Email = "me@example.com", Enabled = true, WatchlistName = "Staff Picks" },
                new SerializdController.AccountItem { Email = "new@example.com", Enabled = true, WatchlistName = "Staff Picks" },
            }
        });

        Assert.IsType<OkObjectResult>(result);
        var saved = Plugin.Instance!.Configuration.SerializdAccounts.Where(a => a.UserJellyfinId == idHex).ToList();
        Assert.Equal("Named By Admin", saved.Single(a => a.Email == "me@example.com").WatchlistName);
        Assert.Null(saved.Single(a => a.Email == "new@example.com").WatchlistName);
    }

    [Fact]
    public void PutAccounts_Admin_CanNameTheCollection()
    {
        var (_, idHex) = AddUserWithAccount(email: "me@example.com");
        Authenticate(idHex, admin: true);

        _controller.PutAccounts(new SerializdController.AccountsUpdateRequest
        {
            Accounts = new() { new SerializdController.AccountItem { Email = "me@example.com", WatchlistName = " Our Shows " } }
        });

        Assert.Equal("Our Shows", Plugin.Instance!.Configuration.SerializdAccounts.Single(a => a.UserJellyfinId == idHex).WatchlistName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetAccounts_TellsThePageWhetherTheCallerMayNameTheCollection(bool admin)
    {
        var (_, idHex) = AddUserWithAccount();
        Authenticate(idHex, admin);

        Assert.Equal(admin, Prop<bool>(_controller.GetAccounts(), "canSetWatchlistName"));
    }
}
