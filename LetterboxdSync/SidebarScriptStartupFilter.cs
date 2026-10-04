using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

/// <summary>
/// The sidebar script tag, shared by <see cref="SidebarScriptStartupFilter"/> and the
/// File Transformation callback (<see cref="SidebarTransformCallback"/>) so both inject the
/// same tag and each recognises the other's work.
/// </summary>
internal static class SidebarScript
{
    /// <summary>Present in every form of the tag either path has ever written.</summary>
    internal const string Marker = "LetterboxdSync/Web/sidebar.js";

    // Relative to /web/ so it resolves under a server base URL (e.g. /jellyfin/web/).
    internal const string Tag = "<script src=\"../LetterboxdSync/Web/sidebar.js\" defer></script>";

    /// <summary>
    /// Insert <see cref="Tag"/> before <c>&lt;/head&gt;</c>. Returns the input unchanged when it
    /// already carries the script (from either path) or is not an HTML document.
    /// </summary>
    internal static string Inject(string html)
    {
        if (html.Contains(Marker, StringComparison.Ordinal))
            return html;

        var headClose = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        return headClose < 0 ? html : html.Insert(headClose, Tag + "\n");
    }
}

/// <summary>
/// Injects the sidebar script into the web client's index page at request time, so the
/// Jellyscribe sidebar link appears without the File Transformation plugin. A pass-through
/// middleware ahead of Jellyfin's own pipeline buffers only GETs of the index page, adds the
/// tag, and leaves everything else (and every non-HTML or non-200 response) untouched. Nothing
/// on disk changes. When File Transformation is installed and already added the tag, the
/// marker check makes this a no-op. <see cref="Configuration.PluginConfiguration.DisableSidebarScriptMiddleware"/>
/// turns it off.
/// </summary>
public sealed class SidebarScriptStartupFilter : IStartupFilter
{
    /// <summary>Index pages larger than this are passed through untouched rather than rewritten.</summary>
    internal const int MaxPageBytes = 1024 * 1024;

    private readonly ILogger<SidebarScriptStartupFilter> _logger;
    private int _loggedOnce;

    public SidebarScriptStartupFilter(ILogger<SidebarScriptStartupFilter> logger)
    {
        _logger = logger;
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(InvokeAsync);
            next(app);
        };
    }

    internal async Task InvokeAsync(HttpContext context, Func<Task> next)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null || config.DisableSidebarScriptMiddleware
            || !HttpMethods.IsGet(context.Request.Method)
            || !IsIndexRequest(context.Request.Path.Value, ServerBaseUrl(context)))
        {
            await next().ConfigureAwait(false);
            return;
        }

        // A compressed, partial, or 304 response can't be edited, and a 304 would let a copy
        // cached before this version keep serving the page without the link.
        var headers = context.Request.Headers;
        headers.Remove("Accept-Encoding");
        headers.Remove("Range");
        headers.Remove("If-Range");
        headers.Remove("If-None-Match");
        headers.Remove("If-Modified-Since");

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next().ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        buffer.Seek(0, SeekOrigin.Begin);
        var isHtml = context.Response.StatusCode == StatusCodes.Status200OK
            && (context.Response.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) ?? false);
        var editable = isHtml
            && buffer.Length <= MaxPageBytes
            && !context.Response.Headers.ContainsKey("Content-Encoding");
        if (!editable)
        {
            await buffer.CopyToAsync(originalBody).ConfigureAwait(false);
            return;
        }

        string html;
        using (var reader = new StreamReader(buffer, Encoding.UTF8, true, 1024, leaveOpen: true))
            html = await reader.ReadToEndAsync().ConfigureAwait(false);

        var injected = html;
        try
        {
            injected = SidebarScript.Inject(html);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Sidebar script injection failed, serving the page unchanged: {Message}", ex.Message);
        }

        if (!ReferenceEquals(injected, html) && System.Threading.Interlocked.Exchange(ref _loggedOnce, 1) == 0)
            _logger.LogInformation("Jellyscribe sidebar link injected into the web client (no File Transformation needed)");

        var bytes = Encoding.UTF8.GetBytes(injected);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength = bytes.Length;
        context.Response.Headers.Remove("ETag");
        context.Response.Headers.Remove("Last-Modified");
        context.Response.Headers.Remove("Accept-Ranges");
        await originalBody.WriteAsync(bytes).ConfigureAwait(false);
    }

    /// <summary>
    /// Jellyfin's configured base URL (e.g. "/jellyfin"), or empty. Resolved per request rather
    /// than in the constructor so a DI problem can never stop the server's pipeline from building.
    /// </summary>
    private static string? ServerBaseUrl(HttpContext context)
        => (context.RequestServices?.GetService(typeof(IServerConfigurationManager)) as IConfigurationManager)
            ?.GetNetworkConfiguration().BaseUrl;

    /// <summary>
    /// Exactly the web client's index page: {base}/web/ or {base}/web/index.html. Exact, so another
    /// route that merely ends in /web/ (another plugin's, say) is never buffered or rewritten.
    /// </summary>
    internal static bool IsIndexRequest(string? path, string? baseUrl)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        var prefix = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (prefix.Length > 0 && prefix[0] != '/')
            prefix = "/" + prefix;

        return string.Equals(path, prefix + "/web/", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, prefix + "/web/index.html", StringComparison.OrdinalIgnoreCase);
    }
}
