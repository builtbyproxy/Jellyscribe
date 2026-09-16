using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LetterboxdSync;
using MediaBrowser.Controller.Playlists;
using NSubstitute;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// Jellyfin 12.0 inserted an <c>int? position</c> parameter into
/// <c>IPlaylistManager.AddItemToPlaylistAsync</c>, so the 3-argument call we compile against
/// (10.11 SDK) throws MissingMethodException on any Jellyfin 12 server and watchlist-to-playlist
/// sync dies there. These tests drive the overload resolution with stand-in types carrying each
/// signature, because the test project also compiles against the 10.11 SDK and so cannot reference
/// the real 12.x interface.
/// </summary>
public class PlaylistManagerCompatTests
{
    private static readonly Guid PlaylistId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid UserId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>Shape of Jellyfin 10.11.x.</summary>
    private sealed class LegacyManager
    {
        public Guid? SeenPlaylistId;
        public IReadOnlyCollection<Guid>? SeenItemIds;
        public Guid? SeenUserId;
        public int Calls;

        public Task AddItemToPlaylistAsync(Guid playlistId, IReadOnlyCollection<Guid> itemIds, Guid userId)
        {
            Calls++;
            SeenPlaylistId = playlistId;
            SeenItemIds = itemIds;
            SeenUserId = userId;
            return Task.CompletedTask;
        }
    }

    /// <summary>Shape of Jellyfin 12.x.</summary>
    private sealed class PositionalManager
    {
        public Guid? SeenPlaylistId;
        public IReadOnlyCollection<Guid>? SeenItemIds;
        public int? SeenPosition = -1; // sentinel: -1 means "never assigned", so null is observable
        public Guid? SeenUserId;
        public int Calls;

        public Task AddItemToPlaylistAsync(Guid playlistId, IReadOnlyCollection<Guid> itemIds, int? position, Guid userId)
        {
            Calls++;
            SeenPlaylistId = playlistId;
            SeenItemIds = itemIds;
            SeenPosition = position;
            SeenUserId = userId;
            return Task.CompletedTask;
        }
    }

    /// <summary>Both shapes present: the 12.x one must win, since a 12 host has only that one.</summary>
    private sealed class BothManager
    {
        public string? Chosen;

        public Task AddItemToPlaylistAsync(Guid playlistId, IReadOnlyCollection<Guid> itemIds, Guid userId)
        {
            Chosen = "legacy";
            return Task.CompletedTask;
        }

        public Task AddItemToPlaylistAsync(Guid playlistId, IReadOnlyCollection<Guid> itemIds, int? position, Guid userId)
        {
            Chosen = "positional";
            return Task.CompletedTask;
        }
    }

    private sealed class UnrecognisedManager
    {
        public Task AddItemToPlaylistAsync(string playlistId) => Task.CompletedTask;
    }

    [Fact]
    public async Task Jellyfin1011Shape_CallsThreeArgOverloadWithUserId()
    {
        var target = new LegacyManager();
        var invoke = PlaylistManagerCompat.BuildInvoker(typeof(LegacyManager));

        await invoke(target, PlaylistId, new[] { Guid.Empty }, UserId);

        Assert.Equal(1, target.Calls);
        Assert.Equal(PlaylistId, target.SeenPlaylistId);
        Assert.Equal(UserId, target.SeenUserId);
    }

    /// <summary>
    /// The regression this whole shim exists for. Also pins position == null: Jellyfin documents it
    /// as "0-based index where to place the items or at the end if null", so null is what reproduces
    /// the 10.11 append behaviour. Passing 0 would silently prepend on every sync.
    /// </summary>
    [Fact]
    public async Task Jellyfin12Shape_CallsFourArgOverloadAndAppends()
    {
        var target = new PositionalManager();
        var invoke = PlaylistManagerCompat.BuildInvoker(typeof(PositionalManager));

        await invoke(target, PlaylistId, new[] { Guid.Empty }, UserId);

        Assert.Equal(1, target.Calls);
        Assert.Equal(PlaylistId, target.SeenPlaylistId);
        Assert.Equal(UserId, target.SeenUserId);
        Assert.Null(target.SeenPosition);
    }

    [Fact]
    public async Task BothShapesPresent_PrefersTheJellyfin12Overload()
    {
        var target = new BothManager();
        var invoke = PlaylistManagerCompat.BuildInvoker(typeof(BothManager));

        await invoke(target, PlaylistId, new[] { Guid.Empty }, UserId);

        Assert.Equal("positional", target.Chosen);
    }

    /// <summary>If Jellyfin changes this again, fail with something that names the problem.</summary>
    [Fact]
    public void UnknownShape_ThrowsNamingBothExpectedSignatures()
    {
        var ex = Assert.Throws<MissingMethodException>(
            () => PlaylistManagerCompat.BuildInvoker(typeof(UnrecognisedManager)));

        Assert.Contains("AddItemToPlaylistAsync", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Jellyfin 12", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Exceptions must surface as themselves, not wrapped in TargetInvocationException: the
    /// try/catch around playlist work upstream is written for real exceptions.
    /// </summary>
    [Fact]
    public async Task TargetThrows_ExceptionIsUnwrapped()
    {
        var invoke = PlaylistManagerCompat.BuildInvoker(typeof(ThrowingManager));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => invoke(new ThrowingManager(), PlaylistId, new[] { Guid.Empty }, UserId));

        Assert.Equal("boom", ex.Message);
    }

    private sealed class ThrowingManager
    {
        public Task AddItemToPlaylistAsync(Guid playlistId, IReadOnlyCollection<Guid> itemIds, Guid userId)
            => throw new InvalidOperationException("boom");
    }

    /// <summary>
    /// The one test that exercises the REAL Jellyfin interface rather than a stand-in, and so the
    /// only one that can catch the shim binding the wrong thing on a real server.
    /// <para>
    /// It asserts against whatever the SDK in use actually offers, so it is meaningful on both
    /// lines without being version-specific: built against 10.11 it proves we bind the 3-argument
    /// overload, and when the CI Jellyfin 12 probe runs the same test against the 12 SDK it proves
    /// we bind the 4-argument one and pass a null position (append). Previously this only asserted
    /// that resolution returned something non-null, which would have passed even if the shim picked
    /// the wrong overload or silently appended at index 0 on every sync.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RealIPlaylistManager_BindsTheOverloadThisJellyfinSdkActuallyHas()
    {
        var overloads = typeof(IPlaylistManager)
            .GetMethods()
            .Where(m => m.Name == "AddItemToPlaylistAsync")
            .ToList();
        Assert.NotEmpty(overloads);

        var target = Substitute.For<IPlaylistManager>();
        var invoke = PlaylistManagerCompat.BuildInvoker(typeof(IPlaylistManager));
        await invoke(target, PlaylistId, new[] { Guid.NewGuid() }, UserId);

        var call = Assert.Single(target.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "AddItemToPlaylistAsync"));
        var boundParameters = call.GetMethodInfo().GetParameters();
        var args = call.GetArguments();

        // Jellyfin 12 added the position parameter; prefer it whenever the SDK has it.
        var expectedArity = overloads.Any(m => m.GetParameters().Length == 4) ? 4 : 3;
        Assert.Equal(expectedArity, boundParameters.Length);

        // userId is last in both shapes, and on 12 position must be null so items append.
        Assert.Equal(UserId, args[^1]);
        if (boundParameters.Length == 4)
        {
            Assert.Equal("position", boundParameters[2].Name);
            Assert.Null(args[2]);
        }
    }
}
