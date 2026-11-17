using System;
using System.Security.Cryptography;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace Msfp;

/// <summary>
/// A server's Ed25519 key pair (MSFP section 4.1): pure Ed25519 of
/// RFC 8032, made from a 32-byte seed. .NET has no Ed25519 of its own;
/// BouncyCastle's managed one does the work.
/// </summary>
public sealed class ServerKey
{
    /// <summary>The seed's length, and the public key's.</summary>
    public const int Size = 32;

    private readonly byte[] _seed;

    private ServerKey(byte[] seed)
    {
        _seed = seed;
        PublicKey = new byte[Size];
        Ed25519.GeneratePublicKey(_seed, 0, PublicKey, 0);
    }

    /// <summary>The 32 raw bytes of the public key.</summary>
    public byte[] PublicKey { get; }

    /// <summary>The public key as it travels: base64url, 43 characters.</summary>
    public string PublicKeyText => Encodings.Base64Url(PublicKey);

    /// <summary>The key's fingerprint, for operators (section 4.2).</summary>
    public string Fingerprint => Keys.Fingerprint(PublicKey);

    /// <summary>A new key from a cryptographic random seed.</summary>
    public static ServerKey Generate()
    {
        return new ServerKey(RandomNumberGenerator.GetBytes(Size));
    }

    /// <summary>The key a 32-byte seed makes.</summary>
    public static ServerKey FromSeed(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != Size)
        {
            throw new ArgumentException("an Ed25519 seed is 32 bytes", nameof(seed));
        }

        return new ServerKey(seed.ToArray());
    }

    /// <summary>A copy of the seed, for storing the key.</summary>
    public byte[] ExportSeed()
    {
        return (byte[])_seed.Clone();
    }

    /// <summary>The 64-byte Ed25519 signature of a message.</summary>
    public byte[] Sign(ReadOnlySpan<byte> message)
    {
        var sig = new byte[Ed25519.SignatureSize];
        var m = message.ToArray();
        Ed25519.Sign(_seed, 0, m, 0, m.Length, sig, 0);
        return sig;
    }
}

/// <summary>Public-key operations: checking signatures and fingerprints.</summary>
public static class Keys
{
    /// <summary>
    /// Whether 32 bytes are a valid public key (section 2.1): the canonical
    /// encoding of a point on the curve, in the prime-order subgroup and not
    /// of small order. The identity, the 8 points of small order and every
    /// point with a small-order part are refused.
    /// </summary>
    public static bool IsValidKey(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != ServerKey.Size)
        {
            return false;
        }

        try
        {
            // BouncyCastle's full check: y below p, a point on the curve
            // whose encoding is canonical, not one of small order, and
            // [L]A the identity.
            return Ed25519.ValidatePublicKeyFull(publicKey.ToArray(), 0);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> CheckedKeys = new(StringComparer.Ordinal);

    private static bool IsValidKeyCached(ReadOnlySpan<byte> publicKey)
    {
        var text = Convert.ToBase64String(publicKey);
        if (CheckedKeys.TryGetValue(text, out var valid))
        {
            return valid;
        }

        valid = IsValidKey(publicKey);
        if (CheckedKeys.Count >= 10_000)
        {
            CheckedKeys.Clear();
        }

        CheckedKeys[text] = valid;
        return valid;
    }

    /// <summary>
    /// Whether a 64-byte signature of a message verifies under a 32-byte
    /// public key, by section 2.1's rules: the key valid, R a valid point
    /// by the same rules, S below L, then RFC 8032's equation. Anything of
    /// the wrong size doesn't.
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != ServerKey.Size || signature.Length != Ed25519.SignatureSize)
        {
            return false;
        }

        // A key is checked once and the result kept (section 2.1); R on
        // every signature.
        if (!IsValidKeyCached(publicKey) || !IsValidKey(signature[..32]))
        {
            return false;
        }

        try
        {
            // BouncyCastle refuses an S at or above L itself.
            var m = message.ToArray();
            return Ed25519.Verify(signature.ToArray(), 0, publicKey.ToArray(), 0, m, 0, m.Length);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// The fingerprint of section 4.2: SHA-256 of the key, its first 15
    /// bytes in base32, six groups of four joined by '-'.
    /// </summary>
    public static string Fingerprint(ReadOnlySpan<byte> publicKey)
    {
        var hash = SHA256.HashData(publicKey);
        var b32 = Encodings.Base32(hash.AsSpan(0, 15));
        return string.Join('-', b32[0..4], b32[4..8], b32[8..12], b32[12..16], b32[16..20], b32[20..24]);
    }
}
