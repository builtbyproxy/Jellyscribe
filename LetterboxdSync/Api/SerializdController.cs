using System;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using LetterboxdSync.Configuration;
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
    private readonly SerializdSyncRunner _syncRunner;
    private readonly SerializdWatchlistSyncRunner _watchlistRunner;
    private readonly MediaBrowser.Controller.Library.IUserManager _userManager;

    /// <summary>
    /// Test-only override for the login check. When non-null, <see cref="Verify"/> calls this
    /// instead of a real network login. Returns the username; throws to simulate a bad login.
    /// Production never sets it (mirrors the factory OverrideForTesting convention).
    /// </summary>
    internal static Func<ILogger, string, string, Task<string?>>? VerifyOverrideForTesting;

    /// <summary>
    /// Holds the most recent fire-and-forget sync started by <see cref="SyncNow"/> so tests can
    /// await it (it otherwise outlives the request). Production never reads it.
    /// </summary>
    internal Task? LastBackgroundSync { get; private set; }

    public SerializdController(ILogger<SerializdController> logger, SerializdSyncRunner syncRunner,
        SerializdWatchlistSyncRunner watchlistRunner, MediaBrowser.Controller.Library.IUserManager userManager)
    {
        _logger = logger;
        _syncRunner = syncRunner;
        _watchlistRunner = watchlistRunner;
        _userManager = userManager;
    }

    private string? GetCurrentUserId()
        => User.Claims.FirstOrDefault(c => c.Type == "Jellyfin-UserId")?.Value?.Replace("-", string.Empty);

    private string? GetJellyfinUsername()
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return null;
        return _userManager.GetUsers().FirstOrDefault(u => u.Id.ToString("N") == userId)?.Username;
    }

    /// <summary>Serializd activity stats for the dashboard, same shape as the Letterboxd <c>/Stats</c>.</summary>
    [HttpGet("Stats")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetStats()
    {
        var (total, success, failed, skipped, rewatches) = SerializdActivity.GetStats(GetJellyfinUsername());
        return Ok(new { total, success, failed, skipped, rewatches });
    }

    /// <summary>Paged Serializd activity for the dashboard, same shape as the Letterboxd <c>/History</c>.</summary>
    [HttpGet("History")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetHistory([FromQuery] int count = 50, [FromQuery] int offset = 0)
    {
        var capped = Math.Clamp(count, 1, 500);
        var (events, total) = SerializdActivity.GetPage(Math.Max(offset, 0), capped, GetJellyfinUsername());
        return Ok(new { events, total });
    }

    public class VerifyRequest
    {
        public string? Email { get; set; }

        public string? Password { get; set; }
    }

    public class ReviewRequest
    {
        public int TmdbId { get; set; }

        public int? Rating { get; set; }

        public string? ReviewText { get; set; }

        public bool ContainsSpoilers { get; set; }
    }

    /// <summary>
    /// Posts a show-level Serializd review for the calling user (fans out to their enabled
    /// Serializd accounts), the TV counterpart to the Letterboxd <c>/Review</c> endpoint.
    /// </summary>
    [HttpPost("Review")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> PostReview([FromBody] ReviewRequest request)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
            return BadRequest(new { error = "Could not determine user" });
        if (request == null || request.TmdbId <= 0)
            return BadRequest(new { error = "A show TMDb id is required" });
        if (string.IsNullOrWhiteSpace(request.ReviewText) && request.Rating is not > 0)
            return BadRequest(new { error = "Write a review or set a rating" });

        var accounts = Plugin.Instance!.Configuration.GetEnabledSerializdAccountsForUser(userId).ToList();
        if (accounts.Count == 0)
            return BadRequest(new { error = "No enabled Serializd account for your user" });

        var posted = 0;
        foreach (var account in accounts)
        {
            try
            {
                using var service = await SerializdServiceFactory
                    .CreateAuthenticatedAsync(account.Email, account.Password, _logger).ConfigureAwait(false);
                await service.CreateShowReviewAsync(request.TmdbId, request.Rating, request.ReviewText, request.ContainsSpoilers)
                    .ConfigureAwait(false);
                posted++;
            }
            catch (Exception ex)
            {
                _logger.LogError("Serializd review failed for TMDb {TmdbId} as {Email}: {Message}",
                    request.TmdbId, account.Email, ex.Message);
            }
        }

        if (posted == 0)
            return BadRequest(new { error = "Could not post the review" });
        return Ok(new { posted });
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

    /// <summary>
    /// Kicks the Serializd catch-up for the calling user (logs any watched episodes not yet on
    /// Serializd). Any logged-in user may call it; it only touches their own accounts. Returns
    /// 202 and runs in the background; 400 if the user has no enabled Serializd account,
    /// 409 if a Serializd sync is already running.
    /// </summary>
    [HttpPost("SyncNow")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult SyncNow()
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
            return BadRequest(new { error = "Could not determine user" });

        if (SerializdSyncGate.IsRunning)
            return Conflict(new { error = "A Serializd sync is already running" });

        if (!Plugin.Instance!.Configuration.GetEnabledSerializdAccountsForUser(userId).Any())
            return BadRequest(new { error = "No enabled Serializd accounts are configured for your user" });

        LastBackgroundSync = Task.Run(async () =>
        {
            try
            {
                await _syncRunner.TryRunForUserAsync(userId, "manual", CancellationToken.None).ConfigureAwait(false);
                await _watchlistRunner.TryRunForUserAsync(userId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError("Manual Serializd sync failed for {UserId}: {Message}", userId, ex.Message);
            }
        });

        return Accepted(new { started = true });
    }

    /// <summary>
    /// Mirrors the calling user's Serializd watchlist into the Jellyfin collection + playlist,
    /// on demand (the TV counterpart to the Letterboxd "Sync Watchlist Now"). 202 + background;
    /// 400 if no Serializd account has watchlist sync enabled.
    /// </summary>
    [HttpPost("SyncWatchlistNow")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult SyncWatchlistNow()
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
            return BadRequest(new { error = "Could not determine user" });

        var enabled = Plugin.Instance!.Configuration.GetEnabledSerializdAccountsForUser(userId);
        if (!enabled.Any(a => a.SyncWatchlist))
            return BadRequest(new { error = "No Serializd account has watchlist sync enabled. Tick it on the TV / Serializd tab and Save." });

        LastBackgroundSync = Task.Run(async () =>
        {
            try
            {
                await _watchlistRunner.TryRunForUserAsync(userId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError("Manual Serializd watchlist sync failed for {UserId}: {Message}", userId, ex.Message);
            }
        });

        return Accepted(new { started = true });
    }
}
