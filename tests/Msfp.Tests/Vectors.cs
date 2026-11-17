using System;
using System.IO;
using System.Text.Json;

namespace Msfp.Tests;

/// <summary>The test vectors, from this project's byte-for-byte copy of vectors/v0.json.</summary>
internal static class Vectors
{
    private static readonly Lazy<JsonDocument> Doc = new(() => JsonDocument.Parse(File.ReadAllBytes(CopyPath)));

    /// <summary>The copy shipped with the tests.</summary>
    public static string CopyPath => Path.Combine(AppContext.BaseDirectory, "vectors", "v0.json");

    /// <summary>The copy in the source tree (the one under version control).</summary>
    public static string SourceCopyPath => Path.Combine(ProjectDir(), "vectors", "v0.json");

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
                dir = Path.Combine(ProjectDir(), "..", "..", "..", "msfp-spec");
            }

            return Path.Combine(dir, "vectors", "v0.json");
        }
    }

    /// <summary>This test project's folder in the source tree, found above the test output.</summary>
    public static string ProjectDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Msfp.Tests.csproj")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Msfp.Tests.csproj not found above the test output");
    }

    public static JsonElement Root => Doc.Value.RootElement;

    public static JsonElement Key(string name) => Root.GetProperty("keys").GetProperty(name);

    public static string Str(this JsonElement e, string name) => e.GetProperty(name).GetString()!;

    public static byte[] Hex(this JsonElement e, string name) => Convert.FromHexString(e.Str(name));
}
