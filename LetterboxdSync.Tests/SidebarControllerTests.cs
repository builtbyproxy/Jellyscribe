using System.IO;
using LetterboxdSync.Api;
using Microsoft.AspNetCore.Mvc;
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
