using LetterboxdSync.Serializd;
using Xunit;

namespace LetterboxdSync.Tests.Serializd;

public class SerializdRatingTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData(0.0, null)]     // unrated
    [InlineData(-1.0, null)]    // guard against negatives
    [InlineData(8.0, 8)]
    [InlineData(10.0, 10)]
    [InlineData(11.0, 10)]      // clamp to 10
    [InlineData(7.4, 7)]        // round
    [InlineData(7.5, 8)]        // round away from zero
    [InlineData(0.5, 1)]        // smallest real rating rounds up to 1★-half
    public void FromJellyfin_MapsAndClamps(double? input, int? expected)
    {
        Assert.Equal(expected, SerializdRating.FromJellyfin(input));
    }
}
