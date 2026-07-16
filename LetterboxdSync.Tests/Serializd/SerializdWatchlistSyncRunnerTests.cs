using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using LetterboxdSync.Configuration;
using LetterboxdSync.Serializd;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace LetterboxdSync.Tests.Serializd;

/// <summary>
/// End-to-end coverage of SerializdWatchlistSyncRunner.SeerrIntegrationAsync (auto-request +
/// watchlist mirror), driven through a real SeerrClient bound to a mock HttpMessageHandler via
/// SeerrClientFactoryOverride. Ports the pattern WatchlistSyncRunnerTests already uses for the
/// Letterboxd/film side; this is the TV counterpart, which previously had none.
/// </summary>
[Collection("Plugin")]
public class SerializdWatchlistSyncRunnerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionManager _collectionManager;
    private readonly SerializdWatchlistSyncRunner _runner;

    public SerializdWatchlistSyncRunnerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-szwl-" + Guid.NewGuid().ToString("N"));
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

        // Isolate SyncHistory's JSONL file to this test's temp dir so Seerr-auto-request
        // recording assertions don't read/write the real on-disk history.
        SyncHistory.DataPathOverride = Path.Combine(_tempDir, "sync-history.jsonl");
        SyncHistory.ResetForTesting();
        SyncHistory.SetLogger(NullLogger.Instance);

        _userManager = Substitute.For<IUserManager>();
        _libraryManager = Substitute.For<ILibraryManager>();
        _collectionManager = Substitute.For<ICollectionManager>();
        _runner = new SerializdWatchlistSyncRunner(NullLoggerFactory.Instance, _libraryManager, _userManager,
            _collectionManager, Substitute.For<IPlaylistManager>());
    }

    public void Dispose()
    {
        SerializdServiceFactory.OverrideForTesting = null;
        SerializdWatchlistSyncRunner.SeerrClientFactoryOverride = null;
        Plugin.Instance!.Configuration.JellyseerrUrl = null;
        Plugin.Instance!.Configuration.JellyseerrApiKey = null;
        SyncHistory.DataPathOverride = null;
        SyncHistory.ResetForTesting();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    private static (User User, string IdHex) MakeUser(string name)
    {
        var u = new User(name, "test-provider-id", "test-reset-id");
        return (u, u.Id.ToString("N"));
    }

    private void AddAccount(string userId, string email = "sz-user@example.com",
        bool autoRequest = false, bool mirror = false, bool isPrimary = false)
    {
        Plugin.Instance!.Configuration.SerializdAccounts.Add(new SerializdAccount
        {
            UserJellyfinId = userId,
            Email = email,
            Password = "secret",
            Enabled = true,
            SyncWatchlist = true,
            AutoRequestWatchlist = autoRequest,
            MirrorJellyseerrWatchlist = mirror,
            IsPrimary = isPrimary,
        });
    }

    /// <summary>Mock HttpMessageHandler that lets us drive SeerrClient end-to-end without hitting the network.</summary>
    private class JellyseerrHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public List<HttpRequestMessage> Calls { get; } = new();
        public JellyseerrHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add(request);
            return Task.FromResult(_responder(request));
        }
    }

    private static HttpResponseMessage UserMapResponse(string jellyfinIdHex, int seerrId = 7)
        => new(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{\"results\":[{\"id\":" + seerrId + ",\"jellyfinUserId\":\"" + jellyfinIdHex + "\"}]}"),
        };

    // ----- Seerr auto-request -----

    [Fact]
    public async Task TryRunForUserAsync_AutoRequest_RequestsUnmatchedShow()
    {
        var (user, userId) = MakeUser("lachlan");
        _userManager.GetUsers().Returns(new[] { user });
        AddAccount(userId, autoRequest: true);

        Plugin.Instance!.Configuration.JellyseerrUrl = "http://jellyseerr.test";
        Plugin.Instance!.Configuration.JellyseerrApiKey = "key";

        var service = Substitute.For<ISerializdService>();
        service.GetWatchlistAsync().Returns(new List<SerializdWatchlistEntry> { new(1396, Array.Empty<int>()) });
        SerializdServiceFactory.OverrideForTesting = (_, _, _) => Task.FromResult(service);

        // Library has no matching series → the show is unmatched → eligible for auto-request.
        _libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

        string? requestBody = null;
        var handler = new JellyseerrHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Get && path.EndsWith("/api/v1/user"))
                return UserMapResponse(user.Id.ToString("N"));
            if (req.Method == HttpMethod.Post && path.EndsWith("/api/v1/request"))
            {
                requestBody = req.Content!.ReadAsStringAsync().Result;
                return new HttpResponseMessage(System.Net.HttpStatusCode.Created) { Content = new StringContent("{}") };
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        });
        SerializdWatchlistSyncRunner.SeerrClientFactoryOverride = (url, key, log) => new SeerrClient(url, key, log, handler);

        var ok = await _runner.TryRunForUserAsync(userId, CancellationToken.None);

        Assert.True(ok);
        Assert.Contains(handler.Calls, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/api/v1/request"));
        Assert.NotNull(requestBody);
        Assert.Contains("\"mediaType\":\"tv\"", requestBody);
        Assert.Contains("\"mediaId\":1396", requestBody);
    }

    [Fact]
    public async Task TryRunForUserAsync_AutoRequest_Requested_RecordsSyncEventWithTitle()
    {
        var (user, userId) = MakeUser("lachlan");
        _userManager.GetUsers().Returns(new[] { user });
        AddAccount(userId, autoRequest: true);

        Plugin.Instance!.Configuration.JellyseerrUrl = "http://jellyseerr.test";
        Plugin.Instance!.Configuration.JellyseerrApiKey = "key";

        var service = Substitute.For<ISerializdService>();
        service.GetWatchlistAsync().Returns(new List<SerializdWatchlistEntry> { new(1396, Array.Empty<int>()) });
        SerializdServiceFactory.OverrideForTesting = (_, _, _) => Task.FromResult(service);
        _libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

        var handler = new JellyseerrHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Get && path.EndsWith("/api/v1/user"))
                return UserMapResponse(user.Id.ToString("N"));
            if (req.Method == HttpMethod.Get && path.EndsWith("/api/v1/tv/1396"))
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"name\":\"Breaking Bad\"}"),
                };
            if (req.Method == HttpMethod.Post && path.EndsWith("/api/v1/request"))
                return new HttpResponseMessage(System.Net.HttpStatusCode.Created) { Content = new StringContent("{}") };
            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        });
        SerializdWatchlistSyncRunner.SeerrClientFactoryOverride = (url, key, log) => new SeerrClient(url, key, log, handler);

        await _runner.TryRunForUserAsync(userId, CancellationToken.None);

        var (events, total) = SyncHistory.GetPage(0, 10, user.Username);
        Assert.Equal(1, total);
        var evt = Assert.Single(events);
        Assert.Equal(SyncStatus.Requested, evt.Status);
        Assert.Equal(SyncEventSources.SeerrAutoRequestTv, evt.Source);
        Assert.Equal(1396, evt.TmdbId);
        Assert.Equal("Breaking Bad", evt.FilmTitle);
    }

    [Fact]
    public async Task TryRunForUserAsync_AutoRequest_Failed_RecordsSyncEventWithError()
    {
        var (user, userId) = MakeUser("lachlan");
        _userManager.GetUsers().Returns(new[] { user });
        AddAccount(userId, autoRequest: true);

        Plugin.Instance!.Configuration.JellyseerrUrl = "http://jellyseerr.test";
        Plugin.Instance!.Configuration.JellyseerrApiKey = "key";

        var service = Substitute.For<ISerializdService>();
        service.GetWatchlistAsync().Returns(new List<SerializdWatchlistEntry> { new(1396, Array.Empty<int>()) });
        SerializdServiceFactory.OverrideForTesting = (_, _, _) => Task.FromResult(service);
        _libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

        var handler = new JellyseerrHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Get && path.EndsWith("/api/v1/user"))
                return UserMapResponse(user.Id.ToString("N"));
            if (req.Method == HttpMethod.Get && path.EndsWith("/api/v1/tv/1396"))
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"name\":\"Breaking Bad\"}"),
                };
            if (req.Method == HttpMethod.Post && path.EndsWith("/api/v1/request"))
                return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("{\"message\":\"boom\"}"),
                };
            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        });
        SerializdWatchlistSyncRunner.SeerrClientFactoryOverride = (url, key, log) => new SeerrClient(url, key, log, handler);

        await _runner.TryRunForUserAsync(userId, CancellationToken.None);

        var (events, total) = SyncHistory.GetPage(0, 10, user.Username);
        Assert.Equal(1, total);
        var evt = Assert.Single(events);
        Assert.Equal(SyncStatus.Failed, evt.Status);
        Assert.Equal(SyncEventSources.SeerrAutoRequestTv, evt.Source);
        Assert.Equal("Breaking Bad", evt.FilmTitle);
        Assert.False(string.IsNullOrEmpty(evt.Error));
    }

    [Fact]
    public async Task TryRunForUserAsync_AutoRequest_AlreadyExists_RecordsNoSyncEvent()
    {
        var (user, userId) = MakeUser("lachlan");
        _userManager.GetUsers().Returns(new[] { user });
        AddAccount(userId, autoRequest: true);

        Plugin.Instance!.Configuration.JellyseerrUrl = "http://jellyseerr.test";
        Plugin.Instance!.Configuration.JellyseerrApiKey = "key";

        var service = Substitute.For<ISerializdService>();
        service.GetWatchlistAsync().Returns(new List<SerializdWatchlistEntry> { new(1396, Array.Empty<int>()) });
        SerializdServiceFactory.OverrideForTesting = (_, _, _) => Task.FromResult(service);
        _libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

        var handler = new JellyseerrHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Get && path.EndsWith("/api/v1/user"))
                return UserMapResponse(user.Id.ToString("N"));
            if (req.Method == HttpMethod.Get && path.EndsWith("/api/v1/tv/1396"))
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"name\":\"Breaking Bad\"}"),
                };
            // All requested seasons already AVAILABLE (status 5) → classified as a no-op.
            if (req.Method == HttpMethod.Post && path.EndsWith("/api/v1/request"))
                return new HttpResponseMessage(System.Net.HttpStatusCode.Created)
                {
                    Content = new StringContent("{\"media\":{\"status\":5}}"),
                };
            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        });
        SerializdWatchlistSyncRunner.SeerrClientFactoryOverride = (url, key, log) => new SeerrClient(url, key, log, handler);

        await _runner.TryRunForUserAsync(userId, CancellationToken.None);

        var (_, total) = SyncHistory.GetPage(0, 10, user.Username);
        Assert.Equal(0, total);
    }

    [Fact]
    public async Task TryRunForUserAsync_JellyseerrUserMapErrors_SkipsAutoRequestGracefully()
    {
        // The Seerr user-map fetch fails (500). The runner must log and bail out of
        // the Seerr branch without throwing or requesting anything.
        var (user, userId) = MakeUser("lachlan");
        _userManager.GetUsers().Returns(new[] { user });
        AddAccount(userId, autoRequest: true);

        Plugin.Instance!.Configuration.JellyseerrUrl = "http://jellyseerr.test";
        Plugin.Instance!.Configuration.JellyseerrApiKey = "key";

        var service = Substitute.For<ISerializdService>();
        service.GetWatchlistAsync().Returns(new List<SerializdWatchlistEntry> { new(1396, Array.Empty<int>()) });
        SerializdServiceFactory.OverrideForTesting = (_, _, _) => Task.FromResult(service);
        _libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

        var handler = new JellyseerrHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        SerializdWatchlistSyncRunner.SeerrClientFactoryOverride = (url, key, log) => new SeerrClient(url, key, log, handler);

        var ok = await _runner.TryRunForUserAsync(userId, CancellationToken.None);

        Assert.True(ok);
        Assert.DoesNotContain(handler.Calls, r => r.Method == HttpMethod.Post);
    }

    // ----- Mirror to Seerr watchlist -----

    [Fact]
    public async Task TryRunForUserAsync_MirrorWatchlist_AddsMissingAndRemovesStale()
    {
        var (user, userId) = MakeUser("lachlan");
        _userManager.GetUsers().Returns(new[] { user });
        AddAccount(userId, mirror: true, isPrimary: true);

        Plugin.Instance!.Configuration.JellyseerrUrl = "http://jellyseerr.test";
        Plugin.Instance!.Configuration.JellyseerrApiKey = "key";

        var service = Substitute.For<ISerializdService>();
        // Serializd watchlist has only 1396; Seerr's TV watchlist has stale entry 999.
        service.GetWatchlistAsync().Returns(new List<SerializdWatchlistEntry> { new(1396, Array.Empty<int>()) });
        SerializdServiceFactory.OverrideForTesting = (_, _, _) => Task.FromResult(service);
        _libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

        var handler = new JellyseerrHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Get && path.EndsWith("/api/v1/user"))
                return UserMapResponse(user.Id.ToString("N"));
            if (req.Method == HttpMethod.Get && path.Contains("/api/v1/user/7/watchlist"))
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"results\":[{\"tmdbId\":999,\"mediaType\":\"tv\"}]}"),
                };
            if (req.Method == HttpMethod.Post && path.EndsWith("/api/v1/watchlist"))
                return new HttpResponseMessage(System.Net.HttpStatusCode.Created);
            if (req.Method == HttpMethod.Delete && path.StartsWith("/api/v1/watchlist/"))
                return new HttpResponseMessage(System.Net.HttpStatusCode.NoContent);
            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        });
        SerializdWatchlistSyncRunner.SeerrClientFactoryOverride = (url, key, log) => new SeerrClient(url, key, log, handler);

        var ok = await _runner.TryRunForUserAsync(userId, CancellationToken.None);

        Assert.True(ok);
        Assert.Contains(handler.Calls, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/api/v1/watchlist"));
        Assert.Contains(handler.Calls, r => r.Method == HttpMethod.Delete && r.RequestUri!.AbsolutePath.Contains("999"));
    }

    [Fact]
    public async Task TryRunForUserAsync_MirrorWatchlist_EmptySerializdWatchlist_SkipsToAvoidMassDeletion()
    {
        // Defensive: an empty Serializd watchlist fetch (deleted, or a failed API call) must
        // never be allowed to wipe an existing Seerr TV watchlist.
        var (user, userId) = MakeUser("lachlan");
        _userManager.GetUsers().Returns(new[] { user });
        AddAccount(userId, mirror: true, isPrimary: true);

        Plugin.Instance!.Configuration.JellyseerrUrl = "http://jellyseerr.test";
        Plugin.Instance!.Configuration.JellyseerrApiKey = "key";

        var service = Substitute.For<ISerializdService>();
        service.GetWatchlistAsync().Returns(new List<SerializdWatchlistEntry>());
        SerializdServiceFactory.OverrideForTesting = (_, _, _) => Task.FromResult(service);
        _libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

        var handler = new JellyseerrHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Get && path.EndsWith("/api/v1/user"))
                return UserMapResponse(user.Id.ToString("N"));
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"results\":[]}") };
        });
        SerializdWatchlistSyncRunner.SeerrClientFactoryOverride = (url, key, log) => new SeerrClient(url, key, log, handler);

        var ok = await _runner.TryRunForUserAsync(userId, CancellationToken.None);

        Assert.True(ok);
        // The empty-watchlist guard trips before ever fetching/adding/removing on Seerr's side.
        Assert.DoesNotContain(handler.Calls, r => r.RequestUri!.AbsolutePath.Contains("/watchlist"));
    }

    [Fact]
    public async Task TryRunForUserAsync_MirrorWatchlist_SecondaryAccount_SkipsMirror()
    {
        // The Seerr TV watchlist mirror destination is keyed by Jellyfin user, not by Serializd
        // account, so only the primary account may own it (otherwise two accounts on one user
        // would each wipe the other's diff).
        var (user, userId) = MakeUser("lachlan");
        _userManager.GetUsers().Returns(new[] { user });
        AddAccount(userId, email: "primary@example.com", mirror: true, isPrimary: true);
        AddAccount(userId, email: "secondary@example.com", mirror: true, isPrimary: false);

        Plugin.Instance!.Configuration.JellyseerrUrl = "http://jellyseerr.test";
        Plugin.Instance!.Configuration.JellyseerrApiKey = "key";

        var service = Substitute.For<ISerializdService>();
        service.GetWatchlistAsync().Returns(new List<SerializdWatchlistEntry> { new(1396, Array.Empty<int>()) });
        SerializdServiceFactory.OverrideForTesting = (_, _, _) => Task.FromResult(service);
        _libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

        var handler = new JellyseerrHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Get && path.EndsWith("/api/v1/user"))
                return UserMapResponse(user.Id.ToString("N"));
            if (req.Method == HttpMethod.Get && path.Contains("/watchlist"))
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"results\":[]}") };
            if (req.Method == HttpMethod.Post && path.EndsWith("/api/v1/watchlist"))
                return new HttpResponseMessage(System.Net.HttpStatusCode.Created);
            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        });
        SerializdWatchlistSyncRunner.SeerrClientFactoryOverride = (url, key, log) => new SeerrClient(url, key, log, handler);

        var ok = await _runner.TryRunForUserAsync(userId, CancellationToken.None);

        Assert.True(ok);
        // Exactly one add, from the primary account; the secondary must skip its mirror entirely.
        Assert.Single(handler.Calls, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/api/v1/watchlist"));
    }

    // ----- Collection mirror: empty-watchlist guard -----

    [Fact]
    public async Task TryRunForUserAsync_EmptySerializdWatchlist_DoesNotWipeExistingCollection()
    {
        // Same defensive guard as the playlist path: a transient Serializd fetch failure must
        // never be allowed to empty an existing "Serializd Watchlist" collection.
        var (user, userId) = MakeUser("lachlan");
        _userManager.GetUsers().Returns(new[] { user });
        AddAccount(userId); // SyncWatchlist=true; mirror/autoRequest both false, Seerr not needed.

        var existingMember = Guid.NewGuid();
        var boxSet = new BoxSet { Id = Guid.NewGuid(), Name = "Serializd Watchlist" };
        boxSet.LinkedChildren = new[] { new LinkedChild { ItemId = existingMember } };

        _libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(callInfo =>
        {
            var query = callInfo.Arg<InternalItemsQuery>();
            if (query.IncludeItemTypes != null && query.IncludeItemTypes.Contains(BaseItemKind.BoxSet))
                return new List<BaseItem> { boxSet };
            return new List<BaseItem>();
        });

        var service = Substitute.For<ISerializdService>();
        service.GetWatchlistAsync().Returns(new List<SerializdWatchlistEntry>());
        SerializdServiceFactory.OverrideForTesting = (_, _, _) => Task.FromResult(service);

        var ok = await _runner.TryRunForUserAsync(userId, CancellationToken.None);

        Assert.True(ok);
        await _collectionManager.DidNotReceive().RemoveFromCollectionAsync(Arg.Any<Guid>(), Arg.Any<IEnumerable<Guid>>());
    }
}
