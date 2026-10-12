using System;
using System.IO;
using System.Linq;
using LetterboxdSync.Serializd;
using Xunit;

namespace LetterboxdSync.Tests.Serializd;

// SerializdSyncHistory is static (path override + cache). Other classes in the
// "Plugin" collection reset it too (runner and playback tests), so this class
// must run serially with them or a parallel reset can swap the file mid-test.
[Collection("Plugin")]
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

    [Fact]
    public void Has_FallsBackToPreAccountScopingKey_SoUpgradeDoesNotReLogEverything()
    {
        // Real-world regression: a server that already ran a pre-account-scoping build has
        // history written as "user|show|season|episode" (no account). Deploying the
        // account-scoped format must not make every already-synced episode look unsynced,
        // or the next catch-up run duplicate-logs the entire back catalog.
        File.WriteAllText(_file, "user1|77236|1|7\n");
        SerializdSyncHistory.ResetForTesting();

        Assert.True(SerializdSyncHistory.Has("user1", Acct1, 77236, 1, 7));

        // A fresh episode with no history at all is still correctly reported as new.
        Assert.False(SerializdSyncHistory.Has("user1", Acct1, 77236, 1, 8));

        // Recording that fresh episode writes the new account-scoped format, not the legacy one.
        SerializdSyncHistory.Record("user1", Acct1, 77236, 1, 8);
        var lines = File.ReadAllLines(_file);
        Assert.Contains("user1|" + Acct1 + "|77236|1|8", lines);
    }

    [Fact]
    public void WatchedSeasonsByShow_CountsWatchedLoggedAndImportedEpisodes_ForThisUserAndAccount()
    {
        File.WriteAllLines(_file, new[]
        {
            "user1|" + Acct1 + "|100|1|3",                // watched
            "user1|" + Acct1 + "|100|2|0|log",            // dated log
            "user1|" + Acct1 + "|200|4|2|imported",       // diary import
            "user1|300|5|1",                               // pre-account-scoping watched row
            "user1|300|6|1|log",                           // pre-account-scoping log row
            "user1|" + Acct1 + "|100|0|0|showmeta",       // show-level rating: not a watch
            "user1|" + Acct1 + "|100|0|4",                // a special: never requested
            "user1|" + Acct2 + "|400|1|1",                // another account
            "user2|" + Acct1 + "|500|1|1",                // another user
        });
        SerializdSyncHistory.ResetForTesting();

        var watched = SerializdSyncHistory.WatchedSeasonsByShow("user1", Acct1);

        Assert.Equal(new[] { 100, 200, 300 }, watched.Keys.OrderBy(k => k));
        Assert.Equal(new[] { 1, 2 }, watched[100].OrderBy(s => s));
        Assert.Equal(new[] { 4 }, watched[200]);
        Assert.Equal(new[] { 5, 6 }, watched[300].OrderBy(s => s));
    }

    [Fact]
    public void WatchedSeasonsByShow_MatchesTheAccountEmailWhateverItsCase()
    {
        SerializdSyncHistory.Record("user1", "TvFan@Example.com", 100, 1, 1);

        var watched = SerializdSyncHistory.WatchedSeasonsByShow("user1", "tvfan@example.com");

        Assert.Equal(new[] { 1 }, watched[100]);
    }
}
