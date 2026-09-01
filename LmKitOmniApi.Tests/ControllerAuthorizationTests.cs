using System.Reflection;
using LmKitOmniApi.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LmKitOmniApi.Tests;

public class ControllerAuthorizationTests
{
    // Controllers that are intentionally reachable without authentication.
    // Every controller that is NOT in this allow-list must be protected by
    // [Authorize] — either at the class level or on every action. New controllers
    // are therefore covered automatically: they fail this test until they are
    // secured or consciously added here with a documented justification.
    private static readonly IReadOnlySet<Type> IntentionallyAnonymousControllers = new HashSet<Type>
    {
        // Login / logout / refresh must be reachable before a token exists.
        // Its authenticated-only endpoint (GetCurrentUser) still carries its own [Authorize].
        typeof(AuthController),
    };

    public static IEnumerable<object[]> AllApiControllers() =>
        DiscoverControllers().Select(controllerType => new object[] { controllerType });

    [Theory]
    [MemberData(nameof(AllApiControllers))]
    public void EveryController_RequiresAuthorization_UnlessExplicitlyAnonymous(Type controllerType)
    {
        var isIntentionallyAnonymous = IntentionallyAnonymousControllers.Contains(controllerType);

        Assert.True(
            isIntentionallyAnonymous || RequiresAuthorization(controllerType),
            $"{controllerType.Name} is not protected by [Authorize] (missing at the class level and on at least one action). " +
            $"If anonymous access is intentional, add it to {nameof(IntentionallyAnonymousControllers)} with a justification.");
    }

    [Fact]
    public void ControllerDiscovery_CoversEveryApiControllerIncludingAdminSurfaces()
    {
        var discovered = DiscoverControllers().ToArray();

        // Guards the cover-all mechanism itself: if reflection silently found no
        // controllers the [Theory] above would pass vacuously. Also pins the two
        // admin controllers that a previous hard-coded allow-list omitted, so they
        // can never drop out of the checked set again.
        Assert.NotEmpty(discovered);
        Assert.Contains(typeof(UsersController), discovered);
        Assert.Contains(typeof(McpServersController), discovered);
    }

    private static IEnumerable<Type> DiscoverControllers() =>
        typeof(AuthController).Assembly
            .GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }
                && typeof(ControllerBase).IsAssignableFrom(type));

    // Mirrors the original "class-level [Authorize] OR every action authorized" rule,
    // but fails closed: a controller with no discoverable actions is treated as
    // unprotected instead of passing on an empty All(...).
    private static bool RequiresAuthorization(Type controllerType)
    {
        if (controllerType.GetCustomAttribute<AuthorizeAttribute>() is not null)
            return true;

        var actions = controllerType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .Where(method => method.GetCustomAttribute<NonActionAttribute>() is null)
            .ToArray();

        return actions.Length > 0
            && actions.All(method => method.GetCustomAttribute<AuthorizeAttribute>() is not null);
    }
}
