using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace LetterboxdSync.Tests.Serializd;

/// <summary>
/// Which collection each account's Serializd watchlist sync writes to. Collections are
/// server-wide, so two users must never share one, and a name an account is configured with
/// must never let it take over a collection someone else curates.
/// </summary>
[Collection("Plugin")]
public class SerializdWatchlistCollectionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IUserManager _userManager = Substitute.For<IUserManager>();
    private readonly ILibraryManager _libraryManager = Substitute.For<ILibraryManager>();
    private readonly FakeCollections _collections;
    private readonly SerializdWatchlistSyncRunner _runner;
    private readonly List<Series> _series = new();
    private readonly Dictionary<string, List<int>> _watchlistByEmail = new(StringComparer.Ordinal);

    public SerializdWatchlistCollectionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-szcol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var paths = Substitute.For<IApplicationPaths>();
        paths.PluginConfigurationsPath.Returns(_tempDir);
        paths.LogDirectoryPath.Returns(_tempDir);
        paths.DataPath.Returns(_tempDir);
        paths.CachePath.Returns(_tempDir);
        var xml = Substitute.For<IXmlSerializer>();
        xml.DeserializeFromFile(typeof(PluginConfiguration), Arg.Any<string>()).Returns(_ => new PluginConfiguration());
        new Plugin(paths, xml);

        _collections = new FakeCollections();
        _libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(ci =>
        {
            var kinds = ci.Arg<InternalItemsQuery>().IncludeItemTypes ?? Array.Empty<BaseItemKind>();
            if (kinds.Contains(BaseItemKind.BoxSet)) return _collections.All.Cast<BaseItem>().ToList();
            if (kinds.Contains(BaseItemKind.Series)) return _series.Cast<BaseItem>().ToList();
            return new List<BaseItem>();
        });
        _libraryManager.GetItemById(Arg.Any<Guid>()).Returns(ci => _collections.Find(ci.Arg<Guid>()));

        SerializdServiceFactory.OverrideForTesting = (email, _, _) =>
        {
            var service = Substitute.For<ISerializdService>();
            var ids = _watchlistByEmail.TryGetValue(email, out var list) ? list : new List<int>();
            service.GetWatchlistAsync().Returns(ids.Select(id => new SerializdWatchlistEntry(id, Array.Empty<int>())).ToList());
            return Task.FromResult(service);
        };

        _runner = new SerializdWatchlistSyncRunner(NullLoggerFactory.Instance, _libraryManager, _userManager,
            _collections.Manager, Substitute.For<IPlaylistManager>());
    }

    public void Dispose()
    {
        SerializdServiceFactory.OverrideForTesting = null;
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    /// <summary>
    /// Models Jellyfin's CollectionManager: a collection lives in a folder named after it and its
    /// item id comes from that path, so creating a second collection with an existing name lands
    /// in the existing one (Emby.Server.Implementations/Collections/CollectionManager.cs).
    /// </summary>
    private sealed class FakeCollections
    {
        private readonly Dictionary<Guid, BoxSet> _byId = new();
        public ICollectionManager Manager { get; } = Substitute.For<ICollectionManager>();
        public List<(Guid Collection, Guid Item)> Removed { get; } = new();
        public IEnumerable<BoxSet> All => _byId.Values;

        public FakeCollections()
        {
            Manager.CreateCollectionAsync(Arg.Any<CollectionCreationOptions>()).Returns(ci =>
            {
                var o = ci.Arg<CollectionCreationOptions>();
                var box = Add(o.Name);
                AddItems(box, o.ItemIdList.Select(Guid.Parse));
                return Task.FromResult(box);
            });
            Manager.AddToCollectionAsync(Arg.Any<Guid>(), Arg.Any<IEnumerable<Guid>>()).Returns(ci =>
            {
                AddItems(_byId[ci.ArgAt<Guid>(0)], ci.ArgAt<IEnumerable<Guid>>(1));
                return Task.CompletedTask;
            });
            Manager.RemoveFromCollectionAsync(Arg.Any<Guid>(), Arg.Any<IEnumerable<Guid>>()).Returns(ci =>
            {
                var box = _byId[ci.ArgAt<Guid>(0)];
                var gone = ci.ArgAt<IEnumerable<Guid>>(1).ToHashSet();
                foreach (var g in gone) Removed.Add((box.Id, g));
                box.LinkedChildren = box.LinkedChildren.Where(lc => !gone.Contains(lc.ItemId!.Value)).ToArray();
                return Task.CompletedTask;
            });
        }

        public BoxSet Add(string name, params Guid[] members)
        {
            var id = new Guid(MD5.HashData(Encoding.UTF8.GetBytes(name.ToLowerInvariant() + " [boxset]")));
            if (!_byId.TryGetValue(id, out var box))
            {
                box = new BoxSet { Id = id, Name = name, LinkedChildren = Array.Empty<LinkedChild>() };
                _byId[id] = box;
            }

            AddItems(box, members);
            return box;
        }

        public BoxSet? Find(Guid id) => _byId.TryGetValue(id, out var b) ? b : null;

        public void Delete(Guid id) => _byId.Remove(id);

        public static HashSet<Guid> Members(BoxSet box) => box.LinkedChildren.Select(lc => lc.ItemId!.Value).ToHashSet();

        private static void AddItems(BoxSet box, IEnumerable<Guid> ids)
        {
            var have = Members(box);
            box.LinkedChildren = box.LinkedChildren
                .Concat(ids.Where(have.Add).Select(id => new LinkedChild { ItemId = id }))
                .ToArray();
        }
    }

    private Series AddSeries(int tmdbId)
    {
        var s = new Series { Id = Guid.NewGuid(), Name = "Show " + tmdbId };
        s.SetProviderId(MetadataProvider.Tmdb, tmdbId.ToString());
        _series.Add(s);
        return s;
    }

    private User AddUser(string name, string email, int[] watchlist, string? watchlistName = null)
    {
        var u = new User(name, "test-provider-id", "test-reset-id");
        var users = _userManager.GetUsers().ToList();
        users.Add(u);
        _userManager.GetUsers().Returns(users);
        Plugin.Instance!.Configuration.SerializdAccounts.Add(new SerializdAccount
        {
            UserJellyfinId = u.Id.ToString("N"),
            Email = email,
            Password = "secret",
            Enabled = true,
            SyncWatchlist = true,
            WatchlistName = watchlistName,
        });
        _watchlistByEmail[email] = watchlist.ToList();
        return u;
    }

    private BoxSet CollectionNamed(string name) => Assert.Single(_collections.All, b => b.Name == name);

    [Fact]
    public async Task TwoUsers_GetSeparateCollections_ThatNeverRemoveEachOthersShows()
    {
        var showA = AddSeries(1);
        var showB = AddSeries(2);
        AddUser("alice", "alice@example.com", new[] { 1 });
        AddUser("bob", "bob@example.com", new[] { 2 });

        // Two passes: the second is where a shared collection would have each user's run strip
        // the other's show.
        await _runner.RunForAllAsync(new Progress<double>(), CancellationToken.None);
        await _runner.RunForAllAsync(new Progress<double>(), CancellationToken.None);

        Assert.Equal(2, _collections.All.Count());
        Assert.Equal(new HashSet<Guid> { showA.Id }, FakeCollections.Members(CollectionNamed("Serializd Watchlist (alice)")));
        Assert.Equal(new HashSet<Guid> { showB.Id }, FakeCollections.Members(CollectionNamed("Serializd Watchlist (bob)")));
        Assert.Empty(_collections.Removed);
    }

    [Fact]
    public async Task ConfiguredNameOfACuratedCollection_LeavesThatCollectionUntouched()
    {
        var curatedShow = Guid.NewGuid();
        var curated = _collections.Add("Staff Picks", curatedShow);
        var showA = AddSeries(1);
        AddUser("alice", "alice@example.com", new[] { 1 }, watchlistName: "Staff Picks");

        await _runner.RunForAllAsync(new Progress<double>(), CancellationToken.None);
        await _runner.RunForAllAsync(new Progress<double>(), CancellationToken.None);

        Assert.Equal(new HashSet<Guid> { curatedShow }, FakeCollections.Members(curated));
        Assert.DoesNotContain(_collections.Removed, r => r.Collection == curated.Id);
        Assert.Equal(new HashSet<Guid> { showA.Id }, FakeCollections.Members(CollectionNamed("Staff Picks 2")));
    }

    [Fact]
    public async Task Upgrade_OnlyAccountOnTheOldSharedName_KeepsTheExistingCollection()
    {
        var oldShow = Guid.NewGuid();
        var legacy = _collections.Add("Serializd Watchlist", oldShow);
        var showA = AddSeries(1);
        AddUser("alice", "alice@example.com", new[] { 1 });

        await _runner.RunForAllAsync(new Progress<double>(), CancellationToken.None);

        // Adopted as alice's: reconciled in place, name kept, no second collection.
        Assert.Single(_collections.All);
        Assert.Equal(new HashSet<Guid> { showA.Id }, FakeCollections.Members(legacy));
        Assert.Equal("Serializd Watchlist", legacy.Name);
        Assert.Equal(legacy.Id, SerializdCollectionStore.Get(Plugin.Instance!.Configuration.SerializdAccounts[0].UserJellyfinId, "alice@example.com")!.CollectionId);
    }

    [Fact]
    public async Task Upgrade_SeveralAccountsOnTheOldSharedName_LeaveItAloneAndEachGetTheirOwn()
    {
        var oldShow = Guid.NewGuid();
        var legacy = _collections.Add("Serializd Watchlist", oldShow);
        var showA = AddSeries(1);
        var showB = AddSeries(2);
        AddUser("alice", "alice@example.com", new[] { 1 });
        AddUser("bob", "bob@example.com", new[] { 2 });

        await _runner.RunForAllAsync(new Progress<double>(), CancellationToken.None);

        Assert.Equal(new HashSet<Guid> { oldShow }, FakeCollections.Members(legacy));
        Assert.DoesNotContain(_collections.Removed, r => r.Collection == legacy.Id);
        Assert.Equal(new HashSet<Guid> { showA.Id }, FakeCollections.Members(CollectionNamed("Serializd Watchlist (alice)")));
        Assert.Equal(new HashSet<Guid> { showB.Id }, FakeCollections.Members(CollectionNamed("Serializd Watchlist (bob)")));
    }

    [Fact]
    public async Task TrackedCollectionDeleted_CreatesAFreshOne()
    {
        var showA = AddSeries(1);
        AddUser("alice", "alice@example.com", new[] { 1 });
        await _runner.RunForAllAsync(new Progress<double>(), CancellationToken.None);
        var first = CollectionNamed("Serializd Watchlist (alice)");

        _collections.Delete(first.Id);
        await _runner.RunForAllAsync(new Progress<double>(), CancellationToken.None);

        Assert.Equal(new HashSet<Guid> { showA.Id }, FakeCollections.Members(CollectionNamed("Serializd Watchlist (alice)")));
    }

    [Fact]
    public async Task AdminChangesTheName_RenamesTheTrackedCollection()
    {
        AddSeries(1);
        AddUser("alice", "alice@example.com", new[] { 1 });
        await _runner.RunForAllAsync(new Progress<double>(), CancellationToken.None);
        var collection = CollectionNamed("Serializd Watchlist (alice)");

        Plugin.Instance!.Configuration.SerializdAccounts[0].WatchlistName = "Alice's Shows";
        await _runner.RunForAllAsync(new Progress<double>(), CancellationToken.None);

        Assert.Equal("Alice's Shows", collection.Name);
        Assert.Single(_collections.All);
        await _libraryManager.Received(1).UpdateItemAsync(collection, Arg.Any<BaseItem>(), ItemUpdateType.MetadataEdit, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdminRenamesOntoANameInUse_GetsAUniqueName()
    {
        var curated = _collections.Add("Staff Picks", Guid.NewGuid());
        AddSeries(1);
        AddUser("alice", "alice@example.com", new[] { 1 });
        await _runner.RunForAllAsync(new Progress<double>(), CancellationToken.None);
        var mine = CollectionNamed("Serializd Watchlist (alice)");

        Plugin.Instance!.Configuration.SerializdAccounts[0].WatchlistName = "Staff Picks";
        await _runner.RunForAllAsync(new Progress<double>(), CancellationToken.None);

        Assert.Equal("Staff Picks 2", mine.Name);
        Assert.Equal("Staff Picks", curated.Name);
    }

    [Fact]
    public async Task Upgrade_OldSharedCollectionAlreadyTrackedByAnotherAccount_IsNotAdopted()
    {
        var legacy = _collections.Add("Serializd Watchlist", Guid.NewGuid());
        AddSeries(1);
        var bob = AddUser("bob", "bob@example.com", Array.Empty<int>(), watchlistName: "Bob's Shows");
        SerializdCollectionStore.Set(bob.Id.ToString("N"), "bob@example.com", legacy.Id, "Bob's Shows");
        Plugin.Instance!.Configuration.SerializdAccounts[0].SyncWatchlist = false;
        AddUser("alice", "alice@example.com", new[] { 1 });

        await _runner.RunForAllAsync(new Progress<double>(), CancellationToken.None);

        Assert.Equal("Serializd Watchlist", legacy.Name);
        Assert.Single(_collections.All, b => b.Name == "Serializd Watchlist (alice)");
    }

    [Fact]
    public async Task UnreadableRecord_SkipsTheCollectionRatherThanGuessing()
    {
        _collections.Add("Serializd Watchlist", Guid.NewGuid());
        AddSeries(1);
        AddUser("alice", "alice@example.com", new[] { 1 });
        File.WriteAllText(Path.Combine(_tempDir, "serializd-watchlist-collections.json"), "{ not json");

        await _runner.RunForAllAsync(new Progress<double>(), CancellationToken.None);

        Assert.Single(_collections.All);
        await _collections.Manager.DidNotReceive().AddToCollectionAsync(Arg.Any<Guid>(), Arg.Any<IEnumerable<Guid>>());
        await _collections.Manager.DidNotReceive().CreateCollectionAsync(Arg.Any<CollectionCreationOptions>());
    }

    [Fact]
    public void UniqueName_SkipsNamesInUse_CaseInsensitively()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "staff picks", "Staff Picks 2" };

        Assert.Equal("Staff Picks 3", SerializdWatchlistSyncRunner.UniqueName("Staff Picks", taken));
        Assert.Equal("Fresh", SerializdWatchlistSyncRunner.UniqueName("Fresh", taken));
    }
}
