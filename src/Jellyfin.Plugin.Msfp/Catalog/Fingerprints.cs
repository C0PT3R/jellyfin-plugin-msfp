using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Jellyfin.Plugin.Msfp.Catalog;

/// <summary>
/// A file named by what's in it, and the etag a copy carries (MSFP video
/// section 3.2), computed exactly as Overfin's (its files.Fingerprint and
/// copies.ETag), so that the two give a file the same fingerprint: the
/// size, then SHA-256 over the size as a big-endian int64 and three pieces
/// of 256 KiB at the start, the middle and the end. Never the path, the
/// size alone or the time: a move keeps it, an edit changes it (a same-size
/// edit touching none of the three pieces keeps it: the accepted limit).
/// </summary>
internal static partial class Fingerprints
{
    /// <summary>How much of a file each of the three pieces reads.</summary>
    public const int Chunk = 256 << 10;

    /// <summary>The bytes of the etag key.</summary>
    public const int KeySize = 32;

    [GeneratedRegex("^[0-9a-z]{1,14}-[0-9a-f]{32}\\z")]
    private static partial Regex FormRe();

    /// <summary>Whether a string is a fingerprint's form.</summary>
    public static bool Form(string? s) => s is not null && FormRe().IsMatch(s);

    /// <summary>The fingerprint of the file open on handle, of the given size: "&lt;size in base 36&gt;-&lt;32 hex&gt;".</summary>
    public static string Of(SafeFileHandle handle, long size)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> be = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(be, size);
        h.AppendData(be);
        var buf = new byte[Chunk];
        foreach (var start in new[] { 0, (size / 2) - (Chunk / 2), size - Chunk })
        {
            var at = Math.Max(start, 0);
            var n = 0;
            while (n < Chunk)
            {
                var k = RandomAccess.Read(handle, buf.AsSpan(n), at + n);
                if (k == 0)
                {
                    break;
                }

                n += k;
            }

            h.AppendData(buf, 0, n);
        }

        return Base36(size) + "-" + Convert.ToHexStringLower(h.GetHashAndReset())[..32];
    }

    /// <summary>
    /// A copy's etag: the first 22 characters of the base64url HMAC-SHA256,
    /// under the plugin's etag key, of the fingerprint as Overfin's mac
    /// writes a part ("&lt;length&gt;:&lt;fingerprint&gt;"). It tells nothing
    /// across servers, and a rotation of the federation key changes none.
    /// </summary>
    public static string ETag(byte[] key, string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        var part = Encoding.UTF8.GetBytes(fingerprint.Length.ToString(CultureInfo.InvariantCulture) + ":" + fingerprint);
        return Convert.ToBase64String(HMACSHA256.HashData(key, part)).TrimEnd('=').Replace('+', '-').Replace('/', '_')[..22];
    }

    /// <summary>
    /// The etag key in the plugin's data folder (its own file, mode 600,
    /// never in the settings Jellyfin serves to the admin pages, never the
    /// federation key): read, or made of 32 random bytes when it is missing
    /// or isn't 32 bytes long. A lost folder gives a new key: every etag
    /// changes once.
    /// </summary>
    public static byte[] Key(string dir)
    {
        var path = Path.Combine(dir, "etag.key");
        try
        {
            var have = File.ReadAllBytes(path);
            if (have.Length == KeySize)
            {
                return have;
            }
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
        }

        Directory.CreateDirectory(dir);
        var key = RandomNumberGenerator.GetBytes(KeySize);
        var tmp = path + ".new";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var f = new FileStream(tmp, options))
        {
            f.Write(key);
            f.Flush(true);
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(tmp, path, true);
        return key;
    }

    private static string Base36(long n)
    {
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        if (n == 0)
        {
            return "0";
        }

        var sb = new StringBuilder();
        var negative = n < 0;
        var u = negative ? (ulong)(-(n + 1)) + 1 : (ulong)n;
        while (u > 0)
        {
            sb.Insert(0, digits[(int)(u % 36)]);
            u /= 36;
        }

        return negative ? "-" + sb : sb.ToString();
    }
}
