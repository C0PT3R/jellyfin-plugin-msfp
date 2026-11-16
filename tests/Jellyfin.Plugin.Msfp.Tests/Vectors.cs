using System;
using System.IO;
using System.Text.Json;

namespace Jellyfin.Plugin.Msfp.Tests;

/// <summary>The test vectors, from this project's byte-for-byte copy of vectors/v0.json.</summary>
internal static class Vectors
{
    private static readonly Lazy<JsonDocument> Doc = new(() => JsonDocument.Parse(File.ReadAllBytes(CopyPath)));

    /// <summary>The copy shipped with the tests.</summary>
    public static string CopyPath => Path.Combine(AppContext.BaseDirectory, "vectors", "v0.json");

    /// <summary>The copy in the source tree (the one under version control).</summary>
    public static string SourceCopyPath => Path.Combine(SkeletonTests.FindRepoRoot(), "tests", "Jellyfin.Plugin.Msfp.Tests", "vectors", "v0.json");

    /// <summary>
    /// The specification repository's file, when it is there: MSFP_SPEC_DIR,
    /// or msfp-spec beside this repository.
    /// </summary>
    public static string SpecPath
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("MSFP_SPEC_DIR");
            if (string.IsNullOrEmpty(dir))
            {
                dir = Path.Combine(SkeletonTests.FindRepoRoot(), "..", "msfp-spec");
            }

            return Path.Combine(dir, "vectors", "v0.json");
        }
    }

    public static JsonElement Root => Doc.Value.RootElement;

    public static JsonElement Key(string name) => Root.GetProperty("keys").GetProperty(name);

    public static string Str(this JsonElement e, string name) => e.GetProperty(name).GetString()!;

    public static byte[] Hex(this JsonElement e, string name) => Convert.FromHexString(e.Str(name));
}
