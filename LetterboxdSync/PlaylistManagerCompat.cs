using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Playlists;

namespace LetterboxdSync;

/// <summary>
/// Calls <c>IPlaylistManager.AddItemToPlaylistAsync</c> across the Jellyfin 10.11 and 12 signatures.
/// <para>
/// Jellyfin 12.0 inserted an optional <c>position</c> parameter:
/// <code>
/// 10.11.x : AddItemToPlaylistAsync(Guid playlistId, IReadOnlyCollection&lt;Guid&gt; itemIds, Guid userId)
/// 12.x    : AddItemToPlaylistAsync(Guid playlistId, IReadOnlyCollection&lt;Guid&gt; itemIds, int? position, Guid userId)
/// </code>
/// We compile against the 10.11 SDK (see the floor policy in LetterboxdSync.csproj), so a direct
/// call emits a reference to the 3-argument method. On a Jellyfin 12 server that method does not
/// exist and the call throws <see cref="MissingMethodException"/> the moment a watchlist sync tries
/// to add films to its playlist. That is the same failure mode as the 10.11.9 <c>IUserManager.Users</c>
/// incident, and it is why watchlist-to-playlist sync was broken on every Jellyfin 12 server.
/// </para>
/// <para>
/// The usual fix, bumping the SDK, is not available here: the 12.x packages are net10.0-only, so
/// adopting them forces net10.0 and drops every Jellyfin 10.11 user. Binding the method reflectively
/// instead keeps one net9.0 build working correctly on both lines.
/// </para>
/// <para>
/// <c>position</c> is documented as "0-based index where to place the items or at the end if null",
/// so passing <c>null</c> on 12.x reproduces the 10.11 append behaviour exactly.
/// </para>
/// <para>
/// Both paths go through reflection deliberately. Emitting a direct call for the 10.11 shape would
/// put an unresolvable member reference in the IL of whichever method contains it, and the JIT
/// resolves those per containing method rather than per call, which makes "we never take that
/// branch on 12" a subtler guarantee than it looks. Going through <see cref="MethodInfo"/> for both
/// removes that question entirely. The cost is one reflective invoke per sync, against a call that
/// does database work.
/// </para>
/// </summary>
internal static class PlaylistManagerCompat
{
    private const string MethodName = "AddItemToPlaylistAsync";

    private static readonly Lazy<Func<object, Guid, IReadOnlyCollection<Guid>, Guid, Task>> Invoker =
        new(() => BuildInvoker(typeof(IPlaylistManager)), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Adds items to a playlist, appending them, on whichever Jellyfin version is hosting us.
    /// </summary>
    public static Task AddItemToPlaylistAsync(
        IPlaylistManager playlistManager, Guid playlistId, IReadOnlyCollection<Guid> itemIds, Guid userId)
        => Invoker.Value(playlistManager, playlistId, itemIds, userId);

    /// <summary>
    /// Picks the right overload off <paramref name="playlistManagerType"/> and returns a delegate that
    /// calls it. Exposed internally (rather than inlined) so tests can drive it with stand-in types
    /// carrying each signature: the test project compiles against the 10.11 SDK, so the real 12.x
    /// interface is not available to test against.
    /// </summary>
    internal static Func<object, Guid, IReadOnlyCollection<Guid>, Guid, Task> BuildInvoker(Type playlistManagerType)
    {
        var candidates = playlistManagerType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == MethodName)
            .ToList();

        // Jellyfin 12: (Guid playlistId, IReadOnlyCollection<Guid> itemIds, int? position, Guid userId).
        // Preferred when present, since a 12 host will not have the older shape at all.
        var withPosition = candidates.FirstOrDefault(m => Matches(m, typeof(Guid), typeof(IReadOnlyCollection<Guid>), typeof(int?), typeof(Guid)));
        if (withPosition != null)
        {
            return (target, playlistId, itemIds, userId) =>
                Call(withPosition, target, new object?[] { playlistId, itemIds, null, userId });
        }

        // Jellyfin 10.11: (Guid playlistId, IReadOnlyCollection<Guid> itemIds, Guid userId).
        var legacy = candidates.FirstOrDefault(m => Matches(m, typeof(Guid), typeof(IReadOnlyCollection<Guid>), typeof(Guid)));
        if (legacy != null)
        {
            return (target, playlistId, itemIds, userId) =>
                Call(legacy, target, new object?[] { playlistId, itemIds, userId });
        }

        // Neither shape: Jellyfin changed this again. Fail with something that names the problem
        // rather than a bare MissingMethodException from deep inside a sync run.
        var seen = candidates.Count == 0
            ? "(no overloads found)"
            : string.Join("; ", candidates.Select(m => string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))));
        throw new MissingMethodException(
            $"No supported {MethodName} overload on {playlistManagerType.FullName}. " +
            $"Expected the Jellyfin 10.11 (Guid, IReadOnlyCollection<Guid>, Guid) or Jellyfin 12 " +
            $"(Guid, IReadOnlyCollection<Guid>, int?, Guid) shape, found: {seen}");
    }

    private static bool Matches(MethodInfo method, params Type[] expected)
    {
        var actual = method.GetParameters();
        if (actual.Length != expected.Length) return false;
        for (var i = 0; i < expected.Length; i++)
        {
            if (actual[i].ParameterType != expected[i]) return false;
        }

        return true;
    }

    /// <summary>
    /// Invokes and unwraps. Reflection wraps anything the target throws synchronously in a
    /// <see cref="TargetInvocationException"/>; callers should see the real exception, and the
    /// existing try/catch around playlist work is written for real exceptions.
    /// </summary>
    private static Task Call(MethodInfo method, object target, object?[] args)
    {
        try
        {
            return (Task)method.Invoke(target, args)!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }
    }
}
