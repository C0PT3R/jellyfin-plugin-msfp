using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Msfp;

/// <summary>The four signature headers of a request (MSFP section 6.1), as text.</summary>
public sealed record SignatureHeaders(string? Key, string? Date, string? Nonce, string? Signature)
{
    public const string KeyHeader = "Overfin-Key";
    public const string DateHeader = "Overfin-Date";
    public const string NonceHeader = "Overfin-Nonce";
    public const string SignatureHeader = "Overfin-Signature";
}

/// <summary>The signed messages of MSFP section 6, and signing them.</summary>
public static class Signing
{
    /// <summary>The bytes of a nonce.</summary>
    public const int NonceSize = 16;

    private const string RequestLabel = "overfin-federation-1 request";
    private const string AnswerLabel = "overfin-federation-1 response";

    /// <summary>
    /// The request message of section 6.2: seven lines joined by '\n',
    /// without a trailing one.
    /// </summary>
    public static byte[] RequestMessage(string method, string host, string target, string date, string nonce, string bodyHash)
    {
        return Lines(RequestLabel, method, host, target, date, nonce, bodyHash);
    }

    /// <summary>The request message for a body.</summary>
    public static byte[] RequestMessage(string method, string host, string target, string date, string nonce, ReadOnlySpan<byte> body)
    {
        return RequestMessage(method, host, target, date, nonce, Encodings.Sha256Hex(body));
    }

    /// <summary>The answer message of section 6.3: four lines.</summary>
    public static byte[] AnswerMessage(string nonce, int status, ReadOnlySpan<byte> body)
    {
        return Lines(AnswerLabel, nonce, status.ToString(CultureInfo.InvariantCulture), Encodings.Sha256Hex(body));
    }

    /// <summary>
    /// Signs a request: the receiver's host, the target, the body, the time
    /// and a nonce (16 random bytes when none is given).
    /// </summary>
    public static SignatureHeaders SignRequest(ServerKey key, string method, string host, string target, ReadOnlySpan<byte> body, DateTimeOffset now, ReadOnlySpan<byte> nonce = default)
    {
        var nonceBytes = nonce.IsEmpty ? RandomNumberGenerator.GetBytes(NonceSize) : nonce.ToArray();
        if (nonceBytes.Length != NonceSize)
        {
            throw new ArgumentException("a nonce is 16 bytes", nameof(nonce));
        }

        var date = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var nonceText = Encodings.Base64Url(nonceBytes);
        var message = RequestMessage(method, host, target, date, nonceText, body);
        return new SignatureHeaders(key.PublicKeyText, date, nonceText, Encodings.Base64Url(key.Sign(message)));
    }

    /// <summary>The Overfin-Signature value of an answer.</summary>
    public static string SignAnswer(ServerKey key, string nonce, int status, ReadOnlySpan<byte> body)
    {
        return Encodings.Base64Url(key.Sign(AnswerMessage(nonce, status, body)));
    }

    /// <summary>
    /// Whether an answer's Overfin-Signature verifies under the answering
    /// server's key, for the request's nonce text, the status and the body.
    /// A missing or undecodable signature doesn't.
    /// </summary>
    public static bool CheckAnswer(ReadOnlySpan<byte> publicKey, string nonce, int status, ReadOnlySpan<byte> body, string? signature)
    {
        if (!Encodings.TryDecodeBase64Url(signature, out var sig))
        {
            return false;
        }

        return Keys.Verify(publicKey, AnswerMessage(nonce, status, body), sig);
    }

    private static byte[] Lines(params string[] lines)
    {
        foreach (var line in lines)
        {
            foreach (var c in line)
            {
                // Signed text is ASCII, and a line can't hold a line feed.
                if (c > 0x7f || c == '\n')
                {
                    throw new ArgumentException("signed lines are ASCII without line feeds");
                }
            }
        }

        return Encoding.ASCII.GetBytes(string.Join('\n', lines));
    }
}
