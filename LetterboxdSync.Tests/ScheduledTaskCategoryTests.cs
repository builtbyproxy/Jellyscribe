using System;
using System.Linq;
using System.Runtime.CompilerServices;
using MediaBrowser.Model.Tasks;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// Every scheduled task the plugin ships must appear under one "Jellyscribe" heading in
/// Jellyfin's Scheduled Tasks page. The rebrand renamed only the sidebar task, leaving seven
/// (including the Serializd ones) under "Letterboxd". This covers every IScheduledTask in the
/// assembly, so a task added later cannot slip back to another heading.
/// </summary>
public class ScheduledTaskCategoryTests
{
    [Fact]
    public void EveryScheduledTask_IsCategorisedAsJellyscribe()
    {
        var taskTypes = typeof(Plugin).Assembly.GetTypes()
            .Where(t => typeof(IScheduledTask).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
            .ToList();

        Assert.True(taskTypes.Count >= 8, $"expected at least 8 scheduled tasks, found {taskTypes.Count}");
        foreach (var type in taskTypes)
        {
            // Category is a constant expression-bodied property, so an uninitialised instance
            // reads it without wiring each task's Jellyfin dependencies.
            var task = (IScheduledTask)RuntimeHelpers.GetUninitializedObject(type);
            Assert.True(task.Category == "Jellyscribe", $"{type.Name} is categorised as \"{task.Category}\"");
        }
    }
}
