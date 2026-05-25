using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using LetterboxdSync;
using MediaBrowser.Controller.Library;
using NSubstitute;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// Regression coverage for <see cref="UserManagerExtensions.GetAllUsers"/>, the
/// reflection shim that survives the Jellyfin 10.11.9 IUserManager.Users
/// signature change reported in issue #46.
/// </summary>
public class UserManagerExtensionsTests
{
    [Fact]
    public void GetAllUsers_NullUserManager_ReturnsEmpty()
    {
        IUserManager? userManager = null;

        var users = userManager.GetAllUsers().ToList();

        Assert.Empty(users);
    }

    [Fact]
    public void GetAllUsers_UsersReturnsList_ReturnsSameUsers()
    {
        // Happy path against the IEnumerable<User> signature we compile against.
        // Construct real User entities (NSubstitute can't proxy User, no
        // parameterless ctor) and stub Users with them.
        var u1 = new User("alice", "test-provider-id", "test-reset-id");
        var u2 = new User("bob", "test-provider-id", "test-reset-id");
        var userManager = Substitute.For<IUserManager>();
        userManager.Users.Returns(new[] { u1, u2 });

        var result = userManager.GetAllUsers().ToList();

        Assert.Equal(2, result.Count);
        Assert.Contains(result, u => u.Username == "alice");
        Assert.Contains(result, u => u.Username == "bob");
    }

    [Fact]
    public void GetAllUsers_UsersGetterThrows_SwallowsAndReturnsEmpty()
    {
        // Models the production failure mode reported in #46: the property's
        // getter throws (MissingMethodException on Jellyfin 10.11.9+ when the
        // plugin is compiled against 10.11.0). The shim catches and falls back
        // to empty, so callers don't 500 / crash scheduled tasks.
        var userManager = Substitute.For<IUserManager>();
        userManager.Users.Returns(_ => throw new MissingMethodException(
            "Method not found: 'System.Collections.Generic.IEnumerable`1<...User> IUserManager.get_Users()'."));

        var users = userManager.GetAllUsers().ToList();

        Assert.Empty(users);
    }

    [Fact]
    public void GetAllUsers_UsersReturnsEmptyEnumeration_ReturnsEmpty()
    {
        // No users configured on the Jellyfin server: shim returns the same
        // empty enumeration rather than null or anything else surprising.
        var userManager = Substitute.For<IUserManager>();
        userManager.Users.Returns(Array.Empty<User>());

        var users = userManager.GetAllUsers().ToList();

        Assert.Empty(users);
    }

    [Fact]
    public void GetAllUsers_CrossSignatureCompatibility_ReadOnlyListCastSucceeds()
    {
        // Proves the cast-to-IEnumerable<User> in the shim accepts an
        // IReadOnlyList<User>, which is what IUserManager.Users returns from
        // Jellyfin 10.11.9 onward. We can't change the static signature of
        // IUserManager.Users in this test (it's IEnumerable<User> in the SDK
        // we compile against), but we can prove the underlying type system
        // contract: the value the shim's reflection call would receive on a
        // 10.11.9+ host (an IReadOnlyList<User>) is assignable to the
        // IEnumerable<User> we cast to.
        IReadOnlyList<User> newSignatureValue = new List<User>
        {
            new User("alice", "test-provider-id", "test-reset-id")
        };

        // Mirror the cast UserManagerExtensions.GetAllUsers performs.
        var asEnumerable = newSignatureValue as IEnumerable<User>;

        Assert.NotNull(asEnumerable);
        Assert.Single(asEnumerable!);
    }
}
