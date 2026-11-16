using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Jellyfin.Plugin.Msfp.Protocol;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

/// <summary>Certificates the tests make.</summary>
internal static class TestCertificates
{
    /// <summary>A self-signed certificate for 127.0.0.1, with its private key.</summary>
    public static X509Certificate2 SelfSigned(string name = "peer.example.org")
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = Request(name, key);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    /// <summary>An authority, and a leaf it issued for 127.0.0.1.</summary>
    public static (X509Certificate2 Ca, X509Certificate2 Leaf) Issued()
    {
        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var caReq = new CertificateRequest("CN=Test authority", caKey, HashAlgorithmName.SHA256);
        caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        var ca = caReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        using var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var serial = new byte[8];
        RandomNumberGenerator.Fill(serial);
        using var leaf = Request("peer.example.org", leafKey).Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(29), serial);
        using var withKey = leaf.CopyWithPrivateKey(leafKey);
        return (ca, X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null));
    }

    private static CertificateRequest Request(string name, ECDsa key)
    {
        var req = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(name);
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        return req;
    }
}

public class PinsTests
{
    // Made with openssl; the pins computed by openssl too:
    // openssl x509 -pubkey -noout | openssl pkey -pubin -outform der
    //   | openssl dgst -sha256 -binary | base64 (then made base64url).
    private const string EcPem = @"-----BEGIN CERTIFICATE-----
MIIBhjCCASugAwIBAgIUQhdovDT3a2X9S20/vaiHhV8uCqUwCgYIKoZIzj0EAwIw
GDEWMBQGA1UEAwwNYS5leGFtcGxlLm9yZzAeFw0yNjExMTYxOTIxNTNaFw0zNjEx
MTMxOTIxNTNaMBgxFjAUBgNVBAMMDWEuZXhhbXBsZS5vcmcwWTATBgcqhkjOPQIB
BggqhkjOPQMBBwNCAARwJ6e2K/vUbJsWW/R3uMlJ+8ayMt9XurWr4V0R91a5tzEt
Ko+N8r8qPts07uzu8oSv3fAIXJ35v+5R/geejB0Co1MwUTAdBgNVHQ4EFgQURFSN
lQoSbPPlETjSH2XQF8tvLlUwHwYDVR0jBBgwFoAURFSNlQoSbPPlETjSH2XQF8tv
LlUwDwYDVR0TAQH/BAUwAwEB/zAKBggqhkjOPQQDAgNJADBGAiEAqandOvcjQivH
8eMHgD7QDR843C38p2ZlvhyyQTn8bCoCIQC05ieChShr8BIjayg6Pru7kRm86Ce9
/l764B+DFhp/tw==
-----END CERTIFICATE-----";

    private const string RsaPem = @"-----BEGIN CERTIFICATE-----
MIIDETCCAfmgAwIBAgIUIk4nXupGOegtmpcLBMnuJXTbCNMwDQYJKoZIhvcNAQEL
BQAwGDEWMBQGA1UEAwwNYi5leGFtcGxlLm9yZzAeFw0yNjExMTYxOTIxNTRaFw0z
NjExMTMxOTIxNTRaMBgxFjAUBgNVBAMMDWIuZXhhbXBsZS5vcmcwggEiMA0GCSqG
SIb3DQEBAQUAA4IBDwAwggEKAoIBAQDBDrbJ0iyp8YVjwzJkpv/8UfbdbNPrslGp
PZiwSwrG+ZV5zIyfJ0wIORd//oatXiWqJ+Y7tyX/QbpWqlq5306khAkGjVox34P4
LkBy4UJREJzBuLPn7mDZomB94ZHL7pQuZINvvBqAdqOuypz6OMj1L0rSQgKNL6Ai
ziuARSy6ql4l6rCRuddSt/23LM9SwZ/J7/UyhwJsrwaFaHHZuAbL/MkXHXmekUKm
CVgC2aC6YLgvL9S8tc11ymJ96OUtimfBrGIFkyhlZUkf5xlxHIsiA0qmH/Vwqeir
EEVfXdGfSPg/+XlCWWKxKPEvwWzGo0kne++suL5XmPplwHNRKTR3AgMBAAGjUzBR
MB0GA1UdDgQWBBSEeSsijjoBUv5NDHJzL0HH35g5yzAfBgNVHSMEGDAWgBSEeSsi
jjoBUv5NDHJzL0HH35g5yzAPBgNVHRMBAf8EBTADAQH/MA0GCSqGSIb3DQEBCwUA
A4IBAQAamcMQ8s2VEbGaV3Uno+vkXOfIoi6cGSckPtdKEFDjHnJn1US7i5DsySc8
lQQzeADLvpdfq+0SGpu34N870TEE8WWmwkBtYawrwAOE6sTIT+cI9+uZ8R7qKQCn
+l72nYM0V8pYmWGumdn+iArYlqEppdw8wAEbA2EG86Qf2grD/CeDpb1Pp1/selyT
+vnTWvtCtSojSNl+v0HKraUv4eMBhAfTcSXDgDqzfaXwRByRKdtS27x2LkaIyLqQ
l/eM5gMLQnjUdIlEkrYWBsNfSbBY4WsWWISJLm6CFhIS7zHYkigSiYSv4vCVwae8
1QODoPuxTleZ4eINM1lTFqs1dwST
-----END CERTIFICATE-----";

    [Theory]
    [InlineData(EcPem, "xpaCpOBmPZUMKmoMKEC4v1iUZI9Hfrmqil6SvQIWt_w")]
    [InlineData(RsaPem, "SPuLcNUsUApndovSxKUqjp3tjIbgjzQQSNSEab6GPgY")]
    public void PinIsSha256OfTheSpkiAsOpensslComputesIt(string pem, string pin)
    {
        using var cert = X509Certificate2.CreateFromPem(pem);
        Assert.Equal(pin, Pins.Of(cert));
        Assert.True(Pins.IsValid(pin));
    }

    [Fact]
    public void PinIsTheSpkiDerAsInTheCertificate()
    {
        using var cert = TestCertificates.SelfSigned();
        var expected = Encodings.Base64Url(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo()));
        Assert.Equal(expected, Pins.Of(cert));
    }

    [Fact]
    public void TrustedCertificatesPassWhateverThePins()
    {
        using var cert = TestCertificates.SelfSigned();
        Assert.True(Pins.TrustedOrNamed(cert, SslPolicyErrors.None, Array.Empty<string>()));
        Assert.True(Pins.TrustedOrNamed(cert, SslPolicyErrors.None, new[] { new string('A', 43) }));
    }

    [Theory]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors)]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch)]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch)]
    public void AnUntrustedLeafPassesOnlyWhenItsPinIsNamed(SslPolicyErrors errors)
    {
        using var cert = TestCertificates.SelfSigned();
        using var other = TestCertificates.SelfSigned();
        Assert.False(Pins.TrustedOrNamed(cert, errors, Array.Empty<string>()));
        Assert.False(Pins.TrustedOrNamed(cert, errors, new[] { Pins.Of(other) }));
        Assert.True(Pins.TrustedOrNamed(cert, errors, new[] { Pins.Of(other), Pins.Of(cert) }));
    }

    [Fact]
    public void NoCertificateNeverPasses()
    {
        Assert.False(Pins.TrustedOrNamed(null, SslPolicyErrors.RemoteCertificateNotAvailable, new[] { new string('A', 43) }));
    }

    [Fact]
    public void AnAuthoritysPinDoesntMakeItsLeafPass()
    {
        var (ca, leaf) = TestCertificates.Issued();
        using (ca)
        using (leaf)
        {
            Assert.False(Pins.TrustedOrNamed(leaf, SslPolicyErrors.RemoteCertificateChainErrors, new[] { Pins.Of(ca) }));
            Assert.True(Pins.TrustedOrNamed(leaf, SslPolicyErrors.RemoteCertificateChainErrors, new[] { Pins.Of(leaf) }));
        }
    }

    private static List<string> Read(string json)
    {
        Assert.True(Pins.TryRead(JsonDocument.Parse(json).RootElement, out var pins));
        return pins;
    }

    [Fact]
    public void ReadingHelloPins()
    {
        var a = new string('A', 43);
        var b = new string('B', 42) + "A"; // ends in a zero-bits character
        Assert.Empty(Read("{}"));
        Assert.Empty(Read("{\"certificates\":null}"));
        Assert.Empty(Read("{\"certificates\":[]}"));
        Assert.Equal(new[] { a }, Read($"{{\"certificates\":[\"{a}\"]}}"));

        // Entries that aren't pins are ignored; each pin once.
        Assert.Equal(new[] { a, b }, Read($"{{\"certificates\":[1,null,\"x\",\"{a}=\",\"{a}\",\"{new string('A', 44)}\",\"{a}\",\"{b}\"]}}"));

        // At most 8.
        var many = Enumerable.Range(0, 12).Select(i => Encodings.Base64Url(SHA256.HashData(new[] { (byte)i }))).ToList();
        Assert.Equal(many.Take(8), Read("{\"certificates\":" + JsonSerializer.Serialize(many) + "}"));
    }

    [Theory]
    [InlineData("{\"certificates\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"}")]
    [InlineData("{\"certificates\":{}}")]
    [InlineData("{\"certificates\":1}")]
    public void ACertificatesFieldThatIsntAListIsInvalid(string json)
    {
        Assert.False(Pins.TryRead(JsonDocument.Parse(json).RootElement, out _));
    }

    [Theory]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB", false)] // unused bits set
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", true)]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", false)]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+", false)]
    [InlineData(null, false)]
    public void PinValidity(string? pin, bool valid)
    {
        Assert.Equal(valid, Pins.IsValid(pin));
    }
}
