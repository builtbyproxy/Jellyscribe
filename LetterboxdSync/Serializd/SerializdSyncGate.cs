using System.Threading;

namespace LetterboxdSync.Serializd;

/// <summary>
/// Serializes Serializd sync runs against each other (scheduled + on-demand) without
/// contending with the Letterboxd <see cref="SyncGate"/>. The two services hit different
/// origins, so a Serializd run and a Letterboxd run may proceed concurrently.
/// </summary>
internal static class SerializdSyncGate
{
    public static readonly SemaphoreSlim Instance = new(1, 1);

    public static bool IsRunning => Instance.CurrentCount == 0;
}
