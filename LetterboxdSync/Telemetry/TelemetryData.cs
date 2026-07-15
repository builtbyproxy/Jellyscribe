using System;

namespace LetterboxdSync.Configuration;

/// <summary>
/// Persisted telemetry state. Lives inside <see cref="PluginConfiguration"/> so window
/// counters, error states, and ping timestamps survive container restarts, self-hosters
/// restart constantly, and in-memory-only counters would systematically undercount.
/// Everything here is local until the admin opts in; nothing is ever sent while
/// <see cref="Enabled"/> is false.
/// </summary>
public class TelemetryData
{
    /// <summary>Master opt-in switch. Default off; no network request may occur while false.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Random instance identity, generated server-side when telemetry is first enabled
    /// (see Plugin.UpdateConfiguration). Never derived from hardware, network, or Jellyfin
    /// identifiers. Regenerating unlinks the identifier going forward; old rows remain.
    /// </summary>
    public string? InstanceId { get; set; }

    /// <summary>One-time opt-in banner on the dashboard tab: set true on Enable OR No-thanks.</summary>
    public bool BannerDismissed { get; set; }

    /// <summary>
    /// Minute-of-day (0..719 over a 12 h window) this instance starts sending on an eligible
    /// day, per-instance jitter so the fleet doesn't thunder-herd the ingest endpoint.
    /// Generated alongside <see cref="InstanceId"/>.
    /// </summary>
    public int JitterMinutes { get; set; }

    public DateTime? LastWeeklyPingUtc { get; set; }

    /// <summary>Daily rate-limit marker for error-transition pings.</summary>
    public DateTime? LastTransitionPingUtc { get; set; }

    /// <summary>
    /// A transition fired while the daily cap was spent; one consolidated transition ping
    /// goes out when the window reopens. Deferred, never dropped.
    /// </summary>
    public bool TransitionQueued { get; set; }

    /// <summary>
    /// Lifetime successful-sync counter. Unlike the window counters below it is NEVER
    /// reset: the syncs_ever bucket it feeds exists to distinguish "installed but never
    /// completed a sync" (an activation failure) from "no syncs this window" (a quiet
    /// week), which syncs_per_week alone cannot. Leaves the box only as a coarse bucket
    /// (0 / 1-10 / 11-100 / 100+), same as every other count.
    /// </summary>
    public int LifetimeSyncs { get; set; }

    /// <summary>Lifetime successful-sync counter for the Serializd (TV) diary, the same
    /// "installed but never activated" signal as <see cref="LifetimeSyncs"/> but tracked
    /// separately since the two diaries are fully independent.</summary>
    public int LifetimeTvSyncs { get; set; }

    // Window counters, measured since the last successful weekly ping (never cumulative,
    // so canary rates are per-period by construction). Reset on successful weekly ping.
    public int WindowSyncs { get; set; }
    public int WindowTvSyncs { get; set; }
    public int WindowSkipped { get; set; }
    public int WindowErrCloudflare { get; set; }
    public int WindowErrAuth { get; set; }
    public int WindowErrTmdb { get; set; }
    public int WindowErrJellyseerr { get; set; }
    public int WindowErrRateLimit { get; set; }
    public int WindowErrServerError { get; set; }
    public int WindowErrWriteFailure { get; set; }
    public int WindowErrParse { get; set; }
    public int WindowErrOther { get; set; }

    // Per-category failing state for rising-edge transition detection. A category firing
    // while clean flips to failing and triggers a transition ping; recovery (any successful
    // sync) flips all back to clean and is visible in the next weekly payload.
    public bool StateCloudflare { get; set; }
    public bool StateAuth { get; set; }
    public bool StateTmdb { get; set; }
    public bool StateJellyseerr { get; set; }
    public bool StateRateLimit { get; set; }
    public bool StateServerError { get; set; }
    public bool StateWriteFailure { get; set; }
    public bool StateParse { get; set; }
    public bool StateOther { get; set; }
}
