using System;
using System.Globalization;
using System.Linq;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace LetterboxdSync.Api;

/// <summary>
/// Shared "who is the calling Jellyfin user" resolution for authenticated plugin controllers.
/// Parsing the Jellyfin-UserId auth claim and resolving it via IUserManager is generic, not
/// specific to either the Letterboxd or Serializd client, so it lives here instead of being
/// duplicated per controller (design.md: "the two clients share no code beyond generic helpers").
/// </summary>
public abstract class JellyfinUserApiController : ControllerBase
{
    private readonly IUserManager _userManager;

    protected JellyfinUserApiController(IUserManager userManager)
    {
        _userManager = userManager;
    }

    /// <summary>The calling Jellyfin user's id (dashes stripped, matching User.Id.ToString("N")), or null if the auth claim is missing.</summary>
    protected string? GetCurrentUserId()
        => User.Claims.FirstOrDefault(c => c.Type == "Jellyfin-UserId")?.Value?.Replace("-", string.Empty);

    /// <summary>
    /// Whose stored credentials a verify/test call may fall back to: <paramref name="requestedUserId"/>
    /// for an administrator (the dashboard edits other users' accounts), the caller otherwise.
    /// Jellyfin's auth handler puts the Administrator role claim on admin sessions.
    /// </summary>
    protected string? GetCredentialOwnerId(string? requestedUserId)
        => !string.IsNullOrWhiteSpace(requestedUserId) && CallerIsAdministrator()
            ? requestedUserId.Replace("-", string.Empty)
            : GetCurrentUserId();

    /// <summary>The calling Jellyfin user, resolved via IUserManager, or null.</summary>
    protected Jellyfin.Database.Implementations.Entities.User? GetCurrentUser()
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return null;
        return _userManager.GetUsers().FirstOrDefault(u => u.Id.ToString("N") == userId);
    }

    /// <summary>The calling Jellyfin user's username, resolved via IUserManager, or null.</summary>
    protected string? GetJellyfinUsername() => GetCurrentUser()?.Username;

    /// <summary>
    /// True when the caller is a Jellyfin administrator. Jellyfin's authentication handler gives
    /// administrators (and API keys) the "Administrator" role claim, the same signal its
    /// RequiresElevation policy checks.
    /// </summary>
    protected bool CallerIsAdministrator() => User.IsInRole("Administrator");

    /// <summary>
    /// 429 for a login check refused by <see cref="LoginCheckLimiter"/>, with a Retry-After
    /// header and an <c>error</c> the dashboards show as it is.
    /// </summary>
    protected ObjectResult TooManyLoginChecks(TimeSpan retryAfter)
    {
        var seconds = (int)Math.Ceiling(retryAfter.TotalSeconds);
        Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return StatusCode(StatusCodes.Status429TooManyRequests, new
        {
            error = LoginCheckLimiter.RefusalMessage(retryAfter),
            retryAfterSeconds = seconds
        });
    }
}
