using System;
using System.Net.Mime;
using System.Threading.Tasks;
using LetterboxdSync.Serializd;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync.Api;

/// <summary>
/// Serializd-scoped endpoints. Kept separate from <see cref="LetterboxdController"/> so the
/// two services stay isolated. Account persistence itself goes through the standard plugin
/// config save (like Letterboxd accounts); this controller only provides the credential check
/// behind the config page's "Verify login" button.
/// </summary>
[ApiController]
[Authorize]
[Route("Jellyfin.Plugin.LetterboxdSync/Serializd")]
[Produces(MediaTypeNames.Application.Json)]
public class SerializdController : ControllerBase
{
    private readonly ILogger<SerializdController> _logger;

    /// <summary>
    /// Test-only override for the login check. When non-null, <see cref="Verify"/> calls this
    /// instead of a real network login. Returns the username; throws to simulate a bad login.
    /// Production never sets it (mirrors the factory OverrideForTesting convention).
    /// </summary>
    internal static Func<ILogger, string, string, Task<string?>>? VerifyOverrideForTesting;

    public SerializdController(ILogger<SerializdController> logger)
    {
        _logger = logger;
    }

    public class VerifyRequest
    {
        public string? Email { get; set; }

        public string? Password { get; set; }
    }

    /// <summary>
    /// Verifies a Serializd email/password by logging in. Returns the account username on
    /// success (200) or a 400 with an error message on failure. Persists nothing.
    /// </summary>
    [HttpPost("Verify")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> Verify([FromBody] VerifyRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Email) || string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(new { error = "Email and password are required." });

        try
        {
            string? username;
            if (VerifyOverrideForTesting != null)
            {
                username = await VerifyOverrideForTesting(_logger, request.Email, request.Password).ConfigureAwait(false);
            }
            else
            {
                using var client = new SerializdApiClient(_logger);
                username = await client.VerifyLoginAsync(request.Email, request.Password).ConfigureAwait(false);
            }

            return Ok(new { ok = true, username });
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Serializd credential verification failed: {Message}", ex.Message);
            return BadRequest(new { error = "Login failed. Check the email and password." });
        }
    }
}
