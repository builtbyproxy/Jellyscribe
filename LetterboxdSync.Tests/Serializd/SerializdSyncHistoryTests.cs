using System;
using System.IO;
using LetterboxdSync.Serializd;
using Xunit;

namespace LetterboxdSync.Tests.Serializd;

public class SerializdSyncHistoryTests : IDisposable
{
    private readonly string _file;

    public SerializdSyncHistoryTests()
    {
        _file = Path.Combine(Path.GetTempPath(), "sz-hist-" + Guid.NewGuid().ToString("N") + ".jsonl");
        SerializdSyncHistory.DataPathOverride = _file;
        SerializdSyncHistory.ResetForTesting();
    }

    public void Dispose()
    {
        SerializdSyncHistory.DataPathOverride = null;
        SerializdSyncHistory.ResetForTesting();
        try { if (File.Exists(_file)) File.Delete(_file); } catch { }
    }

    [Fact]
    public void RecordThenHas_ReturnsTrueForRecorded_FalseOtherwise()
    {
        Assert.False(SerializdSyncHistory.Has("user1", 77236, 1, 1));
        SerializdSyncHistory.Record("user1", 77236, 1, 1);
        Assert.True(SerializdSyncHistory.Has("user1", 77236, 1, 1));
        Assert.False(SerializdSyncHistory.Has("user1", 77236, 1, 2));
        Assert.False(SerializdSyncHistory.Has("user2", 77236, 1, 1)); // per-user
    }

    [Fact]
    public void Record_Duplicate_DoesNotDoubleWrite()
    {
        SerializdSyncHistory.Record("user1", 77236, 1, 1);
        SerializdSyncHistory.Record("user1", 77236, 1, 1);
        var lines = File.ReadAllLines(_file);
        Assert.Single(lines);
    }

    [Fact]
    public void History_PersistsAcrossReload()
    {
        SerializdSyncHistory.Record("user1", 77236, 1, 5);
        SerializdSyncHistory.ResetForTesting(); // forces re-read from disk
        Assert.True(SerializdSyncHistory.Has("user1", 77236, 1, 5));
    }
}
