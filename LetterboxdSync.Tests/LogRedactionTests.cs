using System.Linq;
using LetterboxdSync;
using Xunit;

namespace LetterboxdSync.Tests;

public class LogRedactionTests
{
    [Theory]
    [InlineData("failed for alex as demo@example.com: 503", "failed for alex as [email]: 503")]
    [InlineData("First.Last+tv@mail.example.co.uk logged in", "[email] logged in")]
    [InlineData("two: a@example.com, b_c@jellyfin.example.", "two: [email], [email].")]
    [InlineData("(user%x@sub-domain.example.org)", "([email])")]
    public void RedactEmails_MasksEveryAddress(string line, string expected)
        => Assert.Equal(expected, LogRedaction.RedactEmails(line));

    [Theory]
    [InlineData("Logged Sinners (2025) to Letterboxd for alex as demo-cinephile")]
    [InlineData("Jellyscribe 2.10.0.0 on Jellyfin 10.11.11")]
    [InlineData("package@1.2.3 and an @mention")]
    [InlineData("")]
    public void RedactEmails_LeavesLinesWithoutAnAddressAlone(string line)
        => Assert.Equal(line, LogRedaction.RedactEmails(line));

    [Fact]
    public void RedactEmails_StaysFastOnALongLineWithNoAddress()
    {
        var line = new string('a', 200_000) + "@" + new string('b', 200_000);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(line, LogRedaction.RedactEmails(line));
        Assert.True(sw.ElapsedMilliseconds < 2000, $"took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void AccountTag_IsShortStableAndNeverTheAddress()
    {
        var tag = LogRedaction.AccountTag("Demo@Example.com ");

        Assert.Matches("^serializd-[0-9a-f]{6}$", tag);
        Assert.Equal(tag, LogRedaction.AccountTag("demo@example.com"));
        Assert.NotEqual(tag, LogRedaction.AccountTag("other@example.com"));
        Assert.DoesNotContain("demo", tag);
        Assert.Equal("serializd-none", LogRedaction.AccountTag(null));
        Assert.Equal("serializd-none", LogRedaction.AccountTag("  "));
    }
}
