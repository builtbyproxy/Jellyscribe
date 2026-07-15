using System;
using System.IO;
using LetterboxdSync.Serializd;
using Xunit;

namespace LetterboxdSync.Tests.Serializd;

public class SerializdSyncHistoryTests : IDisposable
{
    private readonly string _file;
    private const string Acct1 = "acct1@example.com";
    private const string Acct2 = "acct2@example.com";

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
        Assert.False(SerializdSyncHistory.Has("user1", Acct1, 77236, 1, 1));
        SerializdSyncHistory.Record("user1", Acct1, 77236, 1, 1);
        Assert.True(SerializdSyncHistory.Has("user1", Acct1, 77236, 1, 1));
        Assert.False(SerializdSyncHistory.Has("user1", Acct1, 77236, 1, 2));
        Assert.False(SerializdSyncHistory.Has("user2", Acct1, 77236, 1, 1)); // per-user
    }

    [Fact]
    public void Record_Duplicate_DoesNotDoubleWrite()
    {
        SerializdSyncHistory.Record("user1", Acct1, 77236, 1, 1);
        SerializdSyncHistory.Record("user1", Acct1, 77236, 1, 1);
        var lines = File.ReadAllLines(_file);
        Assert.Single(lines);
    }

    [Fact]
    public void History_PersistsAcrossReload()
    {
        SerializdSyncHistory.Record("user1", Acct1, 77236, 1, 5);
        SerializdSyncHistory.ResetForTesting(); // forces re-read from disk
        Assert.True(SerializdSyncHistory.Has("user1", Acct1, 77236, 1, 5));
    }

    [Fact]
    public void Kinds_AreTrackedIndependently()
    {
        // Marking an episode watched must NOT make it look diary-logged, and vice versa.
        SerializdSyncHistory.Record("user1", Acct1, 77236, 1, 1, SerializdSyncHistory.KindWatched);
        Assert.True(SerializdSyncHistory.Has("user1", Acct1, 77236, 1, 1, SerializdSyncHistory.KindWatched));
        Assert.False(SerializdSyncHistory.Has("user1", Acct1, 77236, 1, 1, SerializdSyncHistory.KindLog));

        SerializdSyncHistory.Record("user1", Acct1, 77236, 1, 1, SerializdSyncHistory.KindLog);
        Assert.True(SerializdSyncHistory.Has("user1", Acct1, 77236, 1, 1, SerializdSyncHistory.KindLog));
    }

    [Fact]
    public void WatchedKind_StaysSuffixFreeApartFromAccount()
    {
        // KindWatched keys still carry no kind suffix (only non-default kinds do); a key
        // written in that exact shape must match the default (watched) lookup.
        File.WriteAllText(_file, "user1|" + Acct1 + "|77236|1|1\n");
        SerializdSyncHistory.ResetForTesting();
        Assert.True(SerializdSyncHistory.Has("user1", Acct1, 77236, 1, 1)); // default kind = watched
        Assert.False(SerializdSyncHistory.Has("user1", Acct1, 77236, 1, 1, SerializdSyncHistory.KindLog));
    }

    [Fact]
    public void Has_IsScopedPerAccount_NotJustPerUser()
    {
        // The bug this guards: a second Serializd account linked to the same Jellyfin user
        // must not read the first account's history as its own, or it silently never
        // receives episodes the first account already logged.
        SerializdSyncHistory.Record("user1", Acct1, 77236, 1, 1);
        Assert.True(SerializdSyncHistory.Has("user1", Acct1, 77236, 1, 1));
        Assert.False(SerializdSyncHistory.Has("user1", Acct2, 77236, 1, 1));

        SerializdSyncHistory.Record("user1", Acct2, 77236, 1, 1);
        Assert.True(SerializdSyncHistory.Has("user1", Acct2, 77236, 1, 1));
    }
}
