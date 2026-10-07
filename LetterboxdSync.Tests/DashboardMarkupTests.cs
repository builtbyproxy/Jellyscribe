using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// Pins the accessibility and layout rules of both dashboards (the admin settings page and the user
/// page), which have no JS test harness of their own: their colours meet WCAG AA contrast in both
/// palettes, their styles stay inside the page instead of restyling Jellyfin's document, the phone
/// layout can shrink to the screen, and every control can be reached and named by assistive tech.
/// </summary>
public class DashboardMarkupTests
{
    public static IEnumerable<object[]> Pages() => new[]
    {
        new object[] { "configPage.html", "#letterboxdSyncConfigPage" },
        new object[] { "userPage.html", "#letterboxdUserPage" },
    };

    private static string Read(string file)
    {
        var asm = typeof(Plugin).Assembly;
        var resource = asm.GetManifestResourceNames().Single(n => n.EndsWith(".Web." + file, StringComparison.Ordinal));
        using var reader = new StreamReader(asm.GetManifestResourceStream(resource)!);
        return reader.ReadToEnd();
    }

    private static string Style(string page)
    {
        var start = page.IndexOf("<style>", StringComparison.Ordinal);
        return page.Substring(start, page.IndexOf("</style>", start, StringComparison.Ordinal) - start);
    }

    // The declarations of the rule whose selector is exactly `selector`.
    private static Dictionary<string, string> Tokens(string style, string selector)
    {
        var at = style.IndexOf(selector + " {", StringComparison.Ordinal);
        Assert.True(at >= 0, "no rule for " + selector);
        var body = style.Substring(at, style.IndexOf('}', at) - at);
        return Regex.Matches(body, @"(--ws-[a-z0-9-]+):\s*(#[0-9a-fA-F]{6})")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
    }

    private static double Luminance(string hex)
    {
        double Channel(int i)
        {
            var c = int.Parse(hex.Substring(i, 2), NumberStyles.HexNumber) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(1) + 0.7152 * Channel(3) + 0.0722 * Channel(5);
    }

    private static double Contrast(string a, string b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void TextAndStatusColours_ReachAA_OnEverySurface_InBothPalettes(string file, string root)
    {
        var style = Style(Read(file));
        var dark = Tokens(style, root);
        var light = dark.ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var kv in Tokens(style, root + ".ws-light")) light[kv.Key] = kv.Value;

        var text = new[] { "--ws-text", "--ws-muted", "--ws-faint", "--ws-ok", "--ws-warn", "--ws-crit", "--ws-film", "--ws-tv", "--ws-info", "--ws-requested" };
        var surfaces = new[] { "--ws-bg", "--ws-surface", "--ws-surface-2" };
        var failures = new List<string>();
        foreach (var (name, palette) in new[] { ("dark", dark), ("light", light) })
            foreach (var t in text)
                foreach (var s in surfaces)
                {
                    var ratio = Contrast(palette[t], palette[s]);
                    if (ratio < 4.5) failures.Add($"{name} {t} {palette[t]} on {s} {palette[s]}: {ratio:0.00}");
                }
        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void PrimaryButton_HasDarkTextOnGold_AndDisabledButtonsLookDisabled(string file, string root)
    {
        var style = Style(Read(file));
        var primary = Regex.Match(style, Regex.Escape(root) + @" \.ws-btn\.primary \{[^}]*background: var\(--ws-primary\);[^}]*color: (#[0-9a-fA-F]{6});");
        Assert.True(primary.Success, "primary button rule not found");
        Assert.True(Contrast(primary.Groups[1].Value, Tokens(style, root)["--ws-primary"]) >= 4.5);
        Assert.Matches(Regex.Escape(root) + @" \.ws-btn:disabled \{ opacity: \.45; cursor: not-allowed; \}", style);
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void Styles_NeverTargetJellyfinsDocument_NorFollowTheOsTheme(string file, string root)
    {
        var style = Regex.Replace(Style(Read(file)).Substring("<style>".Length), @"/\*.*?\*/", "", RegexOptions.Singleline);
        // Every rule is scoped to the page root (or is a font face or keyframes); nothing styles html or body.
        var selectors = Regex.Matches(style, @"(?:^|[{}])\s*([^{}@][^{}]*?)\s*\{")
            .Select(m => m.Groups[1].Value.Trim())
            .Where(s => s.Length > 0 && !s.StartsWith("@", StringComparison.Ordinal) && !Regex.IsMatch(s, @"^(\d+%|from|to)(,|$)"));
        var unscoped = selectors.SelectMany(s => s.Split(',')).Select(s => s.Trim()).Where(s => !s.StartsWith(root, StringComparison.Ordinal)).ToList();
        Assert.True(unscoped.Count == 0, "unscoped selectors: " + string.Join(" | ", unscoped));
        // The palette follows Jellyfin's theme (applyTheme), with the OS only as the fallback outside Jellyfin.
        Assert.DoesNotContain("prefers-color-scheme", style, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void PhoneLayout_CanShrinkToTheScreen(string file, string root)
    {
        var style = Style(Read(file));
        var phone = style.Substring(style.IndexOf("@media (max-width: 820px)", StringComparison.Ordinal));
        Assert.Contains(root + " .ws { grid-template-columns: minmax(0,1fr);", phone, StringComparison.Ordinal);
        Assert.Contains(root + " .ws-rail, " + root + " .ws-rail-inner, " + root + " .ws-nav { min-width: 0; }", phone, StringComparison.Ordinal);
        // The When column is hidden on phones, so a short date goes into each row's title line.
        Assert.Contains(root + " .ws-tc .ws-mwhen { display: inline; }", style, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void Controls_AreKeyboardReachable_AndNamed(string file, string root)
    {
        var page = Read(file);
        var navStart = page.IndexOf("<nav class=\"ws-nav\" id=\"wsNav\">", StringComparison.Ordinal);
        var nav = page.Substring(navStart, page.IndexOf("</nav>", navStart, StringComparison.Ordinal) - navStart);
        Assert.DoesNotContain("<a ", nav, StringComparison.Ordinal);
        Assert.Matches("<button type=\"button\" class=\"active\" aria-current=\"page\" data-sec=\"overview\">", nav);

        Assert.Contains("<dialog class=\"ws-modal-ov\" id=\"acctModal\" aria-labelledby=\"acctModalTitle\">", page, StringComparison.Ordinal);
        Assert.Contains("<dialog class=\"ws-modal-ov\" id=\"reviewModal\" aria-labelledby=\"reviewTitle\">", page, StringComparison.Ordinal);
        Assert.Contains("<div id=\"starRating\" role=\"slider\" tabindex=\"0\" aria-labelledby=\"ratingField\"", page, StringComparison.Ordinal);

        var fieldLabels = Regex.Matches(page, "<label class=\"ws-field\"[^>]*>").Select(m => m.Value).ToList();
        Assert.NotEmpty(fieldLabels);
        Assert.All(fieldLabels, l => Assert.Contains(" for=\"", l, StringComparison.Ordinal));

        // Password managers see a login form; the saved password is still never filled in (write-only).
        Assert.Contains("id=\"mUsername\" autocomplete=\"username\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"mPassword\" autocomplete=\"current-password\"", page, StringComparison.Ordinal);
        Assert.Contains(root + " :focus-visible { outline: 2px solid var(--ws-focus);", page, StringComparison.Ordinal);
    }
}
