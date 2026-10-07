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
    [InlineData("POST /login?username=demo%40example.com&x=1", "POST /login?username=[email]&x=1")]
    [InlineData("POST /login?username=demo%40EXAMPLE.com", "POST /login?username=[email]")]
    [InlineData("{\"email\":\"demo\\u0040example.com\"}", "{\"email\":\"[email]\"}")]
    [InlineData("<a>demo&#64;example.com</a>", "<a>[email]</a>")]
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
