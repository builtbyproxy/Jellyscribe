using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using LetterboxdSync.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// Serves the embedded sidebar.js asset. The script is compiled into the plugin
/// assembly as an embedded resource, so the happy path returns it as JavaScript.
/// </summary>
public class SidebarControllerTests
{
    [Fact]
    public void GetSidebarJs_EmbeddedResourcePresent_ReturnsJavaScriptFile()
    {
        var controller = new SidebarController();

        var result = controller.GetSidebarJs();

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("application/javascript", file.ContentType);
        Assert.True(file.FileStream.Length > 0, "embedded sidebar.js should not be empty");
    }

    /// <summary>
    /// sidebar.js is served anonymously and now injected on every install's login page, so it must
    /// stay the static embedded file: byte-identical to the resource, no per-user or config data.
    /// </summary>
    [Fact]
    public void GetSidebarJs_ServesTheEmbeddedResourceVerbatim()
    {
        var controller = new LetterboxdSync.Api.SidebarController();
        var file = Assert.IsType<FileStreamResult>(controller.GetSidebarJs());
        using var served = new System.IO.MemoryStream();
        file.FileStream.CopyTo(served);

        using var resource = typeof(LetterboxdSync.Api.SidebarController).Assembly.GetManifestResourceStream("LetterboxdSync.Web.sidebar.js")!;
        using var expected = new System.IO.MemoryStream();
        resource.CopyTo(expected);

        Assert.Equal(expected.ToArray(), served.ToArray());
    }

    private static SidebarController WithRequest()
        => new() { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

    private static byte[] Resource(string file)
    {
        using var resource = typeof(SidebarController).Assembly.GetManifestResourceStream("LetterboxdSync.Web." + file);
        Assert.NotNull(resource);
        using var bytes = new MemoryStream();
        resource!.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static string Text(string file) => Encoding.UTF8.GetString(Resource(file));

    /// <summary>
    /// Every embedded web file other than the two dashboards (which Jellyfin serves itself as
    /// configuration pages) is served by an anonymous route of this controller named after the file,
    /// byte for byte, with a content type that matches it. A new asset without a route, or a route
    /// whose resource is missing, leaves the dashboards stuck on "Couldn't load the Jellyscribe page".
    /// </summary>
    [Fact]
    public void EveryEmbeddedWebAsset_IsServedVerbatim_WithItsContentType()
    {
        var asm = typeof(SidebarController).Assembly;
        var assets = asm.GetManifestResourceNames()
            .Where(n => n.StartsWith("LetterboxdSync.Web.", StringComparison.Ordinal))
            .Select(n => n.Substring("LetterboxdSync.Web.".Length))
            .Where(f => f != "configPage.html" && f != "userPage.html")
            .ToList();
        Assert.Equal(new[] { "jellyscribe.css", "jellyscribe.js", "sidebar.js" }, assets.OrderBy(f => f, StringComparer.Ordinal));

        var routes = typeof(SidebarController).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(m => m.GetCustomAttributes<HttpGetAttribute>().Select(a => (Route: a.Template, Method: m)))
            .ToDictionary(x => x.Route!, x => x.Method);
        foreach (var file in assets)
        {
            Assert.True(routes.TryGetValue(file, out var method), "no route serves " + file);
            Assert.True(method!.IsDefined(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute)), file + " must be anonymous");
            var args = method.GetParameters().Select(p => (object?)null).ToArray();
            var served = Assert.IsType<FileStreamResult>(method.Invoke(WithRequest(), args));
            var expectedType = file.EndsWith(".css", StringComparison.Ordinal) ? "text/css" : "application/javascript";
            Assert.StartsWith(expectedType, served.ContentType, StringComparison.Ordinal);
            using var bytes = new MemoryStream();
            served.FileStream.CopyTo(bytes);
            Assert.Equal(Resource(file), bytes.ToArray());
        }
    }

    /// <summary>
    /// The shared files may be cached for good only under the version this build stamped into the
    /// pages. Any other ?v= (a page from before an upgrade) or none is revalidated, so a browser can
    /// never keep an old copy for a newer page.
    /// </summary>
    [Theory]
    [InlineData("css")]
    [InlineData("js")]
    public void SharedAssets_AreCachedForGood_OnlyUnderThisBuildsVersion(string kind)
    {
        string CacheFor(string? v)
        {
            var controller = WithRequest();
            var file = Assert.IsType<FileStreamResult>(kind == "css" ? controller.GetSharedCss(v) : controller.GetSharedJs(v));
            // Revalidation can be answered with 304: the tag names this build.
            Assert.NotNull(file.EntityTag);
            Assert.StartsWith("\"" + SidebarController.AssetVersion + "-", file.EntityTag!.Tag.ToString(), StringComparison.Ordinal);
            return controller.Response.Headers.CacheControl.ToString();
        }

        Assert.Equal("public, max-age=31536000, immutable", CacheFor(SidebarController.AssetVersion));
        Assert.Equal("no-cache", CacheFor(null));
        Assert.Equal("no-cache", CacheFor("1.0.0.0"));
    }

    /// <summary>
    /// The build stamps its version into both pages and the shared script: the pages ask for the shared
    /// files with ?v=&lt;version&gt; and start only with the shared code registered under that same
    /// version, so a page never runs against an older shared script left in the browser tab.
    /// </summary>
    [Theory]
    [InlineData("configPage.html", "WS")]
    [InlineData("userPage.html", "WSU")]
    public void Pages_LoadTheSharedAssetsOfTheirOwnVersion(string page, string global)
    {
        var html = Text(page);
        // The stamp is this build's version (a Debug build adds "-dev", which the server never caches for good).
        var version = System.Text.RegularExpressions.Regex.Match(html, @"var V = '([^']*)';").Groups[1].Value;
        Assert.True(version == SidebarController.AssetVersion || version == SidebarController.AssetVersion + "-dev", "stamped " + version);
        Assert.Contains("sharedAsset('link', 'jellyscribe.css')", html, StringComparison.Ordinal);
        Assert.Contains("sharedAsset('script', 'jellyscribe.js')", html, StringComparison.Ordinal);
        Assert.Contains("ApiClient.getUrl(path, { v: V })", html, StringComparison.Ordinal);
        // The reuse key carries the version, and the factory is looked up by it.
        Assert.Contains("'[data-v=\"' + V + '\"]'", html, StringComparison.Ordinal);
        Assert.Contains("window.JellyscribeShared[V]", html, StringComparison.Ordinal);
        Assert.Contains("window." + global + " = ", html, StringComparison.Ordinal);
        Assert.Contains("registry['" + version + "'] = function (root)", Text("jellyscribe.js"), StringComparison.Ordinal);
        foreach (var file in new[] { "configPage.html", "userPage.html", "jellyscribe.js", "jellyscribe.css" })
            Assert.DoesNotContain("@@JELLYSCRIBE_VERSION@@", Text(file), StringComparison.Ordinal);
    }

    /// <summary>
    /// The shared files are served to anyone, so they stay static: no credential-like literal, no
    /// inline handler built from data, and an escaper that covers every character that matters in
    /// text and in a quoted attribute (account names and titles from other users go through it).
    /// </summary>
    [Fact]
    public void AnonymousScripts_HoldNoSecrets_AndTheSharedEscaperCoversQuotes()
    {
        foreach (var file in new[] { "sidebar.js", "jellyscribe.js" })
        {
            var js = Text(file);
            Assert.DoesNotMatch(@"(?i)(api[_-]?key|access[_-]?token|password)\s*[:=]\s*['""][^'""]+['""]", js);
            Assert.DoesNotMatch(@"\b[0-9a-f]{32}\b", js);
        }
        var shared = Text("jellyscribe.js");
        Assert.DoesNotContain("onclick", shared, StringComparison.OrdinalIgnoreCase);
        var esc = System.Text.RegularExpressions.Regex.Match(shared, @"\n\s+esc: function \(s\) \{ (.*) \},\n").Groups[1].Value;
        foreach (var pair in new[] { "/&/g, '&amp;'", "/\"/g, '&quot;'", "/</g, '&lt;'", "/>/g, '&gt;'" })
            Assert.Contains(pair, esc, StringComparison.Ordinal);
        Assert.Contains("escAttr: function (s) { return this.esc(s); }", shared, StringComparison.Ordinal);
    }

    /// <summary>The look keeps its bundled typefaces: the shared stylesheet carries them, once.</summary>
    [Fact]
    public void SharedStylesheet_KeepsTheBundledFonts()
    {
        var css = Text("jellyscribe.css");
        foreach (var face in new[] { "'WS Sans'; font-style: normal; font-weight: 400;", "'WS Sans'; font-style: normal; font-weight: 500;", "'WS Sans'; font-style: normal; font-weight: 600 900;",
                     "'WS Mono'; font-style: normal; font-weight: 400;", "'WS Mono'; font-style: normal; font-weight: 500;", "'WS Mono'; font-style: normal; font-weight: 600 900;" })
            Assert.Equal(1, CountOf(css, "@font-face { font-family: " + face + " font-display: swap; src: url(data:font/woff2;base64,"));
        foreach (var page in new[] { "configPage.html", "userPage.html" })
            Assert.DoesNotContain("@font-face", Text(page), StringComparison.Ordinal);
    }

    private static int CountOf(string s, string part)
    {
        int n = 0, at = 0;
        while ((at = s.IndexOf(part, at, StringComparison.Ordinal)) >= 0) { n++; at += part.Length; }
        return n;
    }

    [Fact]
    public void GetSidebarJs_NavLinkLabelIsJellyscribe()
    {
        // Pins the injected sidebar nav link text so a future partial rename can't
        // silently regress it back to the old brand name.
        var controller = new SidebarController();

        var file = Assert.IsType<FileStreamResult>(controller.GetSidebarJs());
        using var reader = new StreamReader(file.FileStream);
        var contents = reader.ReadToEnd();

        Assert.Contains("navMenuOptionText\">Jellyscribe<", contents);
        Assert.DoesNotContain("navMenuOptionText\">Letterboxd<", contents);
    }
}
