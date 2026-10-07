using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LetterboxdSync;
using LetterboxdSync.Configuration;
using LetterboxdSync.Serializd;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// Smoke-tests for the IScheduledTask wrappers. These delegate straight to their
/// runners, but the metadata (Name, Key, Category, default triggers) is what
/// Jellyfin's task scheduler displays and uses, so we lock it in here.
/// </summary>
[Collection("Plugin")]
public class ScheduledTaskTests : IDisposable
{
    private readonly string _tempDir;

    public ScheduledTaskTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-tasks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var paths = Substitute.For<IApplicationPaths>();
        paths.PluginConfigurationsPath.Returns(_tempDir);
        paths.LogDirectoryPath.Returns(_tempDir);
        paths.DataPath.Returns(_tempDir);
        paths.CachePath.Returns(_tempDir);
        var xml = Substitute.For<IXmlSerializer>();
        xml.DeserializeFromFile(typeof(PluginConfiguration), Arg.Any<string>())
            .Returns(_ => new PluginConfiguration());
        new Plugin(paths, xml);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    // Progress<T> posts to the sync context asynchronously; this records inline so
    // the assertions see every report before ExecuteAsync returns.
    private sealed class RecordingProgress : IProgress<double>
    {
        public List<double> Values { get; } = new();
        public void Report(double value) => Values.Add(value);
    }

    private LetterboxdSyncRunner MakeSyncRunner(IUserManager um, ILibraryManager lm, IUserDataManager udm)
        => new(NullLoggerFactory.Instance, lm, um, udm);

    private WatchlistSyncRunner MakeWatchlistRunner(IUserManager um, ILibraryManager lm, IPlaylistManager pm)
        => new(NullLoggerFactory.Instance, lm, um, pm);

    private SerializdSyncRunner MakeSerializdSyncRunner(IUserManager um, ILibraryManager lm, IUserDataManager udm)
        => new(NullLoggerFactory.Instance, lm, um, udm);

    [Fact]
    public void SyncTask_Metadata()
    {
        var um = Substitute.For<IUserManager>();
        var lm = Substitute.For<ILibraryManager>();
        var udm = Substitute.For<IUserDataManager>();
        var task = new SyncTask(MakeSyncRunner(um, lm, udm));

        Assert.Equal("Sync watched movies to Letterboxd", task.Name);
        Assert.Equal("LetterboxdSync", task.Key);
        Assert.Equal("Jellyscribe", task.Category);
        Assert.False(string.IsNullOrEmpty(task.Description));
    }

    [Fact]
    public void SyncTask_DefaultTrigger_IsDaily()
    {
        var um = Substitute.For<IUserManager>();
        var lm = Substitute.For<ILibraryManager>();
        var udm = Substitute.For<IUserDataManager>();
        var task = new SyncTask(MakeSyncRunner(um, lm, udm));

        var triggers = task.GetDefaultTriggers().ToList();

        Assert.Equal(2, triggers.Count);
        Assert.Equal(TaskTriggerInfoType.DailyTrigger, triggers[0].Type);
        Assert.Equal(TaskTriggerInfoType.IntervalTrigger, triggers[1].Type);
    }

    [Fact]
    public async Task SyncTask_ExecuteAsync_DelegatesToRunner()
    {
        // We can't easily Substitute a concrete LetterboxdSyncRunner, so we use
        // a real one with empty users, the task just calls RunForAllAsync.
        var um = Substitute.For<IUserManager>();
        um.GetUsers().Returns(Array.Empty<Jellyfin.Database.Implementations.Entities.User>());
        var lm = Substitute.For<ILibraryManager>();
        var udm = Substitute.For<IUserDataManager>();
        var task = new SyncTask(MakeSyncRunner(um, lm, udm));

        var progress = new RecordingProgress();
        await task.ExecuteAsync(progress, CancellationToken.None);

        // The task really ran the runner: it enumerated users and finished the run.
        um.Received(1).GetUsers();
        Assert.Equal(new[] { 100d }, progress.Values);
    }

    [Fact]
    public void WatchlistSyncTask_Metadata()
    {
        var um = Substitute.For<IUserManager>();
        var lm = Substitute.For<ILibraryManager>();
        var pm = Substitute.For<IPlaylistManager>();
        var task = new WatchlistSyncTask(MakeWatchlistRunner(um, lm, pm));

        Assert.Equal("Sync Letterboxd watchlist to playlist", task.Name);
        Assert.Equal("LetterboxdWatchlistSync", task.Key);
        Assert.Equal("Jellyscribe", task.Category);
        Assert.False(string.IsNullOrEmpty(task.Description));
    }

    [Fact]
    public void WatchlistSyncTask_DefaultTrigger_IsDaily()
    {
        var um = Substitute.For<IUserManager>();
        var lm = Substitute.For<ILibraryManager>();
        var pm = Substitute.For<IPlaylistManager>();
        var task = new WatchlistSyncTask(MakeWatchlistRunner(um, lm, pm));

        var triggers = task.GetDefaultTriggers().ToList();

        Assert.Equal(2, triggers.Count);
        Assert.Equal(TaskTriggerInfoType.DailyTrigger, triggers[0].Type);
        Assert.Equal(TaskTriggerInfoType.IntervalTrigger, triggers[1].Type);
    }

    [Fact]
    public async Task WatchlistSyncTask_ExecuteAsync_DelegatesToRunner()
    {
        var um = Substitute.For<IUserManager>();
        um.GetUsers().Returns(Array.Empty<Jellyfin.Database.Implementations.Entities.User>());
        var lm = Substitute.For<ILibraryManager>();
        var pm = Substitute.For<IPlaylistManager>();
        var task = new WatchlistSyncTask(MakeWatchlistRunner(um, lm, pm));

        var progress = new RecordingProgress();
        await task.ExecuteAsync(progress, CancellationToken.None);

        um.Received(1).GetUsers();
        Assert.Equal(new[] { 100d }, progress.Values);
    }

    [Fact]
    public void SerializdSyncTask_Metadata()
    {
        var um = Substitute.For<IUserManager>();
        var lm = Substitute.For<ILibraryManager>();
        var udm = Substitute.For<IUserDataManager>();
        var task = new SerializdSyncTask(MakeSerializdSyncRunner(um, lm, udm));

        Assert.Equal("Sync watched TV to Serializd", task.Name);
        Assert.Equal("SerializdSync", task.Key);
        Assert.Equal("Jellyscribe", task.Category);
        Assert.False(string.IsNullOrEmpty(task.Description));
    }

    [Fact]
    public void SerializdSyncTask_DefaultTrigger_IsDaily()
    {
        var um = Substitute.For<IUserManager>();
        var lm = Substitute.For<ILibraryManager>();
        var udm = Substitute.For<IUserDataManager>();
        var task = new SerializdSyncTask(MakeSerializdSyncRunner(um, lm, udm));

        var triggers = task.GetDefaultTriggers().ToList();

        Assert.Equal(2, triggers.Count);
        Assert.Equal(TaskTriggerInfoType.DailyTrigger, triggers[0].Type);
        Assert.Equal(TaskTriggerInfoType.IntervalTrigger, triggers[1].Type);
    }

    [Fact]
    public async Task SerializdSyncTask_ExecuteAsync_DelegatesToRunner()
    {
        var um = Substitute.For<IUserManager>();
        um.GetUsers().Returns(Array.Empty<Jellyfin.Database.Implementations.Entities.User>());
        var lm = Substitute.For<ILibraryManager>();
        var udm = Substitute.For<IUserDataManager>();
        var task = new SerializdSyncTask(MakeSerializdSyncRunner(um, lm, udm));

        var progress = new RecordingProgress();
        await task.ExecuteAsync(progress, CancellationToken.None);

        // The task really ran the runner: it enumerated users and finished the run.
        um.Received(1).GetUsers();
        Assert.Equal(new[] { 100d }, progress.Values);
    }

    // A DailyTrigger fires at a fixed time of day; an IntervalTrigger restarts its clock on
    // every Jellyfin restart and fired all seven tasks together. Staggered so the Letterboxd
    // and Serializd families don't hit their origins in the same minute.
    [Fact]
    public void AllSyncTasks_AreStaggeredDailyTriggers()
    {
        var um = Substitute.For<IUserManager>();
        var lm = Substitute.For<ILibraryManager>();
        var udm = Substitute.For<IUserDataManager>();
        var pm = Substitute.For<IPlaylistManager>();
        var cm = Substitute.For<MediaBrowser.Controller.Collections.ICollectionManager>();

        var expected = new (IScheduledTask Task, TimeSpan TimeOfDay)[]
        {
            (new SyncTask(MakeSyncRunner(um, lm, udm)), new TimeSpan(3, 0, 0)),
            (new WatchlistSyncTask(MakeWatchlistRunner(um, lm, pm)), new TimeSpan(3, 20, 0)),
            (new DiaryImportTask(um, NullLoggerFactory.Instance, lm, udm), new TimeSpan(3, 40, 0)),
            (new SerializdSyncTask(MakeSerializdSyncRunner(um, lm, udm)), new TimeSpan(4, 0, 0)),
            (new SerializdWatchlistSyncTask(new SerializdWatchlistSyncRunner(NullLoggerFactory.Instance, lm, um, cm, pm)),
                new TimeSpan(4, 20, 0)),
            (new SerializdDiaryImportTask(new SerializdDiaryImportRunner(NullLoggerFactory.Instance, lm, um, udm)),
                new TimeSpan(4, 40, 0)),
            (new TelemetryTask(lm, Substitute.For<MediaBrowser.Controller.IServerApplicationHost>(), NullLoggerFactory.Instance), new TimeSpan(5, 0, 0)),
        };

        foreach (var (task, timeOfDay) in expected)
        {
            var triggers = task.GetDefaultTriggers().ToList();
            Assert.Equal(2, triggers.Count);
            Assert.Equal(TaskTriggerInfoType.DailyTrigger, triggers[0].Type);
            Assert.Equal(timeOfDay.Ticks, triggers[0].TimeOfDayTicks);
            // A machine that is off at that hour never fires the daily trigger; the interval
            // trigger runs the task once it has gone two days without running.
            Assert.Equal(TaskTriggerInfoType.IntervalTrigger, triggers[1].Type);
            Assert.Equal(TimeSpan.FromDays(2).Ticks, triggers[1].IntervalTicks);
        }
    }
}
