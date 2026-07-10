using System;
using System.Threading.Tasks;
using LetterboxdSync.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LetterboxdSync.Tests.Serializd;

public class SerializdControllerTests : IDisposable
{
    private readonly SerializdController _controller =
        new(new NullLogger<SerializdController>());

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
}
