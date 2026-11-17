using System;
using System.Security.Cryptography;
using System.Text;

namespace Msfp;

/// <summary>
/// The encodings of MSFP section 2: base64url without padding, lowercase
/// hex, and base32 for fingerprints.
/// </summary>
public static class Encodings
{
    private const string Base64UrlAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>base64url of RFC 4648 section 5, without padding.</summary>
    public static string Base64Url(ReadOnlySpan<byte> bytes)
    {
        return System.Buffers.Text.Base64Url.EncodeToString(bytes);
    }

    /// <summary>
    /// Decodes base64url strictly: the alphabet only, no padding, no white
    /// space, and the unused bits of the last character zero. So each value
    /// has exactly one text (see SPEC-GAPS.md, section 2).
    /// </summary>
    public static bool TryDecodeBase64Url(string? text, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (text == null || text.Length % 4 == 1)
        {
            return false;
        }

        var output = new byte[text.Length * 3 / 4];
        int buffer = 0, bits = 0, n = 0;
        foreach (var c in text)
        {
            var v = Base64UrlAlphabet.IndexOf(c, StringComparison.Ordinal);
            if (v < 0)
            {
                return false;
            }

            buffer = (buffer << 6) | v;
            bits += 6;
            if (bits >= 8)
            {
                bits -= 8;
                output[n++] = (byte)(buffer >> bits);
                buffer &= (1 << bits) - 1;
            }
        }

        // What remains are the unused bits: canonical text has them zero.
        if (buffer != 0)
        {
            return false;
        }

        bytes = output;
        return true;
    }

    /// <summary>Lowercase hex.</summary>
    public static string Hex(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>Lowercase hex of SHA-256: the body hash of section 6.</summary>
    public static string Sha256Hex(ReadOnlySpan<byte> bytes)
    {
        return Hex(SHA256.HashData(bytes));
    }

    /// <summary>Standard base32 (RFC 4648 section 6), without padding.</summary>
    public static string Base32(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder((bytes.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                sb.Append(Base32Alphabet[(buffer >> bits) & 31]);
            }

            buffer &= (1 << bits) - 1;
        }

        if (bits > 0)
        {
            sb.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return sb.ToString();
    }
}
