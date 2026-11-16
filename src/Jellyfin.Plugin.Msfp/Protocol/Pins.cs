using System;
using System.Collections.Generic;
using System.Formats.Asn1;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Jellyfin.Plugin.Msfp.Protocol;

/// <summary>
/// Certificate pins (MSFP section 3.1): SHA-256 of a certificate's
/// SubjectPublicKeyInfo, base64url, and which certificates a connection
/// accepts.
/// </summary>
public static class Pins
{
    /// <summary>The most pins kept for a peer.</summary>
    public const int MaxPins = 8;

    /// <summary>
    /// The pin of a certificate: SHA-256 of its SubjectPublicKeyInfo, the
    /// DER bytes exactly as they are in the certificate (not re-encoded).
    /// </summary>
    public static string Of(X509Certificate2 certificate)
    {
        return Of(certificate.RawData);
    }

    /// <summary>The pin of a DER certificate.</summary>
    public static string Of(byte[] certificateDer)
    {
        // Certificate ::= SEQUENCE { tbsCertificate, signatureAlgorithm, signature }
        // TBSCertificate ::= SEQUENCE { [0] version OPTIONAL, serialNumber,
        //   signature, issuer, validity, subject, subjectPublicKeyInfo, ... }
        var cert = new AsnReader(certificateDer, AsnEncodingRules.DER).ReadSequence();
        var tbs = cert.ReadSequence();
        if (tbs.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
        {
            tbs.ReadEncodedValue();
        }

        tbs.ReadEncodedValue(); // serialNumber
        tbs.ReadEncodedValue(); // signature
        tbs.ReadEncodedValue(); // issuer
        tbs.ReadEncodedValue(); // validity
        tbs.ReadEncodedValue(); // subject
        var spki = tbs.ReadEncodedValue();
        return Encodings.Base64Url(SHA256.HashData(spki.Span));
    }

    /// <summary>Whether a text is a pin: 43 characters of base64url, 32 bytes.</summary>
    public static bool IsValid(string? pin)
    {
        return pin != null && pin.Length == 43 && Encodings.TryDecodeBase64Url(pin, out var b) && b.Length == 32;
    }

    /// <summary>
    /// The pins a hello names: the valid entries, each once, at most 8, in
    /// order. Absent, null or an empty list: none. False when the field is
    /// there but not a list.
    /// </summary>
    public static bool TryRead(JsonElement json, out List<string> pins)
    {
        pins = new List<string>();
        if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty("certificates", out var field) || field.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (field.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var e in field.EnumerateArray())
        {
            if (pins.Count == MaxPins)
            {
                break;
            }

            var text = e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            if (IsValid(text) && !pins.Contains(text!))
            {
                pins.Add(text!);
            }
        }

        return true;
    }

    /// <summary>
    /// The check of a connection that needs a *trusted* or *named*
    /// certificate: valid against the usual roots for the host, or a leaf
    /// whose pin the peer named. A pin of an authority in the chain never
    /// counts; pins never refuse a trusted certificate.
    /// </summary>
    public static bool TrustedOrNamed(X509Certificate? leaf, SslPolicyErrors errors, IReadOnlyCollection<string> named)
    {
        if (leaf == null)
        {
            return false;
        }

        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        if (named.Count == 0)
        {
            return false;
        }

        try
        {
            var pin = Of(leaf.GetRawCertData());
            foreach (var n in named)
            {
                if (n == pin)
                {
                    return true;
                }
            }
        }
        catch (AsnContentException)
        {
        }

        return false;
    }
}
