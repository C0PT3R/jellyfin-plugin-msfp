using System;
using System.Globalization;

namespace Jellyfin.Plugin.Msfp.Protocol;

/// <summary>How a receiver hears a key that signed a request (MSFP section 6.5).</summary>
public enum Hearing
{
    /// <summary>Not heard: refused with an unsigned 404, nonce not stored.</summary>
    NotHeard,

    /// <summary>A known peer, in any state.</summary>
    Peer,

    /// <summary>A stranger, heard on pair while a code is live.</summary>
    Stranger,

    /// <summary>A key whose pairing this receiver ended: a signed 404.</summary>
    Forgotten,
}

/// <summary>What checking a request gave.</summary>
public enum CheckOutcome
{
    /// <summary>Accepted: handle the call.</summary>
    Accepted,

    /// <summary>Refused with an unsigned 404.</summary>
    Refused,

    /// <summary>Refused with a signed 404: a key this receiver forgot.</summary>
    RefusedSigned,
}

/// <summary>The result of checking a request, with its key and nonce once known.</summary>
public sealed record CheckResult(CheckOutcome Outcome, byte[]? Key, string? KeyText, string? Nonce, Hearing Hearing)
{
    public static readonly CheckResult Refused = new(CheckOutcome.Refused, null, null, null, Hearing.NotHeard);
}

/// <summary>
/// Checks a signed request as MSFP section 6.4 says, step by step, and
/// refuses at the first failure.
/// </summary>
public sealed class RequestCheck
{
    /// <summary>The time window: 5 minutes either way.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _clock;
    private readonly NonceMemory _nonces;

    public RequestCheck(TimeProvider clock, NonceMemory nonces)
    {
        _clock = clock;
        _nonces = nonces;
    }

    /// <summary>
    /// Checks a request. ownHost is the host of this server's own address
    /// (never the Host header); hears says how a key is heard on this call.
    /// </summary>
    public CheckResult Check(string ownHost, string method, string target, SignatureHeaders headers, ReadOnlySpan<byte> body, Func<byte[], Hearing> hears)
    {
        // 1. No query (federation calls have none, section 6.2), and the
        // key decodes to 32 bytes. (Each header appearing once is the
        // controller's: a header sent twice arrives here as null.)
        if (target.Contains('?', StringComparison.Ordinal)
            || !Encodings.TryDecodeBase64Url(headers.Key, out var key) || key.Length != ServerKey.Size)
        {
            return CheckResult.Refused;
        }

        // 2. The date is a decimal integer.
        if (!TryParseDate(headers.Date, out var date))
        {
            return CheckResult.Refused;
        }

        // 3. Within 5 minutes of the clock, which isn't rounded.
        var nowTicks = (Int128)(_clock.GetUtcNow() - DateTimeOffset.UnixEpoch).Ticks;
        var dateTicks = (Int128)date * TimeSpan.TicksPerSecond;
        var diff = nowTicks - dateTicks;
        if (diff > Window.Ticks || -diff > Window.Ticks)
        {
            return CheckResult.Refused;
        }

        // 4. Not before the nonce memory began; the date is the start of
        // its second.
        if (dateTicks < (_nonces.Began - DateTimeOffset.UnixEpoch).Ticks)
        {
            return CheckResult.Refused;
        }

        // 5. The nonce decodes to 16 bytes.
        if (!Encodings.TryDecodeBase64Url(headers.Nonce, out var nonce) || nonce.Length != Signing.NonceSize)
        {
            return CheckResult.Refused;
        }

        // 6. The signature decodes.
        if (!Encodings.TryDecodeBase64Url(headers.Signature, out var signature))
        {
            return CheckResult.Refused;
        }

        // 7. It verifies over the request message.
        byte[] message;
        try
        {
            message = Signing.RequestMessage(method, ownHost, target, headers.Date!, headers.Nonce!, body);
        }
        catch (ArgumentException)
        {
            return CheckResult.Refused;
        }

        if (!Keys.Verify(key, message, signature))
        {
            return CheckResult.Refused;
        }

        // 8. The key is heard.
        var hearing = hears(key);
        switch (hearing)
        {
            case Hearing.Forgotten:
                return new CheckResult(CheckOutcome.RefusedSigned, key, headers.Key, headers.Nonce, hearing);
            case Hearing.Peer:
            case Hearing.Stranger:
                break;
            default:
                return CheckResult.Refused;
        }

        // 9. Never seen, and there is room.
        if (!_nonces.TryRemember(headers.Key!, headers.Nonce!, hearing == Hearing.Peer ? NonceRoom.Peers : NonceRoom.Strangers))
        {
            return CheckResult.Refused;
        }

        return new CheckResult(CheckOutcome.Accepted, key, headers.Key, headers.Nonce, hearing);
    }

    /// <summary>
    /// A date as section 2.1 reads it: a positive decimal integer, ASCII
    /// digits only, no sign, no leading zero, at most 2^53 − 1.
    /// </summary>
    public static bool TryParseDate(string? text, out long date)
    {
        date = 0;
        if (string.IsNullOrEmpty(text) || text.Length > 16 || text[0] == '0')
        {
            return false;
        }

        foreach (var c in text)
        {
            if (c < '0' || c > '9')
            {
                return false;
            }
        }

        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out date) || date > StrictJson.MaxInteger)
        {
            date = 0;
            return false;
        }

        return true;
    }
}
