using System;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace LetterboxdSync.Api;

/// <summary>
/// Serves the plugin's static web assets: sidebar.js (injected into the web client's index page) and the
/// stylesheet and script both dashboards share. All are embedded files, the same for every caller, so
/// they are anonymous like the web client's own files.
/// </summary>
[ApiController]
[Route("LetterboxdSync/Web")]
public class SidebarController : ControllerBase
{
    /// <summary>
    /// The version the dashboards put in the shared assets' URLs (?v=), stamped into them at build time.
    /// </summary>
    internal static readonly string AssetVersion = typeof(SidebarController).Assembly.GetName().Version!.ToString();

    // A validator for the revalidated (no-cache) case. The module id changes whenever the assembly's
    // content does, so a rebuilt debug build with the same version still gets a new tag.
    private static readonly EntityTagHeaderValue AssetTag =
        new("\"" + AssetVersion + "-" + typeof(SidebarController).Assembly.ManifestModule.ModuleVersionId.ToString("N") + "\"");

    private readonly Assembly _assembly = typeof(SidebarController).Assembly;

    [HttpGet("sidebar.js")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetSidebarJs()
    {
        var stream = _assembly.GetManifestResourceStream("LetterboxdSync.Web.sidebar.js");
        if (stream == null)
        {
            return NotFound();
        }

        return File(stream, "application/javascript");
    }

    /// <summary>The script both dashboards share.</summary>
    [HttpGet("jellyscribe.js")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetSharedJs([FromQuery] string? v = null) => SharedAsset("jellyscribe.js", "application/javascript; charset=utf-8", v);

    /// <summary>The stylesheet (and bundled fonts) both dashboards share.</summary>
    [HttpGet("jellyscribe.css")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetSharedCss([FromQuery] string? v = null) => SharedAsset("jellyscribe.css", "text/css; charset=utf-8", v);

    // A request for this build's version may be cached for good: the next build is a new URL. Anything
    // else (no version, or a page from another build) is revalidated each time, so it can never pin an
    // older or newer copy in the browser's cache across an upgrade; the ETag lets that revalidation be
    // answered with 304 instead of the whole file (the stylesheet carries the fonts).
    private IActionResult SharedAsset(string file, string contentType, string? v)
    {
        var stream = _assembly.GetManifestResourceStream("LetterboxdSync.Web." + file);
        if (stream == null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = string.Equals(v, AssetVersion, StringComparison.Ordinal)
            ? "public, max-age=31536000, immutable"
            : "no-cache";
        return File(stream, contentType, lastModified: null, entityTag: AssetTag);
    }
}
