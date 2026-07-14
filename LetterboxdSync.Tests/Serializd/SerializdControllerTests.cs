using System;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using LetterboxdSync.Api;
using LetterboxdSync.Configuration;
using LetterboxdSync.Serializd;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
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

        _controller = new SerializdController(new NullLogger<SerializdController>(), runner, watchlistRunner, _userManager);
    }

    public void Dispose()
    {
        SerializdController.VerifyOverrideForTesting = null;
        SerializdServiceFactory.OverrideForTesting = null;
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

    /// <summary>Points the controller's ControllerContext at an authenticated Jellyfin-UserId claim.</summary>
    private void Authenticate(string userIdHex)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", userIdHex) }, "Test"));
        _controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } };
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
        SerializdController.VerifyOverrideForTesting = (_, _, _) => Task.FromResult<string?>("8bitproxy");

        var result = await _controller.Verify(
            new SerializdController.VerifyRequest { Email = "me@example.com", Password = "pw" });

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Contains("8bitproxy", ok.Value!.ToString());
    }

    [Fact]
    public async Task Verify_BadLogin_ReturnsBadRequest()
    {
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
        SerializdServiceFactory.OverrideForTesting = (_, _, _) => Task.FromResult(service);

        var result = await _controller.PostReview(new SerializdController.ReviewRequest
        {
            TmdbId = 1396,
            Rating = 7,
            SeasonNumber = 2,
            EpisodeNumber = 5,
        });

        Assert.IsType<OkObjectResult>(result);
        await service.Received(1).CreateEpisodeReviewAsync(1396, 2, 5, 7, null, false);
        await service.DidNotReceive().CreateShowReviewAsync(
            Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<bool>());
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
}
