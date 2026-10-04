using System;
using System.IO;
using System.Linq;
using LetterboxdSync;
using Xunit;

namespace LetterboxdSync.Tests;

[Collection("Plugin")]
public class RatingPushStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "lbs-pushes-" + Guid.NewGuid().ToString("N") + ".jsonl");

    public RatingPushStoreTests()
    {
        RatingPushStore.DataPathOverride = _path;
        RatingPushStore.ResetForTesting();
    }

    public void Dispose()
    {
        RatingPushStore.DataPathOverride = null;
        RatingPushStore.ResetForTesting();
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
    }

    [Fact]
    public void GetLastPushed_NothingRecorded_ReturnsNull()
    {
        Assert.Null(RatingPushStore.GetLastPushed("u1", "lb", 1));
    }

    [Fact]
    public void RecordPushed_IsScopedToUserAccountAndFilm_AndUsernameIsCaseInsensitive()
    {
        RatingPushStore.RecordPushed("u1", "8BitProxy", 680, 4.5);

        Assert.Equal(4.5, RatingPushStore.GetLastPushed("u1", "8bitproxy", 680));
        Assert.Null(RatingPushStore.GetLastPushed("u2", "8bitproxy", 680));
        Assert.Null(RatingPushStore.GetLastPushed("u1", "other", 680));
        Assert.Null(RatingPushStore.GetLastPushed("u1", "8bitproxy", 238));
    }

    [Fact]
    public void Reload_ReadsLatestValuePerKey_AndCompactsTheFile()
    {
        RatingPushStore.RecordPushed("u1", "lb", 680, 3.0);
        RatingPushStore.RecordPushed("u1", "lb", 680, 3.5);
        RatingPushStore.RecordPushed("u1", "lb", 680, 4.0);
        RatingPushStore.RecordPushed("u1", "lb", 238, 5.0);
        Assert.Equal(4, File.ReadAllLines(_path).Length);

        RatingPushStore.ResetForTesting();

        Assert.Equal(4.0, RatingPushStore.GetLastPushed("u1", "lb", 680));
        Assert.Equal(5.0, RatingPushStore.GetLastPushed("u1", "lb", 238));
        Assert.Equal(2, File.ReadAllLines(_path).Count(l => l.Length > 0));
    }

    [Fact]
    public void RecordPushed_SameValueTwice_AppendsOnce()
    {
        RatingPushStore.RecordPushed("u1", "lb", 680, 3.5);
        RatingPushStore.RecordPushed("u1", "lb", 680, 3.5);
        Assert.Single(File.ReadAllLines(_path));
    }

    [Fact]
    public void Load_TornLastLine_KeepsTheRest()
    {
        RatingPushStore.RecordPushed("u1", "lb", 680, 3.5);
        File.AppendAllText(_path, "{\"u\":\"u1\",\"a\":\"lb\",\"t\":23");
        RatingPushStore.ResetForTesting();

        Assert.Equal(3.5, RatingPushStore.GetLastPushed("u1", "lb", 680));
    }
}
