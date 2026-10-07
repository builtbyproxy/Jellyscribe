using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MediaBrowser.Model.Tasks;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// The admin dashboard's Sync buttons start the plugin's scheduled tasks by their Key. A renamed
/// task would make a button report "its scheduled task was not found", so the keys the page names
/// are pinned to the tasks the plugin registers.
/// </summary>
public class DashboardTaskKeysTests
{
    [Fact]
    public void AdminDashboard_StartsOnlyScheduledTasksThePluginRegisters_ForBothServices()
    {
        var asm = typeof(Plugin).Assembly;
        var registered = asm.GetTypes()
            .Where(t => typeof(IScheduledTask).IsAssignableFrom(t) && !t.IsAbstract)
            // Key is a constant per task; no constructor (and none of its dependencies) is needed to read it.
            .Select(t => ((IScheduledTask)RuntimeHelpers.GetUninitializedObject(t)).Key)
            .ToHashSet();

        var resource = asm.GetManifestResourceNames().Single(n => n.EndsWith(".Web.configPage.html", System.StringComparison.Ordinal));
        using var reader = new StreamReader(asm.GetManifestResourceStream(resource)!);
        var page = reader.ReadToEnd();
        var named = Regex.Matches(page, @"key: '([A-Za-z]+)'").Select(m => m.Groups[1].Value).ToHashSet();

        Assert.Equal(new[] { "LetterboxdSync", "LetterboxdWatchlistSync", "SerializdSync", "SerializdWatchlistSync" }, named.OrderBy(k => k));
        Assert.All(named, key => Assert.Contains(key, registered));
    }
}
