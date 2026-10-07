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
    private const string User = "lachlan";
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

    private static SyncEvent Event(SyncStatus status, int minutesAgo, int tmdbId = Film,
        string? source = "scheduled", string title = "Dune Part Two") => new()
        {
            FilmTitle = title,
            TmdbId = tmdbId,
            Username = User,
            Timestamp = DateTime.UtcNow.AddMinutes(-minutesAgo),
            ViewingDate = DateTime.UtcNow.Date,
            Status = status,
            Source = source,
            PermanentFailure = status == SyncStatus.Failed,
        };

    private static void WriteLines(string path, params SyncEvent[] events)
        => File.WriteAllLines(path, events.Select(e => JsonSerializer.Serialize(e)));

    [Fact]
    public void Load_KeepsEveryOutcome_ButOnlyTheNewestSkippedOrFailedPerFilm()
    {
        var events = Enumerable.Range(0, 8).Select(i => Event(SyncStatus.Failed, minutesAgo: i))
            .Concat(Enumerable.Range(10, 8).Select(i => Event(SyncStatus.Skipped, minutesAgo: i)))
            .Append(Event(SyncStatus.Success, minutesAgo: 100))
            .Append(Event(SyncStatus.Rewatch, minutesAgo: 90))
            .Append(Event(SyncStatus.Rated, minutesAgo: 80, source: SyncEventSources.Rating))
            .Append(Event(SyncStatus.Skipped, minutesAgo: 200, source: SyncEventSources.DiaryImport))
            .Append(Event(SyncStatus.Failed, minutesAgo: 300, tmdbId: 42, title: "Other"))
            .ToArray();
        WriteLines(_historyPath, events);

        var (_, total) = SyncHistory.GetPage(0, 100);

        Assert.Equal(SyncHistory.MaxPrunableEventsPerFilm + 5, total);
        Assert.Equal(total, File.ReadAllLines(_historyPath).Length);
        Assert.Equal(new DateTime?(DateTime.UtcNow.Date), SyncHistory.GetLastSuccessfulSyncDate(User, Film));
        Assert.True(SyncHistory.WasImportedFromDiary(User, Film));
        Assert.Equal(SyncStatus.Failed, SyncHistory.GetLastStatusForFilm(User, Film));
        Assert.Equal(SyncHistory.MaxPrunableEventsPerFilm, SyncHistory.GetConsecutiveFailureCount(User, Film));
        Assert.Equal(SyncStatus.Failed, SyncHistory.GetLastStatusForFilm(User, 42));
    }

    [Fact]
    public void Load_NothingToDrop_LeavesTheFileUntouched()
    {
        WriteLines(_historyPath, Event(SyncStatus.Failed, 1), Event(SyncStatus.Success, 2));
        File.AppendAllText(_historyPath, "not json" + Environment.NewLine);
        var before = File.ReadAllText(_historyPath);

        Assert.Equal(SyncStatus.Failed, SyncHistory.GetLastStatusForFilm(User, Film));

        Assert.Equal(before, File.ReadAllText(_historyPath));
    }

    [Fact]
    public void Cap_NeverHidesAnAbandonableFailureStreak()
    {
        Assert.True(SyncHistory.MaxPrunableEventsPerFilm >= LetterboxdSyncRunner.MaxConsecutiveSyncFailures);
    }

    [Fact]
    public void RecordedEvents_AreVisibleToPerFilmQueries()
    {
        SyncHistory.Record(Event(SyncStatus.Failed, 2));
        SyncHistory.Record(Event(SyncStatus.Failed, 1));

        Assert.Equal(2, SyncHistory.GetConsecutiveFailureCount(User, Film));
        Assert.Equal(0, SyncHistory.GetConsecutiveFailureCount(User, 42));
    }

    [Fact]
    public void SerializdActivity_IsCompactedPerEpisode()
    {
        var failures = Enumerable.Range(0, 8)
            .Select(i => Event(SyncStatus.Failed, minutesAgo: i, tmdbId: 1396, title: "Breaking Bad · S1E1"));
        var otherEpisode = Event(SyncStatus.Failed, minutesAgo: 50, tmdbId: 1396, title: "Breaking Bad · S1E2");
        var logged = Event(SyncStatus.Success, minutesAgo: 60, tmdbId: 1396, title: "Breaking Bad · S1E1");
        WriteLines(_activityPath, failures.Append(otherEpisode).Append(logged).ToArray());

        var (_, total) = SerializdActivity.GetPage(0, 100);

        Assert.Equal(SyncHistory.MaxPrunableEventsPerFilm + 2, total);
        Assert.Equal(total, File.ReadAllLines(_activityPath).Length);
    }
}
