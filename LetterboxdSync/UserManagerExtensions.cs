using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;

namespace LetterboxdSync;

/// <summary>
/// Shim around <see cref="IUserManager.Users"/> that survives signature changes
/// across Jellyfin patch releases.
///
/// In 10.11.9 Jellyfin changed the property's return type from
/// <c>IEnumerable&lt;User&gt;</c> to <c>IReadOnlyList&lt;User&gt;</c>. A plugin
/// compiled against the 10.11.0 SDK embeds a metadata reference to the old
/// getter, and the runtime throws <c>System.MissingMethodException</c> on
/// 10.11.9+ because that exact signature no longer exists. Bumping the SDK
/// just flips the breakage to the inverse direction (older servers stop
/// working). Reflecting the property at call time and casting the result to
/// the common base interface lets a single build target every 10.11.x patch.
///
/// Reported in issue #46. Replaces every direct <c>_userManager.Users</c> read
/// in the plugin.
/// </summary>
internal static class UserManagerExtensions
{
    public static IEnumerable<User> GetAllUsers(this IUserManager? userManager)
    {
        if (userManager is null) return Array.Empty<User>();

        try
        {
            var prop = userManager.GetType().GetProperty(
                "Users",
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            if (prop is null) return Array.Empty<User>();

            // Both old (IEnumerable<User>) and new (IReadOnlyList<User>) types
            // are assignable to IEnumerable<User>, so a single cast handles both.
            return prop.GetValue(userManager) as IEnumerable<User> ?? Array.Empty<User>();
        }
        catch
        {
            // Fail closed: callers degrade to "no users" rather than 500.
            return Array.Empty<User>();
        }
    }
}
