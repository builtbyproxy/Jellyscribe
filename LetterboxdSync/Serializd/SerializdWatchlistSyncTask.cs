using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace LetterboxdSync.Serializd;

/// <summary>Daily mirror of each opted-in Serializd account's watchlist into a Jellyfin playlist.</summary>
public class SerializdWatchlistSyncTask : IScheduledTask
{
    private readonly SerializdWatchlistSyncRunner _runner;

    public SerializdWatchlistSyncTask(SerializdWatchlistSyncRunner runner)
    {
        _runner = runner;
    }

    public string Name => "Sync Serializd watchlist to playlist";
    public string Key => "SerializdWatchlistSync";
    public string Description => "Mirrors your Serializd watchlist into a Jellyfin playlist";
    public string Category => "Letterboxd";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _runner.RunForAllAsync(progress, cancellationToken);

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromDays(1).Ticks,
        },
    };
}
