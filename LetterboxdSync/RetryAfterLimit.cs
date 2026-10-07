using System;
using System.Net.Http.Headers;

namespace LetterboxdSync;

/// <summary>
/// How long the Letterboxd and Serializd API clients wait on a 429 before their one retry.
/// A longer Retry-After than <see cref="Max"/> fails the request instead: a sync waiting minutes
/// on one item holds the sync gate and a scheduled-task slot, and the next run retries the item.
/// </summary>
internal static class RetryAfterLimit
{
    internal static readonly TimeSpan Max = TimeSpan.FromSeconds(60);

    /// <summary>The wait when a 429 carries no Retry-After.</summary>
    internal static readonly TimeSpan Default = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The wait a 429's Retry-After asks for (seconds or an HTTP date), or null when it is longer
    /// than <see cref="Max"/>. A date already past waits nothing.
    /// </summary>
    internal static TimeSpan? Wait(RetryConditionHeaderValue? retryAfter, DateTimeOffset? now = null)
    {
        var wait = retryAfter?.Delta
            ?? (retryAfter?.Date is DateTimeOffset date ? date - (now ?? DateTimeOffset.UtcNow) : Default);
        if (wait < TimeSpan.Zero)
            wait = TimeSpan.Zero;
        return wait > Max ? null : wait;
    }
}
