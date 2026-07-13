using System.Linq;
using MediaBrowser.Controller.Library;
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

    /// <summary>The calling Jellyfin user's username, resolved via IUserManager, or null.</summary>
    protected string? GetJellyfinUsername()
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId)) return null;
        return _userManager.GetUsers().FirstOrDefault(u => u.Id.ToString("N") == userId)?.Username;
    }
}
