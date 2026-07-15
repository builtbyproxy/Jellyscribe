using System;
using System.IO;
using LetterboxdSync;
using LetterboxdSync.Serializd;
using Xunit;

namespace LetterboxdSync.Tests.Serializd;

public class SerializdActivityTests : IDisposable
{
    private readonly string _file;

    public SerializdActivityTests()
    {
        _file = Path.Combine(Path.GetTempPath(), "sz-activity-" + Guid.NewGuid().ToString("N") + ".jsonl");
        SerializdActivity.DataPathOverride = _file;
        SerializdActivity.ResetForTesting();
    }

    public void Dispose()
    {
        SerializdActivity.DataPathOverride = null;
        SerializdActivity.ResetForTesting();
        try { if (File.Exists(_file)) File.Delete(_file); } catch { }
    }

    private static SyncEvent Ev(string user, SyncStatus status, string title = "Show · S1E1") => new()
    {
        FilmTitle = title,
        Username = user,
        Timestamp = DateTime.UtcNow,
        Status = status,
        Source = "playback",
    };

    [Fact]
    public void GetStats_CountsByStatus_FilteredByUser()
    {
        SerializdActivity.Record(Ev("lachlan", SyncStatus.Success));
        SerializdActivity.Record(Ev("lachlan", SyncStatus.Rewatch));
        SerializdActivity.Record(Ev("lachlan", SyncStatus.Failed));
        SerializdActivity.Record(Ev("someoneelse", SyncStatus.Success));

        var (total, success, failed, skipped, rewatches) = SerializdActivity.GetStats("lachlan");
        Assert.Equal(3, total);
        Assert.Equal(1, success);
        Assert.Equal(1, failed);
        Assert.Equal(0, skipped);
        Assert.Equal(1, rewatches);
    }

    [Fact]
    public void GetPage_ReturnsNewestFirst_WithTotal()
    {
        SerializdActivity.Record(new SyncEvent { FilmTitle = "old", Username = "u", Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), Status = SyncStatus.Success });
        SerializdActivity.Record(new SyncEvent { FilmTitle = "new", Username = "u", Timestamp = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), Status = SyncStatus.Success });

        var (events, total) = SerializdActivity.GetPage(0, 10, "u");
        Assert.Equal(2, total);
        Assert.Equal("new", events[0].FilmTitle); // newest first
    }

    [Fact]
    public void Activity_PersistsAcrossReload()
    {
        SerializdActivity.Record(Ev("u", SyncStatus.Success));
        SerializdActivity.ResetForTesting();
        Assert.Equal(1, SerializdActivity.GetStats("u").Total);
    }
}
