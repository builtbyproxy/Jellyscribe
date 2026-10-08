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
/// Most styles and code live in the shared jellyscribe.css and jellyscribe.js, so each page is checked
/// as the browser sees it: the shared stylesheet followed by the page's own rules.
/// </summary>
public class DashboardMarkupTests
{
    public static IEnumerable<object[]> Pages() => new[]
    {
        new object[] { "configPage.html", "#letterboxdSyncConfigPage" },
        new object[] { "userPage.html", "#letterboxdUserPage" },
    };

    public static IEnumerable<object[]> Files() => Pages().Select(p => new[] { p[0] });

    private static string Read(string file)
    {
        var asm = typeof(Plugin).Assembly;
        var resource = asm.GetManifestResourceNames().Single(n => n.EndsWith(".Web." + file, StringComparison.Ordinal));
        using var reader = new StreamReader(asm.GetManifestResourceStream(resource)!);
        return reader.ReadToEnd();
    }

    // The page's own <style> block.
    private static string PageStyle(string page)
    {
        var start = page.IndexOf("<style>", StringComparison.Ordinal) + "<style>".Length;
        return page.Substring(start, page.IndexOf("</style>", start, StringComparison.Ordinal) - start);
    }

    // What styles the page: the shared stylesheet, then the page's own rules (which come later).
    private static string Style(string file) => Read("jellyscribe.css") + "\n" + PageStyle(Read(file));

    // The page's code: its own script and the shared one.
    private static string Code(string file) => Read(file) + "\n" + Read("jellyscribe.js");

    // The declarations of every rule (outside or inside a media query) whose selector list names
    // `selector` exactly, in source order.
    private static List<string> Rules(string style, string selector)
    {
        style = Regex.Replace(style, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Matches(style, @"([^{}]+)\{([^{}]*)\}")
            .Where(m => m.Groups[1].Value.Split(',').Select(x => x.Trim()).Contains(selector))
            .Select(m => m.Groups[2].Value).ToList();
    }

    private static string Rule(string style, string selector)
    {
        var rules = Rules(style, selector);
        Assert.True(rules.Count > 0, "no rule for " + selector);
        return string.Join(";", rules);
    }

    // The colour tokens a selector sets; a later rule overrides an earlier one.
    private static Dictionary<string, string> Tokens(string style, string selector)
    {
        var tokens = new Dictionary<string, string>();
        foreach (Match m in Regex.Matches(Rule(style, selector), @"(--ws-[a-z0-9-]+):\s*(#[0-9a-fA-F]{6})"))
            tokens[m.Groups[1].Value] = m.Groups[2].Value;
        return tokens;
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
        var style = Style(file);
        var dark = Tokens(style, root);
        var light = dark.ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var kv in Tokens(style, root + ".ws-light")) light[kv.Key] = kv.Value;

        var text = new[] { "--ws-text", "--ws-muted", "--ws-faint", "--ws-primary-text", "--ws-ok", "--ws-warn", "--ws-crit", "--ws-film", "--ws-tv", "--ws-info", "--ws-requested" };
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
        var style = Style(file);
        var primary = Regex.Match(Rule(style, root + " .ws-btn.primary"), @"background: var\(--ws-primary\);[^}]*color: (#[0-9a-fA-F]{6});");
        Assert.True(primary.Success, "primary button rule not found");
        Assert.True(Contrast(primary.Groups[1].Value, Tokens(style, root)["--ws-primary"]) >= 4.5);
        var disabled = Rule(style, root + " .ws-btn:disabled");
        var opacity = Regex.Match(disabled, @"opacity:\s*([\d.]+)");
        Assert.True(opacity.Success && double.Parse(opacity.Groups[1].Value, CultureInfo.InvariantCulture) <= 0.6, "disabled buttons must be visibly dimmed");
        Assert.Contains("cursor: not-allowed", disabled, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void Styles_NeverTargetJellyfinsDocument_NorFollowTheOsTheme(string file, string root)
    {
        // Every rule is scoped to a dashboard root (or is a font face or keyframes); nothing styles html or
        // body. The page's own rules name only its own root; the shared sheet names only the two roots.
        foreach (var (css, roots) in new[] { (PageStyle(Read(file)), new[] { root }), (Read("jellyscribe.css"), new[] { "#letterboxdSyncConfigPage", "#letterboxdUserPage" }) })
        {
            var style = Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);
            var selectors = Regex.Matches(style, @"(?:^|[{}])\s*([^{}@][^{}]*?)\s*\{")
                .Select(m => m.Groups[1].Value.Trim())
                .Where(s => s.Length > 0 && !s.StartsWith("@", StringComparison.Ordinal) && !Regex.IsMatch(s, @"^(\d+%|from|to)(,|$)"));
            var unscoped = selectors.SelectMany(s => s.Split(',')).Select(s => s.Trim())
                .Where(s => !roots.Any(r => s == r || s.StartsWith(r + " ", StringComparison.Ordinal) || s.StartsWith(r + ".", StringComparison.Ordinal))).ToList();
            Assert.True(unscoped.Count == 0, "unscoped selectors: " + string.Join(" | ", unscoped));
        }
        var all = Style(file);
        // The palette follows Jellyfin's theme (applyTheme), with the OS only as the fallback outside Jellyfin.
        Assert.DoesNotContain("prefers-color-scheme", all, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void PhoneLayout_CanShrinkToTheScreen(string file, string root)
    {
        var style = Style(file);
        var phone = style.Substring(style.IndexOf("@media (max-width: 820px)", StringComparison.Ordinal));
        phone = phone.Substring(0, phone.IndexOf("\n}", StringComparison.Ordinal));
        Assert.Contains("grid-template-columns: minmax(0,1fr);", Rule(phone, root + " .ws"), StringComparison.Ordinal);
        foreach (var part in new[] { " .ws-rail", " .ws-rail-inner", " .ws-nav" })
            Assert.Contains(Rules(phone, root + part), r => r.Trim() == "min-width: 0;");
        // The page's own rules never set the grid's columns, which would beat the phone rule above.
        Assert.DoesNotContain(Rules(PageStyle(Read(file)), root + " .ws"), r => r.Contains("grid-template-columns", StringComparison.Ordinal));
        // The When column is hidden on phones, so a short date goes into each row's title line.
        Assert.Contains("display: inline;", Rule(style, root + " .ws-tc .ws-mwhen"), StringComparison.Ordinal);
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

        // The diary login sits on Jellyfin's own origin, where the browser keeps the Jellyfin login, which must
        // not be filled here or it would be sent to the diary. new-password is what stops the browser filling
        // it (some browsers ignore autocomplete="off" on the login field); current-password would invite it.
        Assert.Contains("id=\"mUsername\" autocomplete=\"off\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"mPassword\" autocomplete=\"new-password\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("current-password", page, StringComparison.Ordinal);
        Assert.Contains("outline: 2px solid var(--ws-focus);", Rule(Style(file), root + " :focus-visible"), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void ActivityList_CanBeSearched_AndPagedToItsEnd(string file)
    {
        var page = Code(file);
        Assert.Contains("id=\"historySearch\" placeholder=\"Search titles\" aria-label=\"Search activity by title\"", page, StringComparison.Ordinal);
        Assert.Contains("<button type=\"button\" class=\"ws-btn\" id=\"historyMore\">Load older history</button>", page, StringComparison.Ordinal);

        // GET History serves at most 200 Letterboxd events a page; asking for more silently got 200
        // and the list stopped there. Each page asks for what the server gives, then pages by offset.
        Assert.Matches(@"histChunk: 200,", page);
        Assert.DoesNotMatch(@"History[^\n]*count(=|: )250", page);
        Assert.Matches(@"offset(: |=' \+ )h\.loaded", page);
        // The merged list stops at the oldest loaded event of a service with more to load, and an event
        // that a later page repeats is shown once.
        Assert.Contains("all = this.visibleEvents(f).filter(", page, StringComparison.Ordinal);
        Assert.Contains("if (self.seen[k]) return false;", page, StringComparison.Ordinal);

        // The group header of a binge is a button that says whether it is open, and every value it
        // shows (show name, first and last episode) goes through the page's escaper.
        var at = page.IndexOf("groupHtml: function", StringComparison.Ordinal);
        Assert.True(page.IndexOf("groupHtml: function", at + 1, StringComparison.Ordinal) < 0, "groupHtml is defined once, in jellyscribe.js");
        Assert.True(at >= 0, "no groupHtml");
        var groupHtml = Regex.Match(page.Substring(at), @"^groupHtml: function[\s\S]*?\n\s*\},").Value;
        Assert.NotEmpty(groupHtml);
        Assert.Matches(@"self\.esc(Attr)?\(u\.show\)", groupHtml);
        Assert.Matches(@"self\.esc(Attr)?\(range\)", groupHtml);
        Assert.Contains("class=\"ws-grp-btn ws-tc\" data-grp=\"' + id + '\" aria-expanded=\"false\"", page, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void AccountDialog_NamesTheServicesPlainly_AndRewordsTheWatchlistToggleWhenTheServiceChanges(string file)
    {
        var page = Code(file);
        Assert.Contains("<option value=\"serializd\">Serializd: TV</option><option value=\"letterboxd\">Letterboxd: Film</option>", page, StringComparison.Ordinal);
        // A new account opens as Serializd; switching it to Letterboxd must reword the watchlist toggle too.
        var at = page.IndexOf("onSvcChange: function", StringComparison.Ordinal);
        Assert.True(at >= 0, "no onSvcChange");
        var end = new[] { "fillSecret: function", "openAccount: function", "collectModal: function" }
            .Select(n => page.IndexOf(n, at, StringComparison.Ordinal)).Where(i => i > at).Min();
        Assert.Contains("watchDesc.textContent = this.watchDesc(svc)", page.Substring(at, end - at), StringComparison.Ordinal);
        Assert.Matches(@"'chkWatch', svc === 'serializd' \? !!a\.\w+ : !!a\.\w+, this\.watchDesc\(svc\)\]", page);
    }

    [Fact]
    public void AdminPage_OffersThePromisedTelemetryAndLogBundlePreviews()
    {
        // README, the telemetry and log-bundle specs, and the bug report form all send admins to these.
        var page = Read("configPage.html");
        foreach (var id in new[] { "telemetryPreviewBtn", "telemetryRegenBtn", "telemetryRegenYes", "telemetryCopyBtn",
                     "telemetryCopyRegenBtn", "sendLogsPreview", "telemetryNotice", "telemetryNoticeEnable", "telemetryNoticeDismiss" })
            Assert.Contains($"id=\"{id}\"", page, StringComparison.Ordinal);
        Assert.Contains("<dialog class=\"ws-modal-ov\" id=\"telemetryModal\" aria-labelledby=\"telemetryModalTitle\">", page, StringComparison.Ordinal);
        Assert.Contains("<dialog class=\"ws-modal-ov\" id=\"logsPreviewModal\" aria-labelledby=\"logsPreviewTitle\">", page, StringComparison.Ordinal);
        foreach (var endpoint in new[] { "Telemetry/Preview'", "Telemetry/PreviewLogs'", "Telemetry/RegenerateId'" })
            Assert.Contains(endpoint, page, StringComparison.Ordinal);
        // The note goes in a POST body, never in the URL.
        Assert.Contains("Telemetry/PreviewLogs'), type: 'POST'", page, StringComparison.Ordinal);
        // The notice is answered through the stored flag, and opens hidden until the config says to show it.
        Assert.Contains("BannerDismissed", page, StringComparison.Ordinal);
        Assert.Matches("id=\"telemetryNotice\"[^>]* hidden>", page);
        // The consent text no longer promises what the payload does not keep.
        Assert.DoesNotContain("exact numbers", page, StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewResult_SaysWhenAReviewJoinedTheExistingEntry_AndShowsTheServersNoteAsText()
    {
        // Both dashboards post reviews through the shared review code; a per-account addedToEntry and
        // note are optional, and the note is the server's text, so it goes through the escaper.
        var js = Read("jellyscribe.js");
        var at = js.IndexOf("reviewAccountNotes: function", StringComparison.Ordinal);
        Assert.True(at >= 0, "no reviewAccountNotes");
        var next = js.IndexOf(": function", at + "reviewAccountNotes: function".Length, StringComparison.Ordinal);
        var body = next < 0 ? js.Substring(at) : js.Substring(at, next - at);
        Assert.Contains("a.addedToEntry === true", body, StringComparison.Ordinal);
        Assert.Contains("Added to the existing diary entry.", body, StringComparison.Ordinal);
        Assert.Contains("self.esc(a.note)", body, StringComparison.Ordinal);
        Assert.Contains("self.esc(a.letterboxdUsername)", body, StringComparison.Ordinal);
        Assert.Contains("return line + self.reviewAccountNotes(d.accounts || []);", js, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void Page_FindsItsRootByIdWhenRunWithoutCurrentScript(string file, string root)
    {
        // Jellyfin's dashboard runs a configuration page's script through jQuery, where
        // document.currentScript is null: the root then comes from the document, before byId exists.
        var page = Read(file);
        var line = Regex.Match(page, @"var root = [^\n]*").Value;
        Assert.EndsWith("|| document.getElementById('" + root.Substring(1) + "');", line, StringComparison.Ordinal);
    }
}
