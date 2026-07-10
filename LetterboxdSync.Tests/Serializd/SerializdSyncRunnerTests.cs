using System.Collections.Generic;
using System.Linq;
using LetterboxdSync.Serializd;
using Xunit;

namespace LetterboxdSync.Tests.Serializd;

public class SerializdSyncRunnerGroupingTests
{
    private static Dictionary<(int, int), List<int>> Group(
        IEnumerable<(int, int, int)> plays, System.Func<int, int, int, bool> logged)
        => SerializdSyncRunner.GroupNewEpisodes(plays, logged);

    [Fact]
    public void GroupNewEpisodes_GroupsPerShowAndSeason_Sorted()
    {
        var plays = new[] { (77236, 1, 3), (77236, 1, 1), (77236, 2, 5), (1396, 1, 1) };
        var g = Group(plays, (_, _, _) => false);

        Assert.Equal(new[] { 1, 3 }, g[(77236, 1)]);
        Assert.Equal(new[] { 5 }, g[(77236, 2)]);
        Assert.Equal(new[] { 1 }, g[(1396, 1)]);
    }

    [Fact]
    public void GroupNewEpisodes_SkipsAlreadyLogged()
    {
        var plays = new[] { (77236, 1, 1), (77236, 1, 2), (77236, 1, 3) };
        // Pretend E2 already logged.
        var g = Group(plays, (show, season, ep) => show == 77236 && season == 1 && ep == 2);

        Assert.Equal(new[] { 1, 3 }, g[(77236, 1)]);
    }

    [Fact]
    public void GroupNewEpisodes_DeduplicatesRepeatedPlays()
    {
        var plays = new[] { (77236, 1, 1), (77236, 1, 1), (77236, 1, 1) };
        var g = Group(plays, (_, _, _) => false);
        Assert.Equal(new[] { 1 }, g[(77236, 1)]);
    }

    [Fact]
    public void GroupNewEpisodes_AllLogged_ReturnsEmpty()
    {
        var plays = new[] { (77236, 1, 1), (77236, 1, 2) };
        var g = Group(plays, (_, _, _) => true);
        Assert.Empty(g);
    }
}
