namespace LetterboxdSync;

/// <summary>
/// Telemetry backend endpoints and the publishable ingest key.
/// The key is the Supabase anon key: publishable by design (like a Plausible domain),
/// RLS denies it all direct table access, and /ingest is the sole write path. Its
/// compromise is bounded to junk rows — never reads, never privacy.
/// Mutable (not const) so tests can point the sender at a fake.
/// </summary>
internal static class TelemetryConstants
{
    /// <summary>Current payload schema version; the ingest function rejects unknown versions.</summary>
    public const int SchemaVersion = 1;

    public static string IngestUrl = "https://PENDING-PROJECT-REF.supabase.co/functions/v1/ingest";

    public static string AnonKey = "PENDING-ANON-KEY";
}
