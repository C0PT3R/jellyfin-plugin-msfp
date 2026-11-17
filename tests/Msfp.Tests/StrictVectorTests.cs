using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Msfp.Tests;

/// <summary>
/// The cases of vectors/v0-strict.json (section 2.1, corrections of
/// 2026-11-16), from this project's byte-for-byte copy: every "valid" is
/// what the plugin must decide.
/// </summary>
public class StrictVectorTests
{
    private static readonly Lazy<JsonDocument> Doc = new(() => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "vectors", "v0-strict.json"))));

    public static IEnumerable<object[]> Cases(string group) =>
        Doc.Value.RootElement.GetProperty(group).EnumerateArray().Select((c, i) => new object[] { i, Label(c) });

    private static JsonElement Case(string group, int i) => Doc.Value.RootElement.GetProperty(group)[i];

    private static string Label(JsonElement c) => c.TryGetProperty("name", out var n) ? n.GetString()! : c.GetProperty("text").GetString()!;

    private static bool Valid(JsonElement c) => c.GetProperty("valid").GetBoolean();

    private static byte[] Bytes(JsonElement c) =>
        c.TryGetProperty("hex", out var h) ? Convert.FromHexString(h.GetString()!) : Encoding.UTF8.GetBytes(c.GetProperty("text").GetString()!);

    [Theory]
    [MemberData(nameof(Cases), "keys")]
    public void Keys_(int i, string label)
    {
        var c = Case("keys", i);
        var decoded = Encodings.TryDecodeBase64Url(c.GetProperty("text").GetString(), out var key) && key.Length == 32;
        Assert.True(Valid(c) == (decoded && Keys.IsValidKey(key)), label);
    }

    [Theory]
    [MemberData(nameof(Cases), "signatures")]
    public void Signatures(int i, string label)
    {
        var c = Case("signatures", i);
        Assert.True(Encodings.TryDecodeBase64Url(c.GetProperty("public").GetString(), out var key));
        var sigOk = Encodings.TryDecodeBase64Url(c.GetProperty("signature").GetString(), out var sig);
        var message = Encoding.ASCII.GetBytes(c.GetProperty("message").GetString()!);
        Assert.True(Valid(c) == (sigOk && Keys.Verify(key, message, sig)), label);
    }

    [Theory]
    [MemberData(nameof(Cases), "base64url")]
    public void Base64Url(int i, string label)
    {
        var c = Case("base64url", i);
        var text = c.GetProperty("text").GetString()!;
        var ok = Encodings.TryDecodeBase64Url(text, out var bytes);

        // Where the length is fixed (a key, a nonce), the case says so in
        // its name; a 15-byte "nonce" is valid base64url, but not a nonce.
        if (label.Contains("nonce", StringComparison.Ordinal))
        {
            ok = ok && bytes.Length == 16;
        }
        else if (label.Contains("key", StringComparison.Ordinal))
        {
            ok = ok && bytes.Length == 32;
        }

        Assert.True(Valid(c) == ok, label);
    }

    [Theory]
    [MemberData(nameof(Cases), "dates")]
    public void Dates(int i, string label)
    {
        var c = Case("dates", i);
        Assert.True(Valid(c) == RequestCheck.TryParseDate(c.GetProperty("text").GetString(), out _), label);
    }

    [Theory]
    [MemberData(nameof(Cases), "integers")]
    public void Integers(int i, string label)
    {
        var c = Case("integers", i);
        var body = Encoding.ASCII.GetBytes("{\"n\":" + c.GetProperty("text").GetString() + "}");
        var ok = StrictJson.TryParse(body, out var doc) && StrictJson.TryGetInteger(doc!.RootElement.GetProperty("n"), out _);
        Assert.True(Valid(c) == ok, label);
    }

    [Theory]
    [MemberData(nameof(Cases), "json")]
    public void Json(int i, string label)
    {
        var c = Case("json", i);
        Assert.True(Valid(c) == StrictJson.TryParse(Bytes(c), out _), label);
    }

    [Theory]
    [MemberData(nameof(Cases), "addresses")]
    public void Addresses(int i, string label)
    {
        var c = Case("addresses", i);
        Assert.True(Valid(c) == Address.IsValid(c.GetProperty("text").GetString()), label);
    }

    [Theory]
    [MemberData(nameof(Cases), "codes")]
    public void Codes(int i, string label)
    {
        var c = Case("codes", i);
        Assert.True(Valid(c) == PairingCode.TryParse(c.GetProperty("text").GetString(), out _), label);
    }

    [Fact]
    public void TheCopyIsTheSpecificationsFile()
    {
        var spec = Path.Combine(Vectors.ProjectDir(), "..", "..", "..", "msfp-spec", "vectors", "v0-strict.json");
        if (!File.Exists(spec))
        {
            return;
        }

        Assert.Equal(File.ReadAllBytes(spec), File.ReadAllBytes(Path.Combine(Vectors.ProjectDir(), "vectors", "v0-strict.json")));
    }
}
