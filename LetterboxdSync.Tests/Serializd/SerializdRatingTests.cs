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
    // 7.5 and 0.5 both happen to land on the same result under banker's rounding too (8 and
    // 0-then-clamped-to-1), so neither actually pins AwayFromZero; 6.5 does (would round to 6
    // under ToEven, since 6 is the even neighbor), confirmed by mutation-testing this suite.
    [InlineData(6.5, 7)]        // round away from zero, distinguishes from banker's rounding
    [InlineData(0.5, 1)]        // smallest real rating rounds up to 1★-half
    public void FromJellyfin_MapsAndClamps(double? input, int? expected)
    {
        Assert.Equal(expected, SerializdRating.FromJellyfin(input));
    }
}
