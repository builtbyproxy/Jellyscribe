namespace LetterboxdSync.Serializd;

/// <summary>
/// Constants for the Serializd private API. The API host (Render) is NOT behind
/// Cloudflare, unlike www.serializd.com, so this client needs no cookie jar,
/// challenge handling, or browser-style backoff, just the three headers the web
/// app sends plus a bearer token after login.
/// </summary>
internal static class SerializdApiConstants
{
    /// <summary>Serializd API base. All endpoints hang off this.</summary>
    internal const string BaseUrl = "https://serializd.onrender.com/api";

    /// <summary>Sent as Origin/Referer on every request (the web app's own origin).</summary>
    internal const string FrontPageUrl = "https://www.serializd.com";

    /// <summary>Value the web app sends in the X-Requested-With header.</summary>
    internal const string AppId = "serializd_vercel";

    internal const string UserAgent = "LetterboxdSync/1.0 (+https://jellyscribe.dev)";
}
