using System;

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

public static class SyncErrors
{
    /// <summary>
    /// True when the failure was Letterboxd refusing the request (a 403 or a Cloudflare
    /// challenge) rather than anything about the film.
    /// </summary>
    public static bool IsBlock(Exception ex) => ex switch
    {
        LetterboxdBlockedException => true,
        DiaryCheckFailedException d => d.Blocked,
        System.Net.Http.HttpRequestException h => h.StatusCode == System.Net.HttpStatusCode.Forbidden,
        _ => false,
    };
}
