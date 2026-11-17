using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Msfp.Tests;

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
