using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// Guards the embedded config pages against element attributes that browser content blockers
/// hide on sight.
/// <para>
/// Issue #112: the Raw cookies input carried <c>id="mCookies"</c>. Privacy and anti-annoyance
/// filter lists (Ghostery, uBlock annoyance lists, AdGuard, Brave shields) ship generic cosmetic
/// rules along the lines of <c>[id*="cookie" i]</c> to remove consent banners, and those matched
/// it. The label sat outside the input and still rendered, so the field looked simply absent, with
/// nothing in the console. The reporter only found it by disabling extensions one at a time.
/// </para>
/// <para>
/// That failure is silent and self-inflicted, and it lands on the exact control we tell
/// Cloudflare- and 2FA-blocked users to reach for, so an affected user concludes the feature does
/// not exist rather than filing a bug. Visible label text is fine and should stay clear; it is the
/// attributes that need to be unremarkable.
/// </para>
/// </summary>
public class WebAssetFilterSafetyTests
{
    /// <summary>
    /// Substrings that generic cosmetic filter rules commonly target. Deliberately short: this is
    /// about attribute values a blocker might match, not about page copy.
    /// </summary>
    private static readonly string[] BlockerMagnets =
    {
        "cookie", "consent", "gdpr", "banner", "advert", "sponsor", "tracking", "popup"
    };

    public static IEnumerable<object[]> EmbeddedWebAssets()
    {
        var asm = typeof(Plugin).Assembly;
        foreach (var name in asm.GetManifestResourceNames()
                     .Where(n => n.Contains(".Web.", StringComparison.Ordinal)
                                 && (n.EndsWith(".html", StringComparison.Ordinal)
                                     || n.EndsWith(".js", StringComparison.Ordinal))))
        {
            yield return new object[] { name };
        }
    }

    [Theory]
    [MemberData(nameof(EmbeddedWebAssets))]
    public void EmbeddedPage_HasNoIdOrClassAContentBlockerWouldHide(string resourceName)
    {
        var asm = typeof(Plugin).Assembly;
        using var stream = asm.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var content = reader.ReadToEnd();

        // id="..." / class="..." only. Page copy and placeholders may say "cookie" freely, and
        // should, because that is what the user is being asked for.
        var attributes = Regex.Matches(content, "(?:id|class)\\s*=\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value);

        var offenders = new List<string>();
        foreach (var value in attributes)
        {
            foreach (var magnet in BlockerMagnets)
            {
                if (value.Contains(magnet, StringComparison.OrdinalIgnoreCase))
                    offenders.Add($"{value} (matched \"{magnet}\")");
            }
        }

        Assert.True(offenders.Count == 0,
            $"{resourceName} has element attributes a content blocker is likely to hide, which fails "
            + "silently with no console error (issue #112): "
            + string.Join(", ", offenders.Distinct())
            + ". Rename the attribute; the visible label can still say what it means.");
    }
}
