using System.Collections.Generic;
using LetterboxdSync.Serializd;
using Xunit;

namespace LetterboxdSync.Tests.Serializd;

public class SerializdEpisodeMapperTests
{
    [Fact]
    public void Build_SingleEpisode_ReturnsOneNumber()
    {
        var r = SerializdEpisodeMapper.Build(1396, 1, 3, null);
        Assert.NotNull(r);
        Assert.Equal(1396, r!.ShowTmdbId);
        Assert.Equal(1, r.SeasonNumber);
        Assert.Equal(new[] { 3 }, r.EpisodeNumbers);
    }

    [Fact]
    public void Build_MultiEpisodeFile_ExpandsInclusiveRange()
    {
        var r = SerializdEpisodeMapper.Build(1396, 2, 5, 7);
        Assert.NotNull(r);
        Assert.Equal(new[] { 5, 6, 7 }, r!.EpisodeNumbers);
    }

    [Fact]
    public void Build_Specials_SeasonZeroIsAllowed()
    {
        var r = SerializdEpisodeMapper.Build(1396, 0, 1, null);
        Assert.NotNull(r);
        Assert.Equal(0, r!.SeasonNumber);
    }

    [Theory]
    [InlineData(null, 1, 1)]   // no series tmdb id
    [InlineData(0, 1, 1)]      // non-positive series tmdb id
    [InlineData(1396, null, 1)] // no season number
    [InlineData(1396, -1, 1)]  // negative season number
    [InlineData(1396, 1, null)] // no episode number
    [InlineData(1396, 1, 0)]   // non-positive episode number
    public void Build_MissingOrInvalidIds_ReturnsNull(int? seriesTmdb, int? season, int? episode)
    {
        Assert.Null(SerializdEpisodeMapper.Build(seriesTmdb, season, episode, null));
    }

    [Fact]
    public void Build_EndBeforeStart_IgnoresEnd()
    {
        var r = SerializdEpisodeMapper.Build(1396, 1, 5, 3);
        Assert.Equal(new[] { 5 }, r!.EpisodeNumbers);
    }

    [Fact]
    public void Build_AbsurdlyWideRange_FallsBackToSingleEpisode()
    {
        // A whole-season file with IndexNumberEnd set to the finale must not fan out
        // into dozens of spurious logs.
        var r = SerializdEpisodeMapper.Build(1396, 1, 1, 500);
        Assert.Equal(new[] { 1 }, r!.EpisodeNumbers);
    }
}
