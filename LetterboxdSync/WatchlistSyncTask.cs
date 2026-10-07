using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace LetterboxdSync;

public class WatchlistSyncTask : IScheduledTask
{
    private readonly WatchlistSyncRunner _runner;

    public WatchlistSyncTask(WatchlistSyncRunner runner)
    {
        _runner = runner;
    }

    public string Name => "Sync Letterboxd watchlist to playlist";
    public string Key => "LetterboxdWatchlistSync";
    public string Description => "Creates a Jellyfin playlist from your Letterboxd watchlist";
    public string Category => "Jellyscribe";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _runner.RunForAllAsync(progress, "scheduled", cancellationToken);

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => TaskSchedule.Daily(TaskSchedule.LetterboxdWatchlist);
}
