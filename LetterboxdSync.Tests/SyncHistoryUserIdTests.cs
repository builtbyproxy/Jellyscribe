using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Jellyfin.Database.Implementations.Entities;
using LetterboxdSync;
using LetterboxdSync.Serializd;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace LetterboxdSync.Tests;

[Collection("Plugin")]
public class SyncHistoryUserIdTests : IDisposable
{
    private const string AliceId = "0f8fad5bd9cb469fa16570867728950e";

    private readonly string _tempDir;
    private readonly Dictionary<string, string> _users = new(StringComparer.Ordinal);

    public SyncHistoryUserIdTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-userid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        SyncHistory.DataPathOverride = Path.Combine(_tempDir, "letterboxd.jsonl");
        SerializdActivity.DataPathOverride = Path.Combine(_tempDir, "serializd.jsonl");
        SyncHistory.ResetForTesting();
        SerializdActivity.ResetForTesting();
        SyncHistory.SetLogger(NullLogger.Instance);
        SyncHistory.UserIdResolver = name => _users.TryGetValue(name, out var id) ? id : null;
    }

    public void Dispose()
    {
        SyncHistory.UserIdResolver = null;
        SyncHistory.DataPathOverride = null;
        SerializdActivity.DataPathOverride = null;
        SyncHistory.ResetForTesting();
        SerializdActivity.ResetForTesting();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private static SyncEvent Event(string username, int tmdbId = 1, string? userId = null,
        SyncStatus status = SyncStatus.Success, DateTime? viewingDate = null) => new()
        {
            FilmTitle = $"Film {tmdbId}",
            TmdbId = tmdbId,
            Username = username,
            UserId = userId,
            Timestamp = DateTime.UtcNow,
            ViewingDate = viewingDate,
            Status = status,
            Source = "test",
        };

    [Fact]
    public void Record_StampsTheUserIdOfTheCurrentUsername()
    {
        _users["alice"] = AliceId;

        SyncHistory.Record(Event("alice"));

        var stored = JsonSerializer.Deserialize<SyncEvent>(File.ReadAllLines(SyncHistory.DataPathOverride!).Single());
        Assert.Equal(AliceId, stored!.UserId);
    }

    [Fact]
    public void Record_KeepsAnExplicitUserId()
    {
        _users["alice"] = "somethingelse";

        SyncHistory.Record(Event("alice", userId: AliceId));

        Assert.Equal(AliceId, SyncHistory.GetRecent().Single().UserId);
    }

    [Fact]
    public void Dashboard_StillShowsHistoryAfterTheUserIsRenamed()
    {
        _users["alice"] = AliceId;
        SyncHistory.Record(Event("alice", tmdbId: 1));
        SerializdActivity.Record(Event("alice", tmdbId: 2));

        _users.Remove("alice");
        _users["Alice Smith"] = AliceId;

        Assert.Equal(1, SyncHistory.GetPage(0, 50, "Alice Smith").Total);
        Assert.Equal(1, SyncHistory.GetStats("Alice Smith").Success);
        Assert.Equal(1, SerializdActivity.GetPage(0, 50, "Alice Smith").Total);
        Assert.Equal(1, SerializdActivity.GetStats("Alice Smith").Success);
    }

    [Fact]
    public void DuplicateChecks_StillSeeASyncMadeBeforeTheRename()
    {
        var watched = new DateTime(2026, 9, 1);
        var events = new List<SyncEvent> { Event("alice", tmdbId: 42, userId: AliceId, viewingDate: watched) };
        _users["Alice Smith"] = AliceId;

        Assert.True(SyncHistory.WasSuccessfullySynced(events, "Alice Smith", 42, watched));
        Assert.Equal(watched, SyncHistory.GetLastSuccessfulSyncDate(events, "Alice Smith", 42));
        Assert.Equal(SyncStatus.Success, SyncHistory.GetLastStatusForFilm(events, "Alice Smith", 42));
    }

    [Fact]
    public void EntryWithoutAUserId_StillMatchesOnUsername()
    {
        _users["alice"] = AliceId;
        var events = new List<SyncEvent> { Event("alice") };

        Assert.Equal(1, SyncHistory.GetPage(events, 0, 50, "alice").Total);
        Assert.Equal(0, SyncHistory.GetPage(events, 0, 50, "bob").Total);
    }

    [Fact]
    public void AnotherUserWithTheOldName_DoesNotSeeTheRenamedUsersHistory()
    {
        var events = new List<SyncEvent> { Event("alice", userId: AliceId) };
        _users["alice"] = "1b4e28ba2fa1411a8c4b0a8d4b1e9f3c";

        Assert.Equal(0, SyncHistory.GetPage(events, 0, 50, "alice").Total);
    }

    [Fact]
    public void StampMissingUserIds_BackfillsOnlyUsernamesThatStillExist()
    {
        File.WriteAllLines(SyncHistory.DataPathOverride!, new[]
        {
            JsonSerializer.Serialize(Event("alice", tmdbId: 1)),
            JsonSerializer.Serialize(Event("gone", tmdbId: 2)),
        });
        _users["alice"] = AliceId;

        Assert.Equal(1, SyncHistory.StampMissingUserIds());
        Assert.Equal(0, SyncHistory.StampMissingUserIds());

        var stored = File.ReadAllLines(SyncHistory.DataPathOverride!)
            .Select(l => JsonSerializer.Deserialize<SyncEvent>(l)!)
            .ToDictionary(e => e.Username, e => e.UserId);
        Assert.Equal(AliceId, stored["alice"]);
        Assert.Null(stored["gone"]);
    }

    [Fact]
    public void SerializdStampMissingUserIds_PersistsTheIds()
    {
        File.WriteAllLines(SerializdActivity.DataPathOverride!, new[] { JsonSerializer.Serialize(Event("alice")) });
        _users["alice"] = AliceId;

        Assert.Equal(1, SerializdActivity.StampMissingUserIds());

        var stored = JsonSerializer.Deserialize<SyncEvent>(File.ReadAllLines(SerializdActivity.DataPathOverride!).Single());
        Assert.Equal(AliceId, stored!.UserId);
    }

    [Fact]
    public async System.Threading.Tasks.Task UserIdentityService_ResolvesUsernamesThroughJellyfin()
    {
        var user = new User("alice", "test-provider-id", "test-reset-id");
        var userManager = Substitute.For<IUserManager>();
        userManager.GetUserByName("alice").Returns(user);
        var service = new UserIdentityService(userManager, NullLogger<UserIdentityService>.Instance);
        UserIdentityService.ResetForTesting();

        await service.StartAsync(CancellationToken.None);
        await service.Stamping!;

        Assert.Equal(user.Id.ToString("N"), SyncHistory.ResolveUserId("alice"));
        Assert.Null(SyncHistory.ResolveUserId("nobody"));
    }

    [Fact]
    public async System.Threading.Tasks.Task UserIdentityService_StampsInTheBackground_AndOnlyOnce()
    {
        File.WriteAllLines(SyncHistory.DataPathOverride!, new[] { JsonSerializer.Serialize(Event("alice")) });
        var user = new User("alice", "test-provider-id", "test-reset-id");
        var userManager = Substitute.For<IUserManager>();
        userManager.GetUserByName("alice").Returns(user);
        UserIdentityService.ResetForTesting();
        var first = new UserIdentityService(userManager, NullLogger<UserIdentityService>.Instance);
        var second = new UserIdentityService(userManager, NullLogger<UserIdentityService>.Instance);

        await first.StartAsync(CancellationToken.None);
        await second.StartAsync(CancellationToken.None);
        await first.Stamping!;

        Assert.Null(second.Stamping);
        var stored = JsonSerializer.Deserialize<SyncEvent>(File.ReadAllLines(SyncHistory.DataPathOverride!).Single());
        Assert.Equal(user.Id.ToString("N"), stored!.UserId);
    }
}
