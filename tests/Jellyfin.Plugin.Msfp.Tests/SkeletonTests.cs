using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

public class SkeletonTests
{
    private static readonly Assembly PluginAssembly = typeof(Plugin).Assembly;

    [Fact]
    public void ConfigurationPageIsEmbeddedUnderTheNameGetPagesUses()
    {
        // GetPages builds "<namespace>.Configuration.configPage.html".
        var name = typeof(Plugin).Namespace + ".Configuration.configPage.html";
        Assert.Contains(name, PluginAssembly.GetManifestResourceNames());
        using var stream = PluginAssembly.GetManifestResourceStream(name)!;
        var page = new StreamReader(stream).ReadToEnd();
        Assert.Contains(Plugin.PluginId, page);
    }

    [Fact]
    public void ManifestNamesThePluginsIdAndDll()
    {
        var root = FindRepoRoot();
        var manifest = File.ReadAllText(Path.Combine(root, "build.yaml"));
        Assert.Contains($"guid: \"{Plugin.PluginId}\"", manifest);
        Assert.Contains("- \"Jellyfin.Plugin.Msfp.dll\"", manifest);
        Assert.Contains("- \"Msfp.dll\"", manifest);
        Assert.Contains("targetAbi: \"12.2.0.0\"", manifest);
    }

    [Fact]
    public void PluginIdIsAGuid()
    {
        Assert.True(Guid.TryParse(Plugin.PluginId, out _));
    }

    [Fact]
    public void PluginTypeIsPublicWithJellyfinsConstructor()
    {
        // Jellyfin creates the plugin through dependency injection.
        var ctor = typeof(Plugin).GetConstructors().Single();
        Assert.Equal(2, ctor.GetParameters().Length);
        Assert.True(typeof(Plugin).IsPublic);
    }

    internal static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "build.yaml")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("build.yaml not found above the test output");
    }
}
