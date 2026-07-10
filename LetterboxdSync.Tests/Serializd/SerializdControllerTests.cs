using System;
using System.Threading.Tasks;
using LetterboxdSync.Api;
using LetterboxdSync.Serializd;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace LetterboxdSync.Tests.Serializd;

public class SerializdControllerTests : IDisposable
{
    private static SerializdSyncRunner MakeRunner()
        => new(new LoggerFactory(), Substitute.For<ILibraryManager>(),
               Substitute.For<IUserManager>(), Substitute.For<IUserDataManager>());

    private readonly SerializdController _controller =
        new(new NullLogger<SerializdController>(), MakeRunner());

    public void Dispose() => SerializdController.VerifyOverrideForTesting = null;

    [Fact]
    public async Task Verify_MissingCredentials_ReturnsBadRequest()
    {
        var result = await _controller.Verify(new SerializdController.VerifyRequest { Email = "", Password = "" });
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Verify_GoodLogin_ReturnsOkWithUsername()
    {
        SerializdController.VerifyOverrideForTesting = (_, _, _) => Task.FromResult<string?>("8bitproxy");

        var result = await _controller.Verify(
            new SerializdController.VerifyRequest { Email = "me@example.com", Password = "pw" });

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Contains("8bitproxy", ok.Value!.ToString());
    }

    [Fact]
    public async Task Verify_BadLogin_ReturnsBadRequest()
    {
        SerializdController.VerifyOverrideForTesting = (_, _, _) =>
            throw new Exception("Serializd login failed (401): Incorrect password.");

        var result = await _controller.Verify(
            new SerializdController.VerifyRequest { Email = "me@example.com", Password = "wrong" });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void SyncNow_NoAuthenticatedUser_ReturnsBadRequest()
    {
        // Empty HttpContext → no Jellyfin-UserId claim → can't determine the user.
        _controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext(),
        };

        var result = _controller.SyncNow();
        Assert.IsType<BadRequestObjectResult>(result);
    }
}
