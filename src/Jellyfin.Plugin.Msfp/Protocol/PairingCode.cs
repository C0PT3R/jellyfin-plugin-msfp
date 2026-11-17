using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Msfp.Protocol;

/// <summary>
/// A pairing code (MSFP section 5): the maker's public key, a one-time
/// secret, an expiry and the maker's address.
/// </summary>
public sealed record PairingCode(byte[] PublicKey, byte[] Secret, ulong Expires, string Address)
{
    /// <summary>The text every code starts with.</summary>
    public const string Prefix = "overfin-pair-1.";

    /// <summary>
    /// The longest remainder after the prefix a reader accepts: 779
    /// characters, the base64url of 584 bytes (72 and a 512-byte address).
    /// </summary>
    public const int MaxRemainder = 779;

    /// <summary>How long a code lives.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    /// <summary>The bytes before the address: key, secret, expiry.</summary>
    private const int Fixed = 32 + 32 + 8;

    /// <summary>
    /// A new code for this key and address, with a random secret, expiring
    /// 24 hours after now, cut to the whole second.
    /// </summary>
    public static PairingCode Create(byte[] publicKey, string address, DateTimeOffset now)
    {
        if (!Protocol.Address.IsValid(address))
        {
            throw new ArgumentException("not a valid MSFP address", nameof(address));
        }

        var expires = (ulong)(now + Lifetime).ToUnixTimeSeconds();
        return new PairingCode(publicKey, RandomNumberGenerator.GetBytes(32), expires, address);
    }

    /// <summary>The code as one line of text.</summary>
    public string Format()
    {
        var address = Encoding.ASCII.GetBytes(Address);
        var bytes = new byte[Fixed + address.Length];
        PublicKey.CopyTo(bytes, 0);
        Secret.CopyTo(bytes, 32);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(64, 8), Expires);
        address.CopyTo(bytes, Fixed);
        return Prefix + Encodings.Base64Url(bytes);
    }

    /// <summary>
    /// Reads a code as section 5.1 says: white space anywhere removed, the
    /// exact prefix, a remainder of at most 779 characters, more than 72
    /// bytes decoded, a valid key (section 2.1), an expiry at most
    /// 2^53 − 1, and a valid address. Whether it expired isn't checked here.
    /// </summary>
    public static bool TryParse(string? text, out PairingCode code)
    {
        code = null!;
        if (text == null)
        {
            return false;
        }

        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!char.IsWhiteSpace(c))
            {
                sb.Append(c);
            }
        }

        var clean = sb.ToString();
        if (!clean.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = clean[Prefix.Length..];
        if (rest.Length > MaxRemainder || !Encodings.TryDecodeBase64Url(rest, out var bytes) || bytes.Length <= Fixed)
        {
            return false;
        }

        if (!Keys.IsValidKey(bytes.AsSpan(0, 32))
            || BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(64, 8)) > (ulong)StrictJson.MaxInteger)
        {
            return false;
        }

        var addressBytes = bytes.AsSpan(Fixed);
        foreach (var b in addressBytes)
        {
            if (b >= 0x80)
            {
                return false;
            }
        }

        var address = Encoding.ASCII.GetString(addressBytes);
        if (!Protocol.Address.IsValid(address))
        {
            return false;
        }

        code = new PairingCode(
            bytes[..32],
            bytes[32..64],
            BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(64, 8)),
            address);
        return true;
    }

    /// <summary>Whether the code has expired at a given time.</summary>
    public bool IsExpired(DateTimeOffset now)
    {
        return Expires > long.MaxValue || now.ToUnixTimeSeconds() >= (long)Expires;
    }
}

/// <summary>The secret hash and the pairing proof of the pair call (MSFP section 9.1).</summary>
public static class PairingProof
{
    private static readonly byte[] Label = Encoding.ASCII.GetBytes("overfin-federation-1 pairing\n");

    /// <summary>SHA-256 of the secret: how the maker finds its code.</summary>
    public static byte[] SecretHash(ReadOnlySpan<byte> secret)
    {
        return SHA256.HashData(secret);
    }

    /// <summary>
    /// HMAC-SHA256, keyed with the secret, over the label, the caller's raw
    /// public key, a line feed and the address field's bytes as sent.
    /// </summary>
    public static byte[] Compute(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> callerPublicKey, ReadOnlySpan<byte> addressField)
    {
        var data = new byte[Label.Length + callerPublicKey.Length + 1 + addressField.Length];
        Label.CopyTo(data, 0);
        callerPublicKey.CopyTo(data.AsSpan(Label.Length));
        data[Label.Length + callerPublicKey.Length] = (byte)'\n';
        addressField.CopyTo(data.AsSpan(Label.Length + callerPublicKey.Length + 1));
        return HMACSHA256.HashData(secret, data);
    }

    /// <summary>Whether a proof is right, compared in constant time.</summary>
    public static bool Check(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> callerPublicKey, ReadOnlySpan<byte> addressField, ReadOnlySpan<byte> proof)
    {
        var expected = Compute(secret, callerPublicKey, addressField);
        return CryptographicOperations.FixedTimeEquals(expected, proof);
    }
}
