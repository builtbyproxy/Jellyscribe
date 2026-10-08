using System;
using System.Threading.Tasks;

namespace LetterboxdSync;

/// <summary>
/// Letterboxd answered the TMDb lookup and has no film for the id (an empty result on the API
/// path, a 404 on the website path). Unlike a network error or a block, retrying gives the same
/// answer, so the runner records it as a permanent failure that counts toward abandoning the film.
/// </summary>
public sealed class FilmNotFoundException : Exception
{
    public FilmNotFoundException(int tmdbId, string message) : base(message)
    {
        TmdbId = tmdbId;
    }

    public int TmdbId { get; }
}

/// <summary>
/// The diary check for a film could not be completed (an error status, a Cloudflare challenge).
/// Thrown instead of returning "no entries", because "no entries" makes the caller log the film,
/// which posts a duplicate whenever the film was in fact already logged. Callers record a
/// transient failure and try again on a later run.
/// </summary>
public sealed class DiaryCheckFailedException : Exception
{
    public DiaryCheckFailedException(string message, bool blocked = false) : base(message)
    {
        Blocked = blocked;
    }

    /// <summary>True when the check failed because Letterboxd (Cloudflare) blocked the request.</summary>
    public bool Blocked { get; }
}

/// <summary>
/// Letterboxd's website refused a request with a block (a 403, usually Cloudflare), after the
/// client's own backoff. Every later request in the run is likely to be refused the same way.
/// </summary>
public sealed class LetterboxdBlockedException : Exception
{
    public LetterboxdBlockedException(string message) : base(message)
    {
    }
}

/// <summary>
/// The official API's token endpoint answered the login with an error status. The message is the
/// same text callers have always logged.
/// </summary>
public sealed class LetterboxdApiAuthException : Exception
{
    public LetterboxdApiAuthException(System.Net.HttpStatusCode statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }

    public System.Net.HttpStatusCode StatusCode { get; }

    /// <summary>True for an answer that will not change on a retry soon (400, 401, 403).</summary>
    public bool IsRejection => StatusCode is System.Net.HttpStatusCode.BadRequest
        or System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden;
}

/// <summary>Serializd answered a request with an error status (after the client's own retries).</summary>
public sealed class SerializdRequestException : Exception
{
    public SerializdRequestException(System.Net.HttpStatusCode statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }

    public System.Net.HttpStatusCode StatusCode { get; }
}

public static class SyncErrors
{
    /// <summary>
    /// True when the failure was Letterboxd refusing the request (a 403, a Cloudflare challenge,
    /// or a 429 whose Retry-After is longer than the clients wait) rather than anything about the
    /// film. Every later request in the run would be refused the same way.
    /// </summary>
    public static bool IsBlock(Exception ex) => ex switch
    {
        LetterboxdBlockedException => true,
        DiaryCheckFailedException d => d.Blocked,
        System.Net.Http.HttpRequestException h => h.StatusCode
            is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests,
        _ => false,
    };

    /// <summary>
    /// True when a Serializd failure says the service is down or refusing the account (no
    /// connection, a timeout, 5xx, 429, 401, 403), rather than something about one item (a 400, a
    /// season it does not have). Only these count toward stopping an account's catch-up, so a few
    /// bad items at the head of the queue never starve the rest.
    /// </summary>
    public static bool IsServiceFailure(Exception ex) => ex switch
    {
        SerializdRequestException r => IsServiceStatus(r.StatusCode),
        System.Net.Http.HttpRequestException h => h.StatusCode is not { } status || IsServiceStatus(status),
        TaskCanceledException or TimeoutException => true,
        _ => false,
    };

    private static bool IsServiceStatus(System.Net.HttpStatusCode status)
        => (int)status >= 500 || status is System.Net.HttpStatusCode.TooManyRequests
            or System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden;
}
