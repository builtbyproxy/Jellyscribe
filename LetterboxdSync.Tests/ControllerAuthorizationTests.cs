using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// Walks every action on every plugin controller and pins who may call it. Dropping an
/// <c>[Authorize]</c>, an admin-only policy or adding an anonymous action changes one of the
/// sets below, so the change has to be made here on purpose instead of slipping through green.
/// </summary>
public class ControllerAuthorizationTests
{
    private const string AdminPolicy = "RequiresElevation";

    /// <summary>Actions anyone may call without signing in. The web client loads sidebar.js
    /// from the index page before any user is known.</summary>
    private static readonly HashSet<string> ExpectedAnonymous = new(StringComparer.Ordinal)
    {
        "SidebarController.GetSidebarJs",
    };

    /// <summary>Actions only a Jellyfin administrator may call.</summary>
    private static readonly HashSet<string> ExpectedAdminOnly = new(StringComparer.Ordinal)
    {
        "LetterboxdController.GetAuthBreakers",
        "LetterboxdController.GetLogs",
        "LetterboxdController.GetTelemetryPreview",
        "LetterboxdController.PreviewLogs",
        "LetterboxdController.SendLogs",
        "LetterboxdController.TestJellyseerr",
    };

    private sealed record ActionInfo(string Name, bool Anonymous, bool Authorized, bool AdminOnly, bool Routed);

    private static List<ActionInfo> Actions()
    {
        var controllers = typeof(Plugin).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .ToList();
        Assert.NotEmpty(controllers);

        var actions = new List<ActionInfo>();
        foreach (var controller in controllers)
        {
            var classAuth = controller.GetCustomAttributes(inherit: true).OfType<IAuthorizeData>().ToList();
            var classAnon = controller.IsDefined(typeof(AllowAnonymousAttribute), inherit: true);

            // MVC treats every public instance method on a controller as an action unless it is
            // marked [NonAction], including ones inherited from a plugin base class such as
            // JellyfinUserApiController, so walk all of them, not only the routed ones. Methods
            // from ControllerBase itself and object are framework plumbing, not actions.
            var methods = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.DeclaringType != null
                            && m.DeclaringType.Assembly == typeof(Plugin).Assembly
                            && !m.IsSpecialName
                            && !m.IsDefined(typeof(NonActionAttribute)));
            foreach (var m in methods)
            {
                var methodAuth = m.GetCustomAttributes(inherit: true).OfType<IAuthorizeData>().ToList();
                var auth = classAuth.Concat(methodAuth).ToList();
                actions.Add(new ActionInfo(
                    $"{controller.Name}.{m.Name}",
                    Anonymous: classAnon || m.IsDefined(typeof(AllowAnonymousAttribute), inherit: true),
                    Authorized: auth.Count > 0,
                    AdminOnly: auth.Any(a => a.Policy == AdminPolicy || (a.Roles?.Contains("Administrator") ?? false)),
                    Routed: m.GetCustomAttributes<HttpMethodAttribute>(inherit: true).Any()));
            }
        }

        return actions;
    }

    [Fact]
    public void EveryPublicControllerMethod_IsARoutedAction()
    {
        var unrouted = Actions().Where(a => !a.Routed).Select(a => a.Name).ToList();
        Assert.Empty(unrouted);
    }

    [Fact]
    public void OnlyTheAllowlistedActions_AreAnonymous()
    {
        var anonymous = Actions().Where(a => a.Anonymous).Select(a => a.Name).ToHashSet();
        Assert.Equal(ExpectedAnonymous.OrderBy(n => n), anonymous.OrderBy(n => n));
    }

    [Fact]
    public void EveryOtherAction_RequiresASignedInUser()
    {
        var open = Actions().Where(a => !a.Anonymous && !a.Authorized).Select(a => a.Name).ToList();
        Assert.Empty(open);
    }

    [Fact]
    public void AdminOnlyActions_MatchTheExpectedSet()
    {
        var admin = Actions().Where(a => a.AdminOnly).Select(a => a.Name).ToHashSet();
        Assert.Equal(ExpectedAdminOnly.OrderBy(n => n), admin.OrderBy(n => n));
    }

    [Fact]
    public void TheWalk_SeesEveryController()
    {
        var controllers = Actions().Select(a => a.Name.Split('.')[0]).ToHashSet();
        Assert.Superset(new HashSet<string>
        {
            "LetterboxdController", "SerializdController", "LibrariesController", "SidebarController",
        }, controllers);
    }
}
