using System;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

// Who may call each controller: the operator's pages (Admin,
// Identification, Paired) only Jellyfin's administrators
// ("RequiresElevation"), none of their actions opened up; the paired
// servers' calls (Federation) and Jellyfin's own reads (Play, each by its
// secret token, the relay and subtitles from this machine only) open. A
// controller added later must say which it is here.
public class ControllerAccessTests
{
    private static readonly Type[] AdminOnly =
    [
        typeof(Api.AdminController), typeof(Api.IdentificationController), typeof(Api.PairedController),
    ];

    private static readonly Type[] Open = [typeof(Api.FederationController), typeof(Api.PlayController)];

    [Fact]
    public void EveryControllerIsKnown()
    {
        var controllers = typeof(Plugin).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        Assert.Equal(AdminOnly.Concat(Open).Select(t => t.Name).Order(StringComparer.Ordinal), controllers.Select(t => t.Name));
    }

    [Fact]
    public void OperatorPagesAreAdministratorsOnly()
    {
        foreach (var c in AdminOnly)
        {
            var policy = c.GetCustomAttributes<AuthorizeAttribute>().Select(a => a.Policy).ToList();
            Assert.True(policy.Contains("RequiresElevation"), c.Name + " doesn't require an administrator");
            Assert.Empty(c.GetCustomAttributes<AllowAnonymousAttribute>());
            foreach (var m in Actions(c))
            {
                Assert.True(m.GetCustomAttribute<AllowAnonymousAttribute>() is null, c.Name + "." + m.Name + " is opened up");
            }
        }
    }

    [Fact]
    public void FederationAndPlayAreOpenByDesign()
    {
        foreach (var c in Open)
        {
            Assert.NotNull(c.GetCustomAttribute<AllowAnonymousAttribute>());
            Assert.NotEmpty(Actions(c));
        }
    }

    private static MethodInfo[] Actions(Type c) =>
        c.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any() || m.GetCustomAttributes<RouteAttribute>().Any())
            .ToArray();
}
