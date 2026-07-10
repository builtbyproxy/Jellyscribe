using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace LetterboxdSync.Serializd;

/// <summary>Daily reverse import: marks Jellyfin episodes played from each opted-in account's Serializd diary.</summary>
public class SerializdDiaryImportTask : IScheduledTask
{
    private readonly SerializdDiaryImportRunner _runner;

    public SerializdDiaryImportTask(SerializdDiaryImportRunner runner)
    {
        _runner = runner;
    }

    public string Name => "Import Serializd diary to Jellyfin";
    public string Key => "SerializdDiaryImport";
    public string Description => "Marks Jellyfin episodes played if they're on your Serializd diary";
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
