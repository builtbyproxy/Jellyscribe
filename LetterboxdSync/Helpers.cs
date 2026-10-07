using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HtmlAgilityPack;

namespace LetterboxdSync;

public static class Helpers
{
    /// <summary>
    /// Key for the process-wide token caches: the account plus a SHA-256 of its secret, so a
    /// cached token is only reused or refreshed by a caller presenting the same secret. Lives in
    /// memory only; never log it.
    /// </summary>
    internal static string TokenCacheKey(string account, string secret)
        => $"{account}\n{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)))}";

    /// <summary>
    /// Map a Jellyfin rating (0-10) to a Letterboxd rating (0.5-5.0 in 0.5 steps).
    /// Returns null if the input is null or out of range. Halves round away from zero (4.5 becomes
    /// 2.5 stars), the same way the Serializd mapping rounds, so one Jellyfin rating lands on the
    /// same value on both services.
    /// </summary>
    public static double? MapRating(double? jellyfinRating)
    {
        if (!jellyfinRating.HasValue || jellyfinRating.Value <= 0)
            return null;

        var mapped = Math.Round(jellyfinRating.Value, MidpointRounding.AwayFromZero) / 2.0;
        return Math.Clamp(mapped, 0.5, 5.0);
    }

    /// <summary>
    /// Map a Letterboxd rating (0.5-5.0) to a Jellyfin rating (1-10).
    /// Returns null if the input is null or out of range.
    /// </summary>
    public static double? LetterboxdToJellyfinRating(double? letterboxdRating)
    {
        if (!letterboxdRating.HasValue || letterboxdRating.Value <= 0)
            return null;

        return Math.Clamp(letterboxdRating.Value * 2.0, 1.0, 10.0);
    }

    /// <summary>
    /// Extract a film slug from a Letterboxd film URL.
    /// e.g. "https://letterboxd.com/film/gladiator-ii/" -> "gladiator-ii"
    /// </summary>
    public static string? ExtractSlugFromUrl(string filmUrl)
    {
        if (string.IsNullOrWhiteSpace(filmUrl))
            return null;

        try
        {
            var uri = new Uri(filmUrl, UriKind.Absolute);
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (segments.Length >= 2 && segments[0].Equals("film", StringComparison.OrdinalIgnoreCase))
                return segments[1];
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Parse diary entry dates from a Letterboxd diary page HTML.
    /// Returns a list of dates found.
    /// </summary>
    public static List<DateTime> ParseDiaryDates(string html)
    {
        var dates = new List<DateTime>();
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var months = doc.DocumentNode.SelectNodes("//a[contains(@class, 'month')]");
        var days = doc.DocumentNode.SelectNodes("//a[contains(@class, 'date') or contains(@class, 'daydate')]");
        var years = doc.DocumentNode.SelectNodes("//a[contains(@class, 'year')]");

        if (months == null || days == null || years == null)
            return dates;

        var count = Math.Min(Math.Min(months.Count, days.Count), years.Count);
        for (int i = 0; i < count; i++)
        {
            var dateStr = $"{days[i].InnerText?.Trim()} {months[i].InnerText?.Trim()} {years[i].InnerText?.Trim()}";
            if (DateTime.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                dates.Add(parsed);
        }

        return dates;
    }

    /// <summary>
    /// Determine if a viewing should be marked as a rewatch.
    /// Only true if there's a prior diary entry AND it's more than 1 day before the viewing date.
    /// </summary>
    public static bool IsRewatch(DateTime? lastDiaryDate, DateTime viewingDate)
    {
        if (!lastDiaryDate.HasValue)
            return false;

        return (viewingDate.Date - lastDiaryDate.Value.Date).TotalDays > 1;
    }

    /// <summary>
    /// Determine if a viewing is a duplicate (already logged on the same date).
    /// </summary>
    public static bool IsDuplicate(DateTime? lastDiaryDate, DateTime viewingDate)
    {
        if (!lastDiaryDate.HasValue)
            return false;

        return lastDiaryDate.Value.Date == viewingDate.Date;
    }

    /// <summary>
    /// Test hook for the server's time zone. Production leaves it null and uses
    /// <see cref="TimeZoneInfo.Local"/>, the zone <see cref="DateTime.Now"/> reads.
    /// </summary>
    internal static TimeZoneInfo? LocalTimeZoneOverride { get; set; }

    /// <summary>
    /// The calendar day, in the server's own time zone, of a Jellyfin play timestamp. Jellyfin
    /// stores LastPlayedDate in UTC (often with an unspecified Kind), so taking its .Date gives
    /// the UTC day, which for an evening watch west of UTC (or a morning watch east of it) is a
    /// different day from the one the viewer lived. Both the scheduled sync and the real-time
    /// sync use this one function, so a watch is logged, and recognised as already logged, on
    /// the same day whichever path reaches it first.
    /// </summary>
    public static DateTime ToLocalViewingDate(DateTime playedUtc)
    {
        var utc = playedUtc.Kind switch
        {
            DateTimeKind.Utc => playedUtc,
            DateTimeKind.Local => playedUtc.ToUniversalTime(),
            _ => DateTime.SpecifyKind(playedUtc, DateTimeKind.Utc),
        };
        var zone = LocalTimeZoneOverride ?? TimeZoneInfo.Local;
        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(utc, zone).Date, DateTimeKind.Unspecified);
    }

    // Some clients send an explicit but bogus datePlayed (e.g. an uninitialized JS Date
    // defaulting to epoch) when marking an item watched manually without a real timestamp.
    // That leaves LastPlayedDate non-null, so it slips past a plain HasValue check. No real
    // Jellyfin server predates this floor, so anything at or before it isn't a genuine watch.
    private static readonly DateTime EpochFloor = new(1971, 1, 1);

    /// <summary>
    /// Whether a Jellyfin LastPlayedDate looks like a genuine watch instant rather than a
    /// missing or degenerate one (null, or epoch-adjacent e.g. 1970-01-01, see issue #106).
    /// </summary>
    public static bool HasPlausibleWatchDate(DateTime? lastPlayedDate)
    {
        return lastPlayedDate.HasValue && lastPlayedDate.Value >= EpochFloor;
    }
}
