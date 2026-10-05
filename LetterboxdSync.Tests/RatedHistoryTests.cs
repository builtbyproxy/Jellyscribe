using System;
using System.Collections.Generic;
using LetterboxdSync;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// A rating push (SyncStatus.Rated) is not a diary outcome. These pin that it never changes what
/// the diary runner reads from history for the same film: the duplicate backstop, "skip
/// previously synced", the abandon-after-failures counter, and the retry ordering.
/// </summary>
[Collection("Plugin")]
public class RatedHistoryTests
{
    private const string User = "lachlan";
    private const int Film = 1233413;
    private static readonly DateTime Watched = new(2026, 10, 1);

    private static SyncEvent Evt(SyncStatus status, int minutesAgo, DateTime? viewing = null) => new()
    {
        Username = User,
        TmdbId = Film,
        Status = status,
        Timestamp = new DateTime(2026, 10, 4, 12, 0, 0).AddMinutes(-minutesAgo),
        ViewingDate = viewing,
        Source = status == SyncStatus.Rated ? SyncEventSources.Rating : "scheduled"
    };

    [Fact]
    public void NewerRatedEvent_DoesNotBlankTheDuplicateBackstop()
    {
        var events = new List<SyncEvent> { Evt(SyncStatus.Success, 60, Watched), Evt(SyncStatus.Rated, 1) };
        Assert.Equal(Watched, SyncHistory.GetLastSuccessfulSyncDate(events, User, Film));
    }

    [Fact]
    public void RatedEventAlone_DoesNotCountAsPreviouslySynced()
    {
        var events = new List<SyncEvent> { Evt(SyncStatus.Rated, 1) };
        Assert.False(SyncHistory.WasSuccessfullySynced(events, User, Film, DateTime.Today));
        Assert.Null(SyncHistory.GetLastSuccessfulSyncDate(events, User, Film));
    }

    [Fact]
    public void RatedEvent_NeitherBreaksNorExtendsTheFailureStreak()
    {
        var events = new List<SyncEvent>
        {
            Evt(SyncStatus.Failed, 30),
            Evt(SyncStatus.Rated, 20),
            Evt(SyncStatus.Failed, 10),
            Evt(SyncStatus.Rated, 1)
        };
        Assert.Equal(2, SyncHistory.GetConsecutiveFailureCount(events, User, Film));
    }

    [Fact]
    public void Stats_TotalLeavesOutRatedEvents()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lbs-rated-stats-" + Guid.NewGuid().ToString("N") + ".jsonl");
        SyncHistory.DataPathOverride = path;
        SyncHistory.ResetForTesting();
        try
        {
            SyncHistory.Record(Evt(SyncStatus.Success, 30, Watched));
            SyncHistory.Record(Evt(SyncStatus.Rated, 1));

            var stats = SyncHistory.GetStats(User);
            Assert.Equal(1, stats.Total); // the dashboards show this as "Films logged"
            Assert.Equal(1, stats.Success);
        }
        finally
        {
            SyncHistory.DataPathOverride = null;
            SyncHistory.ResetForTesting();
            try { System.IO.File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void RatedEvent_DoesNotMaskTheLastDiaryStatus()
    {
        var events = new List<SyncEvent> { Evt(SyncStatus.Failed, 30), Evt(SyncStatus.Rated, 1) };
        Assert.Equal(SyncStatus.Failed, SyncHistory.GetLastStatusForFilm(events, User, Film));
    }
}
