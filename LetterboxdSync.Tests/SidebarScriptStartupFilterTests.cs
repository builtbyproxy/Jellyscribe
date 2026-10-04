using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using LetterboxdSync;
using LetterboxdSync.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// The sidebar link without File Transformation: the shared tag helper, and the request-time
/// middleware run through a real ASP.NET pipeline whose terminal stands in for Jellyfin's
/// static web client.
/// </summary>
[Collection("Plugin")]
public class SidebarScriptStartupFilterTests : IDisposable
{
    private const string Page = "<!DOCTYPE html><html><head><title>Jellyfin</title></head><body><div id=\"app\"></div></body></html>";
    private readonly string _tempDir;

    public SidebarScriptStartupFilterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lbs-sidebar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var paths = Substitute.For<IApplicationPaths>();
        paths.PluginConfigurationsPath.Returns(_tempDir);
        paths.DataPath.Returns(_tempDir);
        var xml = Substitute.For<IXmlSerializer>();
        xml.DeserializeFromFile(typeof(PluginConfiguration), Arg.Any<string>()).Returns(_ => new PluginConfiguration());
        new Plugin(paths, xml);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    // ----- SidebarScript.Inject -----

    [Fact]
    public void Inject_AddsRelativeTagBeforeHeadClose()
    {
        var html = SidebarScript.Inject(Page);
        Assert.Contains(SidebarScript.Tag + "\n</head>", html);
        Assert.Contains("src=\"../LetterboxdSync/Web/sidebar.js\"", html);
    }

    [Theory]
    [InlineData("<script src=\"../LetterboxdSync/Web/sidebar.js\" defer></script>")]
    [InlineData("<script src=\"/LetterboxdSync/Web/sidebar.js\" defer></script>")] // File Transformation, before this version
    public void Inject_AlreadyPresent_ReturnsInputUnchanged(string existing)
    {
        var page = Page.Replace("</head>", existing + "</head>");
        Assert.Same(page, SidebarScript.Inject(page));
    }

    [Fact]
    public void Inject_NotAnHtmlDocument_ReturnsInputUnchanged()
    {
        const string chunk = "export default function index_html(){}";
        Assert.Same(chunk, SidebarScript.Inject(chunk));
    }

    [Fact]
    public void Inject_UppercaseHeadClose_StillInjects()
    {
        Assert.Contains(SidebarScript.Tag, SidebarScript.Inject("<html><HEAD></HEAD><body></body></html>"));
    }

    [Fact]
    public void FileTransformationCallback_UsesTheSameTag()
    {
        var html = SidebarTransformCallback.Transform(new SidebarPatchPayload { Contents = Page });
        Assert.Equal(SidebarScript.Inject(Page), html);
    }

    // ----- Middleware -----

    private sealed record Served(int Status, string Body, IHeaderDictionary Headers, IHeaderDictionary SeenRequestHeaders);

    private static async Task<Served> Request(string path, string method = "GET", string? body = Page,
        string contentType = "text/html", int status = 200, Action<HttpRequest>? setup = null, Exception? throwFromApp = null,
        string baseUrl = "", string? contentEncoding = null)
    {
        var filter = new SidebarScriptStartupFilter(NullLogger<SidebarScriptStartupFilter>.Instance);
        IHeaderDictionary? seen = null;
        var app = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
        filter.Configure(inner => inner.Run(async ctx =>
        {
            seen = new HeaderDictionary();
            foreach (var h in ctx.Request.Headers) seen[h.Key] = h.Value;
            if (throwFromApp != null) throw throwFromApp;
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = contentType;
            ctx.Response.Headers.ETag = "\"abc\"";
            ctx.Response.Headers.LastModified = "Sat, 04 Oct 2026 00:00:00 GMT";
            if (contentEncoding != null) ctx.Response.Headers.ContentEncoding = contentEncoding;
            if (body != null) await ctx.Response.WriteAsync(body);
        }))(app);

        var serverConfig = Substitute.For<IServerConfigurationManager>();
        serverConfig.GetConfiguration("network").Returns(new NetworkConfiguration { BaseUrl = baseUrl });
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton(serverConfig).BuildServiceProvider()
        };
        context.Request.Method = method;
        context.Request.Path = path;
        setup?.Invoke(context.Request);
        var output = new MemoryStream();
        context.Response.Body = output;

        await app.Build()(context);
        return new Served(context.Response.StatusCode, Encoding.UTF8.GetString(output.ToArray()), context.Response.Headers, seen ?? new HeaderDictionary());
    }

    [Theory]
    [InlineData("/web/", "")]
    [InlineData("/web/index.html", "")]
    [InlineData("/WEB/Index.html", "")]
    [InlineData("/jellyfin/web/", "/jellyfin")] // server base URL
    [InlineData("/jellyfin/web/index.html", "/jellyfin/")]
    [InlineData("/jellyfin/web/", "jellyfin")]
    public async Task IndexPage_GetsTheTagOnce(string path, string baseUrl)
    {
        var served = await Request(path, baseUrl: baseUrl);

        Assert.Equal(1, CountOf(served.Body, SidebarScript.Marker));
        Assert.Equal(Encoding.UTF8.GetByteCount(served.Body), served.Headers.ContentLength);
        Assert.False(served.Headers.ContainsKey("ETag"));
        Assert.False(served.Headers.ContainsKey("Last-Modified"));
    }

    [Fact]
    public async Task IndexPage_StripsCacheAndCompressionRequestHeaders()
    {
        var served = await Request("/web/", setup: r =>
        {
            r.Headers.AcceptEncoding = "gzip, br";
            r.Headers.IfNoneMatch = "\"abc\"";
            r.Headers.IfModifiedSince = "Sat, 04 Oct 2026 00:00:00 GMT";
            r.Headers.Range = "bytes=0-10";
        });

        Assert.False(served.SeenRequestHeaders.ContainsKey("Accept-Encoding"));
        Assert.False(served.SeenRequestHeaders.ContainsKey("If-None-Match"));
        Assert.False(served.SeenRequestHeaders.ContainsKey("If-Modified-Since"));
        Assert.False(served.SeenRequestHeaders.ContainsKey("Range"));
    }

    [Fact]
    public async Task PageAlreadyCarryingTheTag_IsServedUnchanged()
    {
        var page = Page.Replace("</head>", "<script src=\"/LetterboxdSync/Web/sidebar.js\" defer></script></head>");
        var served = await Request("/web/", body: page);
        Assert.Equal(page, served.Body);
    }

    [Theory]
    [InlineData("/web/main.bundle.js", "")]
    [InlineData("/Items/abc", "")]
    [InlineData("/LetterboxdSync/Web/sidebar.js", "")]
    [InlineData("/web", "")]                    // Jellyfin redirects this to /web/ itself
    [InlineData("/SomePlugin/web/", "")]        // another route that merely ends in /web/
    [InlineData("/web/", "/jellyfin")]          // not the index when the server has a base URL
    [InlineData("/other/web/", "/jellyfin")]
    [InlineData("/jellyfin/web/", "")]
    public async Task OtherPaths_PassThroughUntouched(string path, string baseUrl)
    {
        var served = await Request(path, baseUrl: baseUrl, setup: r => r.Headers.AcceptEncoding = "gzip");
        Assert.Equal(Page, served.Body);
        Assert.True(served.SeenRequestHeaders.ContainsKey("Accept-Encoding"));
        Assert.True(served.Headers.ContainsKey("ETag"));
    }

    [Fact]
    public async Task NonGet_PassesThroughUntouched()
    {
        var served = await Request("/web/", method: "HEAD");
        Assert.DoesNotContain(SidebarScript.Marker, served.Body);
    }

    [Fact]
    public async Task NonHtmlOrNon200_PassesThroughUntouched()
    {
        Assert.Equal("{}", (await Request("/web/", body: "{}", contentType: "application/json")).Body);
        var notModified = await Request("/web/", body: null, status: 304);
        Assert.Equal(304, notModified.Status);
        Assert.Equal(string.Empty, notModified.Body);
    }

    [Fact]
    public async Task CompressedOrOversizedPage_PassesThroughUntouched()
    {
        Assert.Equal(Page, (await Request("/web/", contentEncoding: "gzip")).Body);

        var big = Page.Replace("</body>", new string('x', SidebarScriptStartupFilter.MaxPageBytes) + "</body>");
        Assert.DoesNotContain(SidebarScript.Marker, (await Request("/web/", body: big)).Body);
    }

    [Fact]
    public void KillSwitch_DefaultsOff_ForConfigsSavedBeforeIt()
    {
        var serializer = new System.Xml.Serialization.XmlSerializer(typeof(PluginConfiguration));
        using var reader = new StringReader("<?xml version=\"1.0\"?><PluginConfiguration xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\"></PluginConfiguration>");
        Assert.False(((PluginConfiguration)serializer.Deserialize(reader)!).DisableSidebarScriptMiddleware);
    }

    [Fact]
    public void ServiceRegistrator_RegistersTheStartupFilter()
    {
        var services = new ServiceCollection();
        new ServiceRegistrator().RegisterServices(services, null!);
        Assert.Single(services, d => d.ServiceType == typeof(Microsoft.AspNetCore.Hosting.IStartupFilter)
            && d.ImplementationType == typeof(SidebarScriptStartupFilter));
    }

    [Fact]
    public async Task KillSwitch_LeavesThePageAlone()
    {
        Plugin.Instance!.Configuration.DisableSidebarScriptMiddleware = true;
        var served = await Request("/web/");
        Assert.Equal(Page, served.Body);
    }

    [Fact]
    public async Task PipelineException_Propagates()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Request("/web/", throwFromApp: new InvalidOperationException("boom")));
    }

    private static int CountOf(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + 1, StringComparison.Ordinal))
            count++;
        return count;
    }
}
