using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using LetterboxdSync;
using LetterboxdSync.Serializd;
using Xunit;

namespace LetterboxdSync.Tests;

[Collection("Plugin")]
public class SyncHistoryCompactionTests : IDisposable
{
    private const string User = "demo-user";
    private const string Account = "demo-cinephile";
    private const int Film = 693134;

    private readonly string _tempDir;
    private readonly string _historyPath;
    private readonly string _activityPath;

    public SyncHistoryCompactionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-compact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _historyPath = Path.Combine(_tempDir, "letterboxd.jsonl");
        _activityPath = Path.Combine(_tempDir, "serializd.jsonl");
        SyncHistory.DataPathOverride = _historyPath;
        SerializdActivity.DataPathOverride = _activityPath;
        SyncHistory.ResetForTesting();
        SerializdActivity.ResetForTesting();
    }

    public void Dispose()
    {
        SyncHistory.DataPathOverride = null;
        SerializdActivity.DataPathOverride = null;
        SyncHistory.ResetForTesting();
        SerializdActivity.ResetForTesting();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private static SyncEvent Event(SyncStatus status, double hoursAgo, int tmdbId = Film,
        string? source = "scheduled", string title = "Dune Part Two", string? account = Account,
        string? error = null, bool permanent = false, bool outage = false) => new()
        {
            FilmTitle = title,
            TmdbId = tmdbId,
            Username = User,
            Account = account,
            Timestamp = DateTime.UtcNow.AddHours(-hoursAgo),
            ViewingDate = DateTime.UtcNow.Date,
            Status = status,
            Source = source,
            Error = error,
            PermanentFailure = permanent,
            Outage = outage,
        };

    private void WriteHistory(params SyncEvent[] events)
        => File.WriteAllLines(_historyPath, events.Select(e => JsonSerializer.Serialize(e)));

    private static int Total() => SyncHistory.GetPage(0, 10_000).Total;

    [Fact]
    public void Load_KeepsEveryOutcome_AndDropsOldUnsettledSkips()
    {
        var events = Enumerable.Range(0, 8).Select(i => Event(SyncStatus.Skipped, i, error: "No TMDb ID"))
            .Append(Event(SyncStatus.Success, 100))
            .Append(Event(SyncStatus.Rewatch, 90))
            .Append(Event(SyncStatus.Rated, 80, source: SyncEventSources.Rating))
            .Append(Event(SyncStatus.Requested, 70, source: SyncEventSources.SeerrAutoRequestFilm))
            .Append(Event(SyncStatus.Skipped, 200, source: SyncEventSources.DiaryImport))
            .Append(Event(SyncStatus.Failed, 300, tmdbId: 42, title: "Other"))
            .ToArray();
        WriteHistory(events);

        var total = Total();

        Assert.Equal(SyncHistory.MaxPrunableEventsPerFilm + 6, total);
        Assert.Equal(total, File.ReadAllLines(_historyPath).Length);
        Assert.Equal(new DateTime?(DateTime.UtcNow.Date), SyncHistory.GetLastSuccessfulSyncDate(User, Film, Account));
        Assert.True(SyncHistory.WasImportedFromDiary(User, Film));
        Assert.Equal(SyncStatus.Skipped, SyncHistory.GetLastStatusForFilm(User, Film, Account));
        Assert.Equal(SyncStatus.Failed, SyncHistory.GetLastStatusForFilm(User, 42));
    }

    [Fact]
    public void Load_NeverDropsSettledSkips_SoOldViewingDatesStaySettled()
    {
        var events = Enumerable.Range(0, 10)
            .Select(i =>
            {
                var e = Event(SyncStatus.Skipped, 24 * (i + 1), error: SyncHistory.AlreadyOnDiaryError);
                e.ViewingDate = DateTime.UtcNow.Date.AddDays(-(i + 1));
                return e;
            })
            .Concat(Enumerable.Range(0, 8).Select(i => Event(SyncStatus.Skipped, i, error: "No TMDb ID")))
            .ToArray();
        WriteHistory(events);

        Assert.Equal(10 + SyncHistory.MaxPrunableEventsPerFilm, Total());
        Assert.True(SyncHistory.WasSuccessfullySynced(User, Film, DateTime.UtcNow.Date.AddDays(-10), Account));
    }

    // The abandon rule for transient failures needs ten in a row over seven days; a cap of five
    // would silently stop any film from ever reaching it.
    [Fact]
    public void Load_KeepsTheWholeFailureStreakTheAbandonRulesCount()
    {
        var events = Enumerable.Range(0, LetterboxdSyncRunner.MaxTransientSyncFailures)
            .Select(i => Event(SyncStatus.Failed, 24 * i + 1, error: "Letterboxd returned 500"))
            .Append(Event(SyncStatus.Success, 24 * 30))
            .ToArray();
        var before = SyncHistory.GetFailureStreak(events, User, Film, Account);
        Assert.True(LetterboxdSyncRunner.ShouldAbandon(before));
        WriteHistory(events);

        var after = SyncHistory.GetFailureStreak(User, Film, Account);

        Assert.Equal(before, after);
        Assert.Equal(LetterboxdSyncRunner.MaxTransientSyncFailures + 1, File.ReadAllLines(_historyPath).Length);
    }

    [Fact]
    public void Load_DropsSurplusOutageRows_WithoutChangingTheStreak()
    {
        var events = Enumerable.Range(0, 20)
            .Select(i => Event(SyncStatus.Failed, i * 2 + 1, error: "Cloudflare", outage: true))
            .Concat(Enumerable.Range(0, 2).Select(i => Event(SyncStatus.Failed, i * 2 + 2, error: "not found", permanent: true)))
            .Append(Event(SyncStatus.Success, 500))
            .ToArray();
        var expected = SyncHistory.GetFailureStreak(events, User, Film, Account);
        WriteHistory(events);

        var streak = SyncHistory.GetFailureStreak(User, Film, Account);

        Assert.Equal(expected, streak);
        Assert.Equal(2, streak.Permanent);
        Assert.True(Total() < events.Length);
    }

    [Fact]
    public void Load_DropsFailuresOlderThanTheLastSuccess()
    {
        var events = Enumerable.Range(0, 12)
            .Select(i => Event(SyncStatus.Failed, 100 + i, error: "Letterboxd returned 500"))
            .Append(Event(SyncStatus.Success, 10))
            .ToArray();
        WriteHistory(events);

        Assert.Equal(SyncHistory.MaxPrunableEventsPerFilm + 1, Total());
        Assert.Equal(new FailureStreak(0, 0, 0), SyncHistory.GetFailureStreak(User, Film, Account));
    }

    [Fact]
    public void Load_CapsEachAccountSeparately()
    {
        var events = Enumerable.Range(0, 8).Select(i => Event(SyncStatus.Skipped, i, error: "No TMDb ID"))
            .Concat(Enumerable.Range(0, 8).Select(i => Event(SyncStatus.Skipped, i, error: "No TMDb ID", account: "second-account")))
            .ToArray();
        WriteHistory(events);

        Assert.Equal(2 * SyncHistory.MaxPrunableEventsPerFilm, Total());
        Assert.Equal(SyncStatus.Skipped, SyncHistory.GetLastStatusForFilm(User, Film, "second-account"));
    }

    [Fact]
    public void Load_WithAnUnreadableLine_LeavesTheFileUntouched()
    {
        WriteHistory(Enumerable.Range(0, 8).Select(i => Event(SyncStatus.Skipped, i, error: "No TMDb ID")).ToArray());
        File.AppendAllText(_historyPath, "{\"written by a newer version" + Environment.NewLine);
        var before = File.ReadAllText(_historyPath);

        Assert.Equal(SyncStatus.Skipped, SyncHistory.GetLastStatusForFilm(User, Film, Account));

        Assert.Equal(before, File.ReadAllText(_historyPath));
    }

    [Fact]
    public void Rewrite_AfterAnUnreadableLine_KeepsThatLine()
    {
        WriteHistory(Event(SyncStatus.Failed, 1, error: "500"));
        File.AppendAllText(_historyPath, "{\"written by a newer version" + Environment.NewLine);
        SyncHistory.GetPage(0, 1);

        var recorded = Event(SyncStatus.Failed, 0, error: "500");
        SyncHistory.Record(recorded);
        SyncHistory.MarkOutage(new[] { recorded }); // forces a full rewrite

        Assert.Contains("{\"written by a newer version", File.ReadAllLines(_historyPath));
        Assert.Equal(3, File.ReadAllLines(_historyPath).Length);
    }

    [Fact]
    public void Record_AfterATornLastLine_StartsOnAFreshLine()
    {
        WriteHistory(Event(SyncStatus.Success, 5));
        File.AppendAllText(_historyPath, "{\"FilmTitle\":\"torn"); // a crash cut the last append short

        SyncHistory.Record(Event(SyncStatus.Success, 0, tmdbId: 7, title: "Arrival"));

        SyncHistory.ResetForTesting();
        Assert.NotNull(SyncHistory.GetLastSuccessfulSyncDate(User, 7, Account));
        Assert.Equal(3, File.ReadAllLines(_historyPath).Length);
    }

    [Fact]
    public void RecordedEvents_AreVisibleToPerFilmQueries()
    {
        SyncHistory.Record(Event(SyncStatus.Failed, 2, permanent: true));
        SyncHistory.Record(Event(SyncStatus.Failed, 1, permanent: true));

        Assert.Equal(2, SyncHistory.GetConsecutiveFailureCount(User, Film, Account));
        Assert.Equal(0, SyncHistory.GetConsecutiveFailureCount(User, 42, Account));
    }

    [Fact]
    public void SerializdActivity_IsCompactedPerEpisode()
    {
        var skips = Enumerable.Range(0, 8)
            .Select(i => Event(SyncStatus.Skipped, i, tmdbId: 1396, title: "Breaking Bad · S1E1", account: null));
        var otherEpisode = Event(SyncStatus.Failed, 50, tmdbId: 1396, title: "Breaking Bad · S1E2", account: null);
        var logged = Event(SyncStatus.Success, 60, tmdbId: 1396, title: "Breaking Bad · S1E1", account: null);
        File.WriteAllLines(_activityPath, skips.Append(otherEpisode).Append(logged).Select(e => JsonSerializer.Serialize(e)));

        var (_, total) = SerializdActivity.GetPage(0, 100);

        Assert.Equal(SyncHistory.MaxPrunableEventsPerFilm + 2, total);
        Assert.Equal(total, File.ReadAllLines(_activityPath).Length);
    }
}
