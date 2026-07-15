using System;

namespace LetterboxdSync.Serializd;

/// <summary>
/// Maps a Jellyfin user rating to a Serializd rating. Both are on a 0..10 scale
/// (Serializd shows 1..10 as half-stars, 10 = 5★), so it's a round-and-clamp with
/// "0 / unset ⇒ unrated (omit)".
/// </summary>
public static class SerializdRating
{
    public static int? FromJellyfin(double? jellyfinRating)
    {
        if (jellyfinRating is not > 0)
            return null;

        var rounded = (int)Math.Round(jellyfinRating.Value, MidpointRounding.AwayFromZero);
        return Math.Clamp(rounded, 1, 10);
    }
}
