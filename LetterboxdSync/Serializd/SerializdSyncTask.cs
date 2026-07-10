using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace LetterboxdSync.Serializd;

/// <summary>
/// Daily catch-up that logs Jellyfin-watched TV episodes to Serializd, picking up anything
/// the real-time playback handler missed. Auto-discovered by Jellyfin as an IScheduledTask;
/// its <see cref="SerializdSyncRunner"/> dependency is registered in
/// <see cref="ServiceRegistrator"/>.
/// </summary>
public class SerializdSyncTask : IScheduledTask
{
    private readonly SerializdSyncRunner _runner;

    public SerializdSyncTask(SerializdSyncRunner runner)
    {
        _runner = runner;
    }

    public string Name => "Sync watched TV to Serializd";
    public string Key => "SerializdSync";
    public string Description => "Logs your Jellyfin TV watch history to your Serializd account";
    public string Category => "Letterboxd";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _runner.RunForAllAsync(progress, "scheduled", cancellationToken);

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromDays(1).Ticks,
        },
    };
}
