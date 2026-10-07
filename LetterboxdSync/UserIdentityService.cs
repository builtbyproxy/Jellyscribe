using System;
using System.Threading;
using System.Threading.Tasks;
using LetterboxdSync.Serializd;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

public class UserIdentityService : IHostedService
{
    private readonly IUserManager _userManager;
    private readonly ILogger<UserIdentityService> _logger;

    public UserIdentityService(IUserManager userManager, ILogger<UserIdentityService> logger)
    {
        _userManager = userManager;
        _logger = logger;
    }

    // One stamping pass per process, however many times the host starts the service.
    private static int _stampingStarted;

    /// <summary>The background stamping pass, for tests to await. Null until it starts.</summary>
    internal Task? Stamping { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        SyncHistory.UserIdResolver = name => _userManager.GetUserByName(name)?.Id.ToString("N");

        // Stamping reads (and may rewrite) the whole history files, which can be large, so it
        // runs off the startup path instead of holding up Jellyfin's start.
        if (Interlocked.Exchange(ref _stampingStarted, 1) == 0)
            Stamping = Task.Run(StampExistingHistory, CancellationToken.None);

        return Task.CompletedTask;
    }

    private void StampExistingHistory()
    {
        try
        {
            var films = SyncHistory.StampMissingUserIds();
            var episodes = SerializdActivity.StampMissingUserIds();
            if (films + episodes > 0)
                _logger.LogInformation(
                    "Linked {Films} Letterboxd and {Episodes} Serializd history entries to their Jellyfin user ids",
                    films, episodes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not link existing history entries to Jellyfin user ids");
        }
    }

    internal static void ResetForTesting() => Interlocked.Exchange(ref _stampingStarted, 0);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
