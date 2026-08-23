using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace BeamSharp.Tests;

/// <summary>
/// Builds a throwaway certificate chain in memory, so the TLS tests need no openssl and no files
/// on disk.
/// </summary>
internal static class TestCertificates
{
    /// <summary>Creates a self-signed authority that can issue node certificates.</summary>
    public static X509Certificate2 CreateAuthority(string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.DigitalSignature, true));

        // The authority has to outlive what it issues, and "now plus a day" computed twice a
        // millisecond apart does not: the second call lands later than the first.
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(2));
    }

    /// <summary>
    /// Issues a node certificate from <paramref name="authority"/>, good for both ends of a mutual
    /// TLS connection.
    /// </summary>
    public static X509Certificate2 Issue(X509Certificate2 authority, string commonName)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={commonName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")], false));  // server + client auth

        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(Node.NodeName.LocalShortHost);
        names.AddDnsName("localhost");
        names.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());

        using var issued = request.Create(
            authority, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1),
            Guid.NewGuid().ToByteArray()[..8]);

        // A certificate straight out of Create has no private key, and one attached in memory is
        // ephemeral; the PKCS#12 round trip gives a key handle usable for server authentication.
        using var withKey = issued.CopyWithPrivateKey(key);
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), password: null);
    }
}
