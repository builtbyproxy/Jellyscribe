using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using LetterboxdSync.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync.Api;

[ApiController]
[Authorize]
[Route("Jellyfin.Plugin.LetterboxdSync")]
[Produces(MediaTypeNames.Application.Json)]
public class LetterboxdController : JellyfinUserApiController
{
    private readonly ILogger<LetterboxdController> _logger;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IApplicationPaths _appPaths;
    private readonly LetterboxdSyncRunner _syncRunner;
    private readonly WatchlistSyncRunner _watchlistRunner;

    // Holds the most recent fire-and-forget sync task from StartSync/StartWatchlistSync.
    // Production code never reads it; tests await it so a background sync can't outlive
    // its test and hold SyncGate while the next test runs.
    internal Task? LastBackgroundSync { get; private set; }

    public LetterboxdController(
        ILogger<LetterboxdController> logger,
        IUserManager userManager,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        IApplicationPaths appPaths,
        LetterboxdSyncRunner syncRunner,
        WatchlistSyncRunner watchlistRunner)
        : base(userManager)
    {
        _logger = logger;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _appPaths = appPaths;
        _syncRunner = syncRunner;
        _watchlistRunner = watchlistRunner;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    [HttpGet("Progress")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetProgress()
    {
        return Ok(SyncProgress.GetSnapshot());
    }

    /// <summary>
    /// Trigger a sync for the calling user. Any logged-in user can call this; it only ever
    /// touches their own Letterboxd account. Returns 202 immediately and runs in the background;
    /// the UI polls /Progress for completion. Returns 409 if a sync is already running.
    /// </summary>
    [HttpPost("Sync")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult StartSync([FromQuery] string? letterboxdUsername = null)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
            return BadRequest(new { error = "Could not determine user" });

        if (LetterboxdSyncRunner.IsRunning)
            return Conflict(new { error = "Sync already running" });

        // When letterboxdUsername is supplied, target only that account. Otherwise the
        // runner fans out across all enabled accounts for this Jellyfin user.
        if (!string.IsNullOrEmpty(letterboxdUsername))
        {
            if (Config.FindAccount(userId, letterboxdUsername) == null)
                return BadRequest(new { error = $"No enabled Letterboxd account '{letterboxdUsername}' for your user" });
        }
        else if (!Config.GetEnabledAccountsForUser(userId).Any())
        {
            return BadRequest(new { error = "No enabled Letterboxd accounts are configured for your user" });
        }

        // Fire and forget. Errors during the run are logged by the runner; the UI polls /Progress.
        LastBackgroundSync = Task.Run(async () =>
        {
            try
            {
                await _syncRunner.TryRunForUserAsync(
                    userId, "manual", new Progress<double>(), System.Threading.CancellationToken.None,
                    letterboxdUsername).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "User-triggered sync for {UserId} crashed", userId);
            }
        });

        return Accepted(new { started = true });
    }

    /// <summary>
    /// Trigger a Letterboxd → Jellyfin playlist (and optional Seerr) watchlist sync
    /// for the calling user. Same shape as <see cref="StartSync"/>: 202 immediately,
    /// 409 if any sync is in flight, 400 if the user has no watchlist-enabled account.
    /// </summary>
    [HttpPost("SyncWatchlist")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult StartWatchlistSync([FromQuery] string? letterboxdUsername = null)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
            return BadRequest(new { error = "Could not determine user" });

        if (LetterboxdSyncRunner.IsRunning)
            return Conflict(new { error = "Sync already running" });

        // Validate up front so we can return a 400 instead of starting an empty run.
        if (!string.IsNullOrEmpty(letterboxdUsername))
        {
            var account = Config.FindAccount(userId, letterboxdUsername);
            if (account == null)
                return BadRequest(new { error = $"No enabled Letterboxd account '{letterboxdUsername}' for your user" });
            if (!account.EnableWatchlistSync)
                return BadRequest(new { error = $"Watchlist sync is disabled for '{letterboxdUsername}'; enable it in Settings first" });
        }
        else
        {
            var enabled = Config.GetEnabledAccountsForUser(userId).Where(a => a.EnableWatchlistSync).ToList();
            if (enabled.Count == 0)
                return BadRequest(new { error = "No enabled accounts with watchlist sync turned on for your user" });
        }

        LastBackgroundSync = Task.Run(async () =>
        {
            try
            {
                await _watchlistRunner.TryRunForUserAsync(
                    userId, "manual", new Progress<double>(), System.Threading.CancellationToken.None,
                    letterboxdUsername).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "User-triggered watchlist sync for {UserId} crashed", userId);
            }
        });

        return Accepted(new { started = true });
    }

    /// <summary>
    /// Returns the exact JSON the next telemetry ping would send. Admin-only: the payload
    /// contains the instance UUID plus a configuration fingerprint, the same policy as
    /// the config page that displays it. Backs the Integrations "Preview" dialog and the
    /// Overview opt-in notice, whose Copy and "Copy + regenerate ID" actions double as a
    /// diagnostic bundle for bug reports.
    /// </summary>
    [HttpGet("Telemetry/Preview")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetTelemetryPreview()
    {
        int? libraryCount;
        try
        {
            libraryCount = _libraryManager.GetCount(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                Recursive = true
            });
        }
        catch
        {
            libraryCount = null;
        }

        var json = TelemetryService.BuildPayload("weekly", libraryCount);
        return Content(json, "application/json");
    }

    /// <summary>
    /// Gives this server a new random telemetry instance id, the settings "Regenerate ID" and
    /// "Copy + regenerate ID" actions. Admin-only, like the preview that shows the id. Later
    /// pings and log bundles carry only the new id; rows already sent keep the old one.
    /// </summary>
    [HttpPost("Telemetry/RegenerateId")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult RegenerateTelemetryId()
    {
        return Ok(new { instanceId = TelemetryService.RegenerateInstanceId() });
    }

    [HttpGet("Stats")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult GetStats()
    {
        // SyncHistory treats a null username as "everyone", so an unresolved caller must stop here.
        var jellyfinUsername = GetJellyfinUsername();
        if (string.IsNullOrEmpty(jellyfinUsername))
            return BadRequest(new { error = "Could not determine user" });

        var (total, success, failed, skipped, rewatches, requested) = SyncHistory.GetStats(jellyfinUsername);
        return Ok(new
        {
            total,
            success,
            failed,
            skipped,
            rewatches,
            requested,
            watchlist = WatchlistStats.GetFilm(GetCurrentUserId() ?? string.Empty)
        });
    }

    /// <summary>The most rows one <c>/History</c> page returns.</summary>
    internal const int MaxHistoryPage = 250;

    /// <summary>
    /// Paginated sync history. Returns the slice plus the total so the dashboard can
    /// render a paginator. Without an offset the response is backwards-compatible with
    /// pre-pagination clients that just consumed the array, since callers that ignore
    /// the wrapper still get the most recent items via /History?count=N.
    /// </summary>
    [HttpGet("History")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult GetHistory([FromQuery] int count = 50, [FromQuery] int offset = 0)
    {
        var jellyfinUsername = GetJellyfinUsername();
        if (string.IsNullOrEmpty(jellyfinUsername))
            return BadRequest(new { error = "Could not determine user" });

        // Both dashboards ask for 250 rows a page; a lower cap here silently shortened their pages.
        var capped = Math.Clamp(count, 1, MaxHistoryPage);
        var (events, total) = SyncHistory.GetPage(Math.Max(offset, 0), capped, jellyfinUsername);
        return Ok(new { events, total, offset = Math.Max(offset, 0), count = capped });
    }

    /// <summary>
    /// Open auth breakers across all users, for the admin dashboard's paused badges.
    /// Admin-only: the payload names other users' Letterboxd accounts.
    /// </summary>
    [HttpGet("AuthBreakers")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetAuthBreakers()
    {
        var open = AuthBreaker.GetOpenEntries().Select(e => new
        {
            userJellyfinId = e.UserJellyfinId,
            letterboxdUsername = e.LetterboxdUsername,
            failingSinceUtc = e.FirstFailureUtc
        });
        return Ok(new { breakers = open });
    }

    /// <summary>Test-only replacements for the two login attempts in <see cref="VerifyLogin"/>.</summary>
    internal static Func<string, string, Task>? VerifyApiLoginForTesting;

    internal static Func<string, string, string?, string?, Task>? VerifyWebsiteLoginForTesting;

    /// <summary>
    /// Message when a Letterboxd account name is an email address, else null. Letterboxd's API
    /// rejects email sign-in ("Sign-in via email address has been disabled"), and the website
    /// path builds diary URLs from the username, so an email never works.
    /// </summary>
    internal static string? EmailAsUsernameError(string? username) =>
        username != null && username.Contains('@')
            ? "Letterboxd no longer accepts an email address to sign in. Use your Letterboxd username, the name in letterboxd.com/<username>/."
            : null;

    /// <summary>Letterboxd's own reason (an OAuth error_description) when present, else the sanitised message.</summary>
    internal static string DescribeLoginError(Exception ex)
    {
        var match = System.Text.RegularExpressions.Regex.Match(ex.Message, "\"error_description\"\\s*:\\s*\"([^\"]+)\"");
        return match.Success ? match.Groups[1].Value : AuthBreaker.Sanitize(ex.Message) ?? "Unknown error";
    }

    /// <summary>
    /// Checks Letterboxd credentials the way sync will use them: the official API first, then the
    /// website login (with the optional raw cookies and user agent). Reports which one worked, or
    /// both reasons. Saves nothing and does not touch the auth breaker. An empty password or cookie
    /// field uses the stored account with this username, so a saved login can be re-checked
    /// without the secret ever going back to the browser. Failed checks are rate-limited per
    /// user and server-wide (<see cref="LoginCheckLimiter"/>), answering 429.
    /// </summary>
    [HttpPost("Verify")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult> VerifyLogin([FromBody] LetterboxdVerifyRequest request)
    {
        var username = request?.LetterboxdUsername?.Trim();
        if (string.IsNullOrEmpty(username))
            return BadRequest(new { error = "Username and password are required." });

        var stored = Config.FindStored(GetCredentialOwnerId(request!.UserJellyfinId) ?? string.Empty,
            SecretMerge.OriginalOr(request.OriginalLetterboxdUsername, username));
        var password = SecretMerge.KeepIfEmpty(request.LetterboxdPassword, stored?.LetterboxdPassword);
        var rawCookies = SecretMerge.TypedClearedOrKept(request.RawCookies, request.ClearRawCookies, stored?.RawCookies);
        if (string.IsNullOrEmpty(password))
            return BadRequest(new { error = "Username and password are required." });

        var emailError = EmailAsUsernameError(username);
        if (emailError != null)
            return BadRequest(new { error = emailError });

        var limiterKey = GetCurrentUserId() ?? string.Empty;
        if (!LoginCheckLimiter.Letterboxd.TryAcquire(limiterKey, out var stamp, out var retryAfter))
        {
            _logger.LogWarning("Letterboxd login check refused for {UserId}: rate limit reached", limiterKey);
            return TooManyLoginChecks(retryAfter);
        }

        string apiError;
        try
        {
            if (VerifyApiLoginForTesting != null)
            {
                await VerifyApiLoginForTesting(username, password).ConfigureAwait(false);
            }
            else
            {
                using var api = new LetterboxdApiClient(_logger);
                await api.AuthenticateAsync(username, password).ConfigureAwait(false);
            }

            LoginCheckLimiter.Letterboxd.Refund(limiterKey, stamp);
            return Ok(new { ok = true, via = "api" });
        }
        catch (Exception ex)
        {
            apiError = DescribeLoginError(ex);
        }

        try
        {
            if (VerifyWebsiteLoginForTesting != null)
            {
                await VerifyWebsiteLoginForTesting(username, password, rawCookies, request.UserAgent).ConfigureAwait(false);
            }
            else
            {
                using var website = new ScrapingLetterboxdService(_logger, request.UserAgent);
                await website.AuthenticateAsync(username, password, rawCookies).ConfigureAwait(false);
            }

            LoginCheckLimiter.Letterboxd.Refund(limiterKey, stamp);
            return Ok(new { ok = true, via = "website", apiError });
        }
        catch (Exception ex)
        {
            var websiteError = DescribeLoginError(ex);
            _logger.LogWarning("Letterboxd credential verification failed for {LbUser}: API: {ApiError}; website: {WebsiteError}",
                username, apiError, websiteError);
            return BadRequest(new { error = "Login failed.", apiError, websiteError });
        }
    }

    /// <summary>
    /// Returns every Letterboxd account belonging to the calling Jellyfin user, in
    /// config order with primary first. The userPage uses this to render the full list
    /// of accounts the user can edit on their own sidebar page.
    /// </summary>
    [HttpGet("Accounts")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult GetAccounts()
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
            return BadRequest(new { error = "Could not determine user" });

        var accounts = Config.Accounts
            .Where(a => a.UserJellyfinId == userId)
            .OrderByDescending(a => a.IsPrimary)
            .Select(a => new
            {
                letterboxdUsername = a.LetterboxdUsername,
                hasPassword = a.HasPassword,
                hasCookies = a.HasRawCookies,
                userAgent = a.UserAgent,
                authPaused = AuthBreaker.IsOpen(userId, a.LetterboxdUsername),
                authPausedSince = AuthBreaker.GetState(userId, a.LetterboxdUsername)?.FirstFailureUtc,
                enabled = a.Enabled,
                syncFavorites = a.SyncFavorites,
                syncRatings = a.SyncRatings,
                enableDateFilter = a.EnableDateFilter,
                dateFilterDays = a.DateFilterDays,
                enableWatchlistSync = a.EnableWatchlistSync,
                enableDiaryImport = a.EnableDiaryImport,
                autoRequestWatchlist = a.AutoRequestWatchlist,
                backfillAvailableRequests = a.BackfillAvailableRequests,
                mirrorJellyseerrWatchlist = a.MirrorJellyseerrWatchlist,
                skipPreviouslySynced = a.SkipPreviouslySynced,
                stopOnFailure = a.StopOnFailure,
                isPrimary = a.IsPrimary,
                playlistName = a.PlaylistName,
                excludedLibraryIds = a.ExcludedLibraryIds
            })
            .ToList();

        // Naming is admin-only (see PutAccounts); the page hides the field for everyone else.
        return Ok(new { accounts, canSetWatchlistName = CallerIsAdministrator() });
    }

    /// <summary>
    /// Bulk-replace the calling user's set of Letterboxd accounts. Accounts owned by
    /// other Jellyfin users are preserved (security: a non-admin user cannot touch
    /// another user's row). Each submitted account is stamped with the calling user's
    /// id regardless of what the request body claimed. NormalisePrimaryFlags runs after
    /// so the single-primary-per-user invariant holds across the save.
    /// </summary>
    [HttpPut("Accounts")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult PutAccounts([FromBody] AccountsUpdateRequest request)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
            return BadRequest(new { error = "Could not determine user" });

        if (request?.Accounts == null)
            return BadRequest(new { error = "accounts is required" });

        // Reject empty username up front so a malformed row doesn't silently produce
        // a half-broken account that fails authentication later.
        for (var i = 0; i < request.Accounts.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(request.Accounts[i].LetterboxdUsername))
                return BadRequest(new { error = $"Account #{i + 1} is missing a Letterboxd username" });
            if (EmailAsUsernameError(request.Accounts[i].LetterboxdUsername) is { } emailError)
                return BadRequest(new { error = emailError });
        }

        // Preserve every account that doesn't belong to the calling user. The admin
        // page is the only path that should touch other users' rows; this endpoint
        // is per-user scope.
        var preserved = Config.Accounts.Where(a => a.UserJellyfinId != userId).ToList();
        var previous = Config.Accounts.Where(a => a.UserJellyfinId == userId).ToList();

        var canName = CallerIsAdministrator();
        var mine = new List<Account>();
        foreach (var req in request.Accounts)
        {
            // A rename names the username it started from, so the stored secrets and settings follow it.
            var storedName = SecretMerge.OriginalOr(req.OriginalLetterboxdUsername, req.LetterboxdUsername);
            var stored = previous.FirstOrDefault(p => string.Equals(p.LetterboxdUsername, storedName?.Trim(), StringComparison.OrdinalIgnoreCase));
            var account = new Account
            {
                UserJellyfinId = userId,
                LetterboxdUsername = req.LetterboxdUsername,
                LetterboxdPassword = req.LetterboxdPassword ?? string.Empty,
                RawCookies = req.RawCookies,
                ClearRawCookies = req.ClearRawCookies,
                UserAgent = req.UserAgent,
                Enabled = req.Enabled,
                SyncFavorites = req.SyncFavorites,
                SyncRatings = req.SyncRatings ?? stored?.SyncRatings ?? true,
                EnableDateFilter = req.EnableDateFilter,
                DateFilterDays = req.DateFilterDays,
                EnableWatchlistSync = req.EnableWatchlistSync,
                EnableDiaryImport = req.EnableDiaryImport,
                AutoRequestWatchlist = req.AutoRequestWatchlist,
                BackfillAvailableRequests = req.BackfillAvailableRequests,
                MirrorJellyseerrWatchlist = req.MirrorJellyseerrWatchlist,
                SkipPreviouslySynced = req.SkipPreviouslySynced,
                StopOnFailure = req.StopOnFailure,
                IsPrimary = req.IsPrimary,
                // Admin-only, like the Serializd watchlist name: the playlist is found by name among the
                // playlists this user can see, which can include ones shared with them, so a chosen
                // name could aim the sync at someone else's playlist. Anyone else keeps the stored value.
                PlaylistName = canName
                    ? (string.IsNullOrWhiteSpace(req.PlaylistName) ? null : req.PlaylistName.Trim())
                    : stored?.PlaylistName,
                // A client that omits the field keeps the account's stored exclusions.
                ExcludedLibraryIds = LibraryExclusion.ResolveForSave(req.ExcludedLibraryIds, stored?.ExcludedLibraryIds)
            };
            account.KeepSecretsFrom(stored);
            mine.Add(account);
        }

        Config.Accounts.Clear();
        Config.Accounts.AddRange(preserved);
        Config.Accounts.AddRange(mine);

        Config.NormalisePrimaryFlags();
        Plugin.Instance!.SaveConfiguration();

        // Credentials were just (re-)persisted for every submitted account: close any
        // open auth breakers so the next run retries login (issue #103's reset path).
        foreach (var saved in mine)
            AuthBreaker.Reset(userId, saved.LetterboxdUsername);

        _logger.LogInformation("User {UserId} saved {Count} Letterboxd account(s) via /Accounts", userId, mine.Count);
        return Ok(new { success = true, count = mine.Count });
    }

    /// <summary>Test-only transport for <see cref="TestJellyseerr"/>. Production never assigns it.</summary>
    internal static System.Net.Http.HttpMessageHandler? SeerrTestHandlerForTesting;

    /// <summary>
    /// Checks the Seerr URL and API key from the admin settings form. Admin-only, because it
    /// makes the server GET any URL; failures return a fixed message so the response never
    /// describes what answered (or did not answer) at that address.
    /// </summary>
    [HttpPost("TestJellyseerr")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> TestJellyseerr([FromBody] JellyseerrTestRequest request)
    {
        // An empty key uses the stored one, but only against the stored URL, so the saved key is
        // never sent to an address the admin didn't save it for.
        var apiKey = string.IsNullOrEmpty(request.ApiKey) && SecretMerge.IsStoredUrl(request.Url, Config.JellyseerrUrl)
            ? Config.JellyseerrApiKey
            : request.ApiKey;
        if (!SeerrClient.IsConfigured(request.Url, apiKey))
            return BadRequest(new { success = false, error = "URL and API key are required" });

        try
        {
            using var client = new SeerrClient(request.Url!, apiKey!, _logger, SeerrTestHandlerForTesting);
            var userId = await client.GetJellyseerrUserIdAsync(GetCurrentUserId() ?? string.Empty)
                .ConfigureAwait(false);
            return Ok(new
            {
                success = true,
                linkedToCurrentUser = userId.HasValue,
                jellyseerrUserId = userId
            });
        }
        catch (Exception ex)
        {
            // The exception text names hosts, ports and TLS details of whatever the URL points
            // at, so it goes to the server log only and the caller gets a fixed message.
            _logger.LogWarning("Seerr test failed: {Message}", ex.Message);
            return BadRequest(new { success = false, error = "Could not connect to Seerr with that URL and API key. The server log has the details." });
        }
    }

    [HttpPost("Review")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> PostReview([FromBody] ReviewRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.FilmSlug))
            return BadRequest(new { error = "filmSlug is required" });
        if (request.FilmSlug.Any(char.IsControl))
            return BadRequest(new { error = "filmSlug is not a Letterboxd film slug" });

        // A rating with no text and no rewatch is not a diary entry (Letterboxd refuses an empty
        // review): it sets the member's film rating instead, the same call RatingSyncHandler makes.
        var ratingOnly = string.IsNullOrWhiteSpace(request.ReviewText) && !request.IsRewatch && request.Rating.HasValue;
        if (string.IsNullOrWhiteSpace(request.ReviewText) && !request.IsRewatch && !ratingOnly)
            return BadRequest(new { error = "reviewText is required unless logging a rewatch or setting a rating" });

        // The diary date goes to Letterboxd and onto the history row, which needs it to read it back.
        if (!string.IsNullOrEmpty(request.Date)
            && !DateTime.TryParseExact(request.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return BadRequest(new { error = "date must be a day in the form yyyy-MM-dd" });

        if (ratingOnly)
        {
            var r = request.Rating!.Value;
            if (!double.IsFinite(r) || r < 0.5 || r > 5.0 || Math.Abs((r * 2) - Math.Round(r * 2)) > 1e-9)
                return BadRequest(new { error = "A rating must be from 0.5 to 5 stars, in half stars" });
            if (request.TmdbId is not > 0)
                return BadRequest(new { error = "A rating on its own needs the film's TMDb id" });
        }

        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
            return BadRequest(new { error = "Could not determine user" });

        // When LetterboxdUsername is set, post under that single account. When it's
        // null/empty, fan out to every enabled account for this Jellyfin user, so
        // shared TV-user setups (e.g. Lachlan + Deb) get the review on both diaries.
        List<Account> accounts;
        if (!string.IsNullOrWhiteSpace(request.LetterboxdUsername))
        {
            var single = Config.FindAccount(userId, request.LetterboxdUsername!);
            if (single == null)
                return BadRequest(new { error = $"No enabled Letterboxd account '{request.LetterboxdUsername}' for this user" });
            accounts = new List<Account> { single };
        }
        else
        {
            accounts = Config.GetEnabledAccountsForUser(userId).ToList();
            if (accounts.Count == 0)
                return BadRequest(new { error = "No Letterboxd accounts configured for this user" });
        }

        var jellyfinUsername = GetJellyfinUsername() ?? userId;
        var perAccount = new List<object>();
        var anySuccess = false;
        string? lastError = null;

        foreach (var account in accounts)
        {
            try
            {
                using var service = await LetterboxdServiceFactory.CreateAuthenticatedAsync(
                    account.LetterboxdUsername, account.LetterboxdPassword, account.RawCookies, _logger, account.UserAgent)
                    .ConfigureAwait(false);

                if (ratingOnly)
                {
                    await SetFilmRatingAsync(service, account, userId, jellyfinUsername, request).ConfigureAwait(false);
                    perAccount.Add(new { letterboxdUsername = account.LetterboxdUsername, success = true });
                    anySuccess = true;
                    continue;
                }

                // Held like the syncs hold it, so a sync of this film cannot log it between the
                // history lookup below and the review's own history row.
                using var filmLock = request.TmdbId is > 0
                    ? await FilmSyncLock.AcquireAsync(userId.ToLowerInvariant(), account.LetterboxdUsername, request.TmdbId.Value).ConfigureAwait(false)
                    : null;

                // A review of a film this plugin already logged for the account goes on that diary
                // entry. Posting it as a new entry would log a second watch, dated today.
                var loggedOn = !request.IsRewatch && string.IsNullOrEmpty(request.Date)
                    && !string.IsNullOrWhiteSpace(request.ReviewText) && request.TmdbId is > 0
                    ? SyncHistory.GetLastSuccessfulSyncDate(jellyfinUsername, request.TmdbId.Value, account.LetterboxdUsername)
                    : null;

                // Today is the server's local day, the same day a sync would log a watch on.
                var postDate = string.IsNullOrEmpty(request.Date)
                    ? Helpers.ToLocalViewingDate(DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : request.Date;
                var addedToEntry = false;
                string? note = null;
                if (loggedOn is { } entryDate)
                {
                    var entryDay = entryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    var outcome = await service.AddReviewToDiaryEntryAsync(request.TmdbId!.Value, entryDate, request.ReviewText!,
                        request.ContainsSpoilers, request.Rating).ConfigureAwait(false);
                    if (outcome == ReviewAttachResult.AlreadyReviewed)
                    {
                        // Not a sync failure, so no Failed row: nothing was sent, and the film's diary sync is unaffected.
                        lastError = $"Your Letterboxd diary entry for this film on {entryDay} already has a review, so it was left as it is. Edit that review on Letterboxd.";
                        perAccount.Add(new { letterboxdUsername = account.LetterboxdUsername, success = false, error = lastError });
                        continue;
                    }

                    addedToEntry = outcome == ReviewAttachResult.Attached;
                    postDate = entryDay;
                    if (outcome == ReviewAttachResult.Unsupported)
                        note = $"The Letterboxd website login cannot add a review to an existing diary entry, so the review was posted as a new entry dated {entryDay}.";
                    else if (outcome == ReviewAttachResult.NoEntry)
                        note = $"Letterboxd has no diary entry for this film on {entryDay}, so the review was posted as a new entry on that date.";
                }

                if (!addedToEntry)
                    await service.PostReviewAsync(request.FilmSlug, request.ReviewText, request.ContainsSpoilers, request.IsRewatch, postDate, request.Rating, request.TmdbId)
                        .ConfigureAwait(false);

                _logger.LogInformation("Posted review for {FilmSlug} by {Username} ({Where})",
                    request.FilmSlug, account.LetterboxdUsername, addedToEntry ? "on the existing diary entry" : "as a new diary entry");

                var status = request.IsRewatch ? SyncStatus.Rewatch : SyncStatus.Success;
                SyncHistory.Record(new SyncEvent
                {
                    FilmTitle = request.FilmSlug.Replace("-", " "),
                    FilmSlug = request.FilmSlug,
                    TmdbId = request.TmdbId is > 0 ? request.TmdbId.Value : 0,
                    Username = jellyfinUsername,
                    Account = account.LetterboxdUsername,
                    Timestamp = DateTime.UtcNow,
                    ViewingDate = DateTime.ParseExact(postDate, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Status = status,
                    Source = "review"
                });

                perAccount.Add(new { letterboxdUsername = account.LetterboxdUsername, success = true, addedToEntry, note });
                anySuccess = true;
            }
            catch (Exception ex) when (ratingOnly)
            {
                // Logged, not recorded as a Failed event, as RatingSyncHandler does: Failed rows feed
                // the diary sync's per-film rules, and a rating that did not land must not touch them.
                // The reply carries the one-line, length-capped form: the raw message can quote a response body.
                lastError = AuthBreaker.Sanitize(ex.Message) ?? "Failed to set the rating";
                _logger.LogError("Failed to set the rating on {FilmSlug} as {LbUser}: {Message}",
                    request.FilmSlug, account.LetterboxdUsername, lastError);
                perAccount.Add(new { letterboxdUsername = account.LetterboxdUsername, success = false, error = lastError });
            }
            catch (Exception ex)
            {
                lastError = AuthBreaker.Sanitize(ex.Message) ?? "Failed to post the review";
                _logger.LogError("Failed to post review for {FilmSlug} as {LbUser}: {Message}",
                    request.FilmSlug, account.LetterboxdUsername, ex.Message);

                SyncHistory.Record(new SyncEvent
                {
                    FilmTitle = request.FilmSlug.Replace("-", " "),
                    FilmSlug = request.FilmSlug,
                    Username = jellyfinUsername,
                    Timestamp = DateTime.UtcNow,
                    Status = SyncStatus.Failed,
                    Error = ex.Message,
                    Source = "review"
                });

                perAccount.Add(new { letterboxdUsername = account.LetterboxdUsername, success = false, error = lastError });
            }
        }

        // Mirror the rating to Jellyfin once if any account accepted it; the rating
        // belongs to the Jellyfin user, not to a specific Letterboxd account, so a
        // single writeback is correct regardless of how many accounts we posted to.
        if (anySuccess)
            WriteJellyfinRating(userId, request.TmdbId, request.Rating);

        if (!anySuccess && lastError != null)
            return BadRequest(new { error = lastError, accounts = perAccount });

        return Ok(new { success = true, ratedOnly = ratingOnly, accounts = perAccount });
    }

    /// <summary>
    /// A rating-only review: sets the member's Letterboxd film rating (not a diary entry) and
    /// records it the way <see cref="RatingSyncHandler"/> does, as a Rated event and the value
    /// last pushed, so the handler does not push the same rating again.
    /// </summary>
    private static async Task SetFilmRatingAsync(ILetterboxdService service, Account account, string userId, string jellyfinUsername, ReviewRequest request)
    {
        var tmdbId = request.TmdbId!.Value;
        var stars = request.Rating!.Value;
        // The film id must come from this same service instance: the API and the website use different ids.
        var film = await service.LookupFilmByTmdbIdAsync(tmdbId).ConfigureAwait(false);
        await service.SetFilmRatingAsync(film.Slug, film.FilmId, stars).ConfigureAwait(false);

        RatingPushStore.RecordPushed(userId, account.LetterboxdUsername, tmdbId, stars);
        // One plain line: control characters become spaces, and no " · ", which the dashboards read as the title's end.
        var title = string.Join(' ', new string((request.Title ?? string.Empty).Select(c => char.IsControl(c) ? ' ' : c).ToArray())
            .Replace('·', '-').Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (title.Length == 0) title = request.FilmSlug.Replace("-", " ");
        if (title.Length > 200) title = title[..200];
        SyncHistory.Record(new SyncEvent
        {
            FilmTitle = $"{title} · Rated {stars.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} stars",
            FilmSlug = film.Slug,
            TmdbId = tmdbId,
            Username = jellyfinUsername,
            Account = account.LetterboxdUsername,
            Timestamp = DateTime.UtcNow,
            Status = SyncStatus.Rated,
            Source = SyncEventSources.Rating
        });
    }

    /// <summary>
    /// Returns the most recent LetterboxdSync log lines from Jellyfin's log files,
    /// for in-dashboard debugging and "send me your logs" support flows.
    /// Reads only LetterboxdSync-tagged lines so users can share without leaking
    /// unrelated server activity. The plugin never logs review text, auth tokens,
    /// passwords or cookies, and the reader masks email addresses.
    /// </summary>
    [HttpGet("Logs")]
    [Authorize(Policy = "RequiresElevation")] // raw server logs name every user's Letterboxd account + watched films; admin-only
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetLogs([FromQuery] int maxLines = 500)
    {
        var cap = Math.Min(Math.Max(maxLines, 1), 5000);
        var (all, source, error) = ReadRecentLogLines();
        if (error != null)
            return Ok(new { lines = Array.Empty<string>(), source = (string?)null, error });

        var trimmed = all.Count > cap ? all.GetRange(all.Count - cap, cap) : all;
        return Ok(new { lines = trimmed, totalMatches = all.Count, returned = trimmed.Count, source });
    }

    // Matches a Jellyfin log-entry header start: "[2026-06-14 ...". Continuation lines
    // (stack frames, exception messages) do not begin this way.
    private static readonly System.Text.RegularExpressions.Regex _logHeader =
        new(@"^\[\d{4}-\d{2}-\d{2}", System.Text.RegularExpressions.RegexOptions.Compiled);

    // ANSI CSI escape sequences (colour codes) that some console sinks emit.
    private static readonly System.Text.RegularExpressions.Regex _ansi =
        new(@"\x1B\[[0-9;]*[A-Za-z]", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool IsLogHeader(string line) => _logHeader.IsMatch(line);

    private static string StripAnsi(string line) => _ansi.Replace(line, string.Empty);

    /// <summary>
    /// Reads ALL recent LetterboxdSync-tagged lines from Jellyfin's two newest main
    /// log files (covering a just-rolled-over file), untrimmed. Shared by the Logs tab
    /// and the "Send logs to developer" bundle; each caller caps as it sees fit. The
    /// plugin never logs auth tokens, passwords, cookies, or review text, and every
    /// email address is replaced with [email]. The lines still name films, shows,
    /// Jellyfin users and Letterboxd usernames.
    /// </summary>
    private (List<string> Lines, string? Source, string? Error) ReadRecentLogLines()
    {
        try
        {
            var logDir = _appPaths.LogDirectoryPath;
            if (!Directory.Exists(logDir))
                return (new List<string>(), null, "log directory not found");

            var mainLogs = Directory.GetFiles(logDir, "log_*.log")
                .OrderByDescending(f => f)
                .Take(2)
                .ToList();
            if (mainLogs.Count == 0)
                return (new List<string>(), null, "no log files");

            var lines = new List<string>();
            foreach (var path in mainLogs.AsEnumerable().Reverse())
            {
                // Force UTF-8 so non-ASCII (accented/CJK film titles) round-trips cleanly.
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs, System.Text.Encoding.UTF8);
                string? line;
                // A matched LetterboxdSync entry is often multi-line: the header line carries
                // the tag, but the exception message and "   at ..." stack frames continue on
                // following lines that DON'T carry the tag. Capture those continuation lines
                // too (until the next timestamped header) or the most useful diagnostic, the
                // stack trace, gets shredded by a per-line filter.
                var inMatch = false;
                var inLegacyReviewBody = false;
                while ((line = sr.ReadLine()) != null)
                {
                    // Emails are masked here, in the one reader behind the Logs tab, the bundle
                    // preview and the send, so all three show the same lines.
                    line = LogRedaction.RedactEmails(StripAnsi(line));
                    var isHeader = IsLogHeader(line);
                    if (isHeader)
                    {
                        inMatch = line.Contains("LetterboxdSync", StringComparison.Ordinal) ||
                                  line.Contains("Letterboxd ", StringComparison.Ordinal);
                        // Older releases logged the review reply's body, which can echo the review;
                        // such a line keeps its status and loses the body, continuation lines included.
                        inLegacyReviewBody = inMatch && LogRedaction.TryCutLegacyReviewBody(ref line);
                        if (inMatch) lines.Add(line);
                    }
                    else if (inMatch && !inLegacyReviewBody)
                    {
                        lines.Add(line); // continuation of a matched entry (stack frame / message)
                    }
                }
            }

            return (lines, string.Join(", ", mainLogs.Select(Path.GetFileName)), null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("ReadRecentLogLines failed: {Message}", ex.Message);
            return (new List<string>(), null, ex.Message);
        }
    }

    // The id a bundle carries when telemetry never made one: one per server run, so the
    // preview shows the same id the send then uploads.
    private static readonly string OneOffBundleId = Guid.NewGuid().ToString();

    private const int MaxNoteLength = 2000;

    /// <summary>
    /// Assembles the exact diagnostic bundle JSON for the calling server. Used by both
    /// the preview and the send so they cannot diverge: what the preview shows is byte
    /// for byte what the send uploads (note aside, which the user types in either path).
    /// </summary>
    private (string Json, int MatchedLines) BuildLogBundleJson(string? note)
    {
        // The ingest Worker keeps the first 2000 characters of a note (worker/src/index.ts);
        // cut it here so the preview shows what is kept, never splitting a surrogate pair.
        if (string.IsNullOrEmpty(note)) note = null;
        else if (note.Length > MaxNoteLength)
            note = note[..(char.IsHighSurrogate(note[MaxNoteLength - 1]) ? MaxNoteLength - 1 : MaxNoteLength)];

        var (allLines, source, error) = ReadRecentLogLines();
        var matched = allLines.Count;
        var lines = allLines.Count > 500 ? allLines.GetRange(allLines.Count - 500, 500) : allLines;

        // Collector status travels inside the bundle. Without it an empty capture is
        // indistinguishable (server-side) from a broken collector: bundle LBX-C1EP38
        // arrived as log_lines=[] with nothing saying whether the plugin was idle,
        // the log dir was missing, or the line filter matched nothing.
        lines.Insert(0, $"[meta] collector: files={source ?? "none"}; matched={matched}; error={error ?? "none"}");
        var collector = new { files = source ?? "none", matched, error = error ?? "none" };

        int? libraryCount;
        try
        {
            libraryCount = _libraryManager.GetCount(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                Recursive = true
            });
        }
        catch
        {
            libraryCount = null;
        }

        var telemetrySnapshot = TelemetryService.BuildPayload("logs", libraryCount);
        var instanceId = Plugin.Instance?.Configuration?.Telemetry?.InstanceId;
        if (string.IsNullOrEmpty(instanceId))
            instanceId = OneOffBundleId;

        var json = TelemetryService.BuildLogBundleJson(
            instanceId,
            Plugin.Instance?.Version?.ToString() ?? "unknown",
            telemetrySnapshot,
            note,
            lines,
            collector);
        return (json, matched);
    }

    /// <summary>
    /// Returns the EXACT bundle that "Send logs to developer" would upload, without
    /// sending it, including the note the admin has typed so far. Backs the Logs tab's
    /// Preview so the user sees the real log lines and telemetry snapshot, not just the
    /// anonymous part. A POST so the note travels in the body, never in a URL that request
    /// and proxy logs keep.
    /// </summary>
    [HttpPost("Telemetry/PreviewLogs")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult PreviewLogs([FromBody] SendLogsRequest? request = null)
    {
        return Content(BuildLogBundleJson(request?.Note).Json, "application/json");
    }

    /// <summary>
    /// User-initiated "send logs to developer". Uploads the bundle from
    /// <see cref="BuildLogBundleJson"/> (recent log lines with emails masked, the current
    /// telemetry snapshot, versions and the admin's note) to the private telemetry backend
    /// and returns a short reference code the user can quote in a bug report. Admin-only.
    /// Unlike telemetry this is NOT anonymous (log lines name films, shows, Jellyfin users
    /// and Letterboxd usernames) and only runs on this explicit, disclosed action. Works
    /// whether or not telemetry is enabled; if no telemetry instance id exists, the bundle
    /// carries an id made once per server run, so a preview and the send that follows match.
    /// </summary>
    [HttpPost("Telemetry/SendLogs")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> SendLogs([FromBody] SendLogsRequest? request)
    {
        var (json, matched) = BuildLogBundleJson(request?.Note);
        var code = await TelemetryService.PostLogBundleAsync(json).ConfigureAwait(false);

        if (string.IsNullOrEmpty(code))
            return BadRequest(new { error = "Could not reach the diagnostics endpoint. Check the server's internet connection and try again." });

        // Still a success (the telemetry snapshot alone can be useful), but the user
        // must know their diagnostics were blank, otherwise they quote the ref code
        // in a bug report and wait on logs that never arrived.
        string? warning = matched == 0
            ? "No LetterboxdSync entries were found in the two newest server log files, so the bundle contains no log lines. Reproduce the problem first (for example, run a sync), then send logs again."
            : null;

        return Ok(new { refCode = code, warning });
    }

    /// <summary>
    /// Returns the calling user's stored Jellyfin rating for an item, both raw
    /// (1-10) and mapped to Letterboxd half-stars, so the review modal can
    /// pre-fill its star widget. Movies resolve by TMDb id; TV resolves the
    /// series by TMDb id, or a single episode when season and episode numbers
    /// are supplied (matching how Serializd sync sources episode vs show
    /// ratings). Always 200 with null fields when the item or rating is absent:
    /// the pre-fill fetch must never surface an error in the modal.
    /// </summary>
    [HttpGet("ItemRating")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult GetItemRating(
        [FromQuery] int? tmdbId = null,
        [FromQuery] bool isShow = false,
        [FromQuery] int? seasonNumber = null,
        [FromQuery] int? episodeNumber = null)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
            return BadRequest(new { error = "Could not determine user" });

        var none = Ok(new { rating = (double?)null, stars = (double?)null });
        if (!tmdbId.HasValue || tmdbId.Value <= 0)
            return none;

        var user = _userManager.GetUsers().FirstOrDefault(u => u.Id.ToString("N") == userId);
        if (user == null)
            return none;

        BaseItem? item;
        if (!isShow)
            item = FindMovieByTmdbId(user, tmdbId.Value);
        else if (seasonNumber.HasValue && episodeNumber.HasValue)
            item = FindEpisodeByTmdbId(user, tmdbId.Value, seasonNumber.Value, episodeNumber.Value);
        else
            item = FindSeriesByTmdbId(user, tmdbId.Value);

        if (item == null)
            return none;

        var stored = _userDataManager.GetUserData(user, item)?.Rating;
        if (!stored.HasValue || stored.Value <= 0)
            return none;

        return Ok(new { rating = (double?)stored.Value, stars = Helpers.MapRating(stored.Value) });
    }

    /// <summary>
    /// Resolve a library movie by TMDb id for the given user. Shared by the
    /// rating writeback and the ItemRating read path so the two cannot drift.
    /// </summary>
    private BaseItem? FindMovieByTmdbId(User user, int tmdbId)
        => FindByTmdbId(user, BaseItemKind.Movie, tmdbId).FirstOrDefault();

    private BaseItem? FindSeriesByTmdbId(User user, int tmdbId)
        => FindByTmdbId(user, BaseItemKind.Series, tmdbId).FirstOrDefault();

    /// <summary>
    /// Resolve an episode by series TMDb id + season/episode number. The series
    /// TMDb id lives on the parent Series entity, so the series is resolved first
    /// and its episodes are then queried by number.
    /// </summary>
    private BaseItem? FindEpisodeByTmdbId(User user, int seriesTmdbId, int seasonNumber, int episodeNumber)
    {
        var seriesIds = FindByTmdbId(user, BaseItemKind.Series, seriesTmdbId).Select(s => s.Id).ToArray();
        if (seriesIds.Length == 0)
            return null;

        return _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            IsVirtualItem = false,
            Recursive = true,
            AncestorIds = seriesIds,
            ParentIndexNumber = seasonNumber,
            IndexNumber = episodeNumber
        }).OfType<MediaBrowser.Controller.Entities.TV.Episode>()
          // Re-checked in memory like FindByTmdbId: the episode must belong to the matched series.
          .FirstOrDefault(ep => seriesIds.Contains(ep.SeriesId)
              && ep.ParentIndexNumber == seasonNumber && ep.IndexNumber == episodeNumber);
    }

    /// <inheritdoc cref="TmdbLibraryLookup.FindByTmdbId"/>
    private IEnumerable<BaseItem> FindByTmdbId(User user, BaseItemKind kind, int tmdbId)
        => TmdbLibraryLookup.FindByTmdbId(_libraryManager, user, kind, tmdbId);

    /// <summary>
    /// Mirror the dashboard review's star rating into Jellyfin's UserItemData.Rating
    /// so it survives plugin uninstall and is visible to other clients/plugins.
    /// Always overwrites: posting a review is the user's latest input, so it wins.
    /// No-op if the review had no rating, no TmdbId, or the film isn't in the library.
    /// </summary>
    private void WriteJellyfinRating(string userId, int? tmdbId, double? letterboxdRating)
    {
        if (!tmdbId.HasValue || !letterboxdRating.HasValue)
            return;

        var jellyfinRating = Helpers.LetterboxdToJellyfinRating(letterboxdRating);
        if (!jellyfinRating.HasValue)
            return;

        var user = _userManager.GetUsers().FirstOrDefault(u => u.Id.ToString("N") == userId);
        if (user == null)
            return;

        var movie = FindMovieByTmdbId(user, tmdbId.Value);

        if (movie == null)
        {
            _logger.LogDebug("Skipping Jellyfin rating writeback: TMDb {TmdbId} not in library", tmdbId.Value);
            return;
        }

        try
        {
            var userData = _userDataManager.GetUserData(user, movie);
            if (userData == null) return;

            userData.Rating = jellyfinRating;
            // Import, not UpdateUserRating: the value originates on the Letterboxd side (the review
            // post already carried it there), and RatingSyncHandler ignores Import saves, so this
            // mirror can never echo back out as a second push.
            _userDataManager.SaveUserData(user, movie, userData, UserDataSaveReason.Import, CancellationToken.None);

            _logger.LogInformation("Mirrored Letterboxd rating {LbRating} -> Jellyfin {JfRating} for {Title} ({UserId})",
                letterboxdRating.Value, jellyfinRating.Value, movie.Name, userId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to write Jellyfin rating for TMDb {TmdbId}: {Message}", tmdbId.Value, ex.Message);
        }
    }
}

public class SendLogsRequest
{
    /// <summary>Optional free-text note from the user describing what went wrong.</summary>
    public string? Note { get; set; }
}

public class ReviewRequest
{
    public string FilmSlug { get; set; } = string.Empty;
    public string? ReviewText { get; set; }
    public bool ContainsSpoilers { get; set; }
    public bool IsRewatch { get; set; }
    public string? Date { get; set; }
    public double? Rating { get; set; }
    public int? TmdbId { get; set; }

    /// <summary>Optional film title, used to label a rating-only review in the activity list.</summary>
    public string? Title { get; set; }

    /// <summary>
    /// Optional. When set, the review is posted only to that Letterboxd account.
    /// When null/empty, the review is fanned out to every enabled Letterboxd account
    /// for the calling Jellyfin user.
    /// </summary>
    public string? LetterboxdUsername { get; set; }
}
