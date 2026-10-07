using System;
using System.Collections.Generic;
using MediaBrowser.Model.Tasks;

namespace LetterboxdSync;

/// <summary>
/// Default triggers for the plugin's daily tasks, staggered so the Letterboxd and Serializd
/// families never start in the same minute.
/// </summary>
internal static class TaskSchedule
{
    internal static readonly TimeSpan LetterboxdDiarySync = new(3, 0, 0);
    internal static readonly TimeSpan LetterboxdWatchlist = new(3, 20, 0);
    internal static readonly TimeSpan LetterboxdDiaryImport = new(3, 40, 0);
    internal static readonly TimeSpan SerializdSync = new(4, 0, 0);
    internal static readonly TimeSpan SerializdWatchlist = new(4, 20, 0);
    internal static readonly TimeSpan SerializdDiaryImport = new(4, 40, 0);
    internal static readonly TimeSpan Telemetry = new(5, 0, 0);

    /// <summary>
    /// How long a task may go without running before the fallback trigger runs it anyway.
    /// </summary>
    internal static readonly TimeSpan Fallback = TimeSpan.FromDays(2);

    /// <summary>
    /// A daily trigger at <paramref name="timeOfDay"/> (server time), which a restart does not
    /// push back the way it re-arms an interval trigger, plus an interval trigger as a fallback:
    /// a daily trigger never fires on a machine that is always off or asleep at that hour, and
    /// the interval trigger then runs the task once it has gone <see cref="Fallback"/> without
    /// running. While the daily trigger keeps running the task, the fallback never comes due.
    /// </summary>
    internal static IEnumerable<TaskTriggerInfo> Daily(TimeSpan timeOfDay) => new[]
    {
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = timeOfDay.Ticks,
        },
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = Fallback.Ticks,
        },
    };
}
