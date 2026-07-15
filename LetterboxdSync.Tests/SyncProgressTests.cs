using LetterboxdSync;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// SyncProgress tracks long-running sync state per independent-concurrency "track"
/// (Letterboxd vs Serializd), so two families running at once don't clobber each other.
/// Because the backing dictionary is static, every test resets its track via Start()
/// before asserting, which is the same pattern the real callers use at the top of each run.
/// </summary>
[Collection("SyncProgress")]
public class SyncProgressTests
{
    private static object Snapshot() => SyncProgress.GetSnapshot();
    private static T Prop<T>(object snapshot, string name) =>
        (T)snapshot.GetType().GetProperty(name)!.GetValue(snapshot)!;

    [Fact]
    public void Start_SetsTaskNamePhaseAndRunningFlag()
    {
        SyncProgress.Start(SyncProgress.TrackLetterboxd, "MyTask", "init");

        var s = Snapshot();
        Assert.Equal("MyTask", Prop<string>(s, "taskName"));
        Assert.Equal("init", Prop<string>(s, "phase"));
        Assert.True(Prop<bool>(s, "isRunning"));
        Assert.NotNull(s.GetType().GetProperty("startedAt")!.GetValue(s));

        SyncProgress.Complete(SyncProgress.TrackLetterboxd);
    }

    [Fact]
    public void Start_ResetsCounters()
    {
        SyncProgress.Start(SyncProgress.TrackLetterboxd, "First", "p");
        SyncProgress.SetTotal(SyncProgress.TrackLetterboxd, 10);
        SyncProgress.IncrementProcessed(SyncProgress.TrackLetterboxd);
        SyncProgress.IncrementCacheHit(SyncProgress.TrackLetterboxd);
        SyncProgress.IncrementNewLookup(SyncProgress.TrackLetterboxd);

        SyncProgress.Start(SyncProgress.TrackLetterboxd, "Second", "p2");

        var s = Snapshot();
        Assert.Equal(0, Prop<int>(s, "totalItems"));
        Assert.Equal(0, Prop<int>(s, "processedItems"));
        Assert.Equal(0, Prop<int>(s, "cacheHits"));
        Assert.Equal(0, Prop<int>(s, "newLookups"));
        Assert.Equal("Second", Prop<string>(s, "taskName"));

        SyncProgress.Complete(SyncProgress.TrackLetterboxd);
    }

    [Fact]
    public void SetPhase_UpdatesPhaseWithoutTouchingOtherFields()
    {
        SyncProgress.Start(SyncProgress.TrackLetterboxd, "Task", "scanning");
        SyncProgress.SetTotal(SyncProgress.TrackLetterboxd, 42);
        SyncProgress.IncrementProcessed(SyncProgress.TrackLetterboxd);

        SyncProgress.SetPhase(SyncProgress.TrackLetterboxd, "uploading");

        var s = Snapshot();
        Assert.Equal("uploading", Prop<string>(s, "phase"));
        Assert.Equal(42, Prop<int>(s, "totalItems"));
        Assert.Equal(1, Prop<int>(s, "processedItems"));

        SyncProgress.Complete(SyncProgress.TrackLetterboxd);
    }

    [Fact]
    public void SetTotal_OverwritesPreviousValue()
    {
        SyncProgress.Start(SyncProgress.TrackLetterboxd, "Task", "p");
        SyncProgress.SetTotal(SyncProgress.TrackLetterboxd, 5);
        SyncProgress.SetTotal(SyncProgress.TrackLetterboxd, 50);

        Assert.Equal(50, Prop<int>(Snapshot(), "totalItems"));

        SyncProgress.Complete(SyncProgress.TrackLetterboxd);
    }

    [Fact]
    public void IncrementProcessed_AccumulatesMonotonically()
    {
        SyncProgress.Start(SyncProgress.TrackLetterboxd, "Task", "p");

        for (int i = 0; i < 7; i++) SyncProgress.IncrementProcessed(SyncProgress.TrackLetterboxd);

        Assert.Equal(7, Prop<int>(Snapshot(), "processedItems"));

        SyncProgress.Complete(SyncProgress.TrackLetterboxd);
    }

    [Fact]
    public void IncrementCacheHit_AndNewLookup_TrackedSeparately()
    {
        SyncProgress.Start(SyncProgress.TrackLetterboxd, "Task", "p");

        SyncProgress.IncrementCacheHit(SyncProgress.TrackLetterboxd);
        SyncProgress.IncrementCacheHit(SyncProgress.TrackLetterboxd);
        SyncProgress.IncrementNewLookup(SyncProgress.TrackLetterboxd);

        var s = Snapshot();
        Assert.Equal(2, Prop<int>(s, "cacheHits"));
        Assert.Equal(1, Prop<int>(s, "newLookups"));

        SyncProgress.Complete(SyncProgress.TrackLetterboxd);
    }

    [Fact]
    public void Complete_ClearsRunningFlag_AndSetsPhaseComplete()
    {
        SyncProgress.Start(SyncProgress.TrackLetterboxd, "Task", "active");

        SyncProgress.Complete(SyncProgress.TrackLetterboxd);

        var s = Snapshot();
        Assert.False(Prop<bool>(s, "isRunning"));
        Assert.Equal("complete", Prop<string>(s, "phase"));
    }

    [Fact]
    public void GetSnapshot_ReturnsCurrentState()
    {
        SyncProgress.Start(SyncProgress.TrackLetterboxd, "SnapshotTask", "phase-x");
        SyncProgress.SetTotal(SyncProgress.TrackLetterboxd, 10);
        SyncProgress.IncrementProcessed(SyncProgress.TrackLetterboxd);
        SyncProgress.IncrementCacheHit(SyncProgress.TrackLetterboxd);

        var snapshot = SyncProgress.GetSnapshot();
        var t = snapshot.GetType();

        // Anonymous type, so fields need reflection. Verify the dashboard payload contract.
        Assert.Equal("SnapshotTask", t.GetProperty("taskName")!.GetValue(snapshot));
        Assert.Equal("phase-x", t.GetProperty("phase")!.GetValue(snapshot));
        Assert.Equal(10, t.GetProperty("totalItems")!.GetValue(snapshot));
        Assert.Equal(1, t.GetProperty("processedItems")!.GetValue(snapshot));
        Assert.Equal(1, t.GetProperty("cacheHits")!.GetValue(snapshot));
        Assert.Equal(0, t.GetProperty("newLookups")!.GetValue(snapshot));
        Assert.Equal(true, t.GetProperty("isRunning")!.GetValue(snapshot));
        Assert.NotNull(t.GetProperty("startedAt")!.GetValue(snapshot));
        // Elapsed is integer seconds; for a freshly-started run it's ~0 but never negative.
        var elapsed = (int)t.GetProperty("elapsedSeconds")!.GetValue(snapshot)!;
        Assert.True(elapsed >= 0);

        SyncProgress.Complete(SyncProgress.TrackLetterboxd);
    }

    [Fact]
    public void GetSnapshot_AfterComplete_ReportsNotRunning()
    {
        SyncProgress.Start(SyncProgress.TrackLetterboxd, "Task", "p");
        SyncProgress.Complete(SyncProgress.TrackLetterboxd);

        var snapshot = SyncProgress.GetSnapshot();
        var t = snapshot.GetType();

        Assert.Equal(false, t.GetProperty("isRunning")!.GetValue(snapshot));
        Assert.Equal("complete", t.GetProperty("phase")!.GetValue(snapshot));
    }

    [Fact]
    public void GetSnapshot_TwoTracksRunning_MergesInsteadOfClobbering()
    {
        // The bug this guards: a Letterboxd run and a Serializd run writing to one shared
        // singleton used to stomp each other's task name/counts, and one's Complete()
        // could flip the dashboard's IsRunning off while the other was still active.
        SyncProgress.Start(SyncProgress.TrackLetterboxd, "Letterboxd Sync", "Syncing films");
        SyncProgress.SetTotal(SyncProgress.TrackLetterboxd, 4);
        SyncProgress.IncrementProcessed(SyncProgress.TrackLetterboxd);

        SyncProgress.Start(SyncProgress.TrackSerializd, "Serializd TV sync", "Starting");
        SyncProgress.SetTotal(SyncProgress.TrackSerializd, 6);
        SyncProgress.IncrementProcessed(SyncProgress.TrackSerializd);
        SyncProgress.IncrementProcessed(SyncProgress.TrackSerializd);

        var s = Snapshot();
        Assert.True(Prop<bool>(s, "isRunning"));
        Assert.Equal(10, Prop<int>(s, "totalItems"));
        Assert.Equal(3, Prop<int>(s, "processedItems"));
        Assert.Contains("Letterboxd Sync", Prop<string>(s, "taskName"));
        Assert.Contains("Serializd TV sync", Prop<string>(s, "taskName"));

        // Completing one track must not clear the other's running state.
        SyncProgress.Complete(SyncProgress.TrackLetterboxd);
        var afterOneComplete = Snapshot();
        Assert.True(Prop<bool>(afterOneComplete, "isRunning"));
        Assert.Equal("Serializd TV sync", Prop<string>(afterOneComplete, "taskName"));

        SyncProgress.Complete(SyncProgress.TrackSerializd);
        Assert.False(Prop<bool>(Snapshot(), "isRunning"));
    }
}
