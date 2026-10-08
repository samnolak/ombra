using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace OmpGui.Tests;

/// <summary>Synthetic certificates made at test time (never real ones, never the keychain's): CAs, servers, keys.</summary>
internal static class CorporateTrustCerts
{
    public const string CaName = "CN=Synthetic Test Root CA";

    public static X509Certificate2 Ca(string name = CaName, DateTimeOffset? from = null, DateTimeOffset? to = null, bool isCa = true, bool keyCertSign = true)
    {
        var key = RSA.Create(2048); // kept: the CA signs server certificates with it
        var req = new CertificateRequest(name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(isCa, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(keyCertSign ? X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign : X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        return req.CreateSelfSigned(from ?? DateTimeOffset.UtcNow.AddDays(-1), to ?? DateTimeOffset.UtcNow.AddDays(30));
    }

    /// <summary>A server certificate for 127.0.0.1 and localhost: signed by <paramref name="ca"/>, else self-signed.</summary>
    public static (X509Certificate2 Cert, string KeyPem) Server(X509Certificate2? ca = null)
    {
        var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        X509Certificate2 cert;
        if (ca is null) cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10));
        else
        {
            req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));
            cert = req.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10), RandomNumberGenerator.GetBytes(12));
        }
        return (cert, key.ExportPkcs8PrivateKeyPem());
    }

    public static string Write(string dir, string name, string content)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        return path;
    }
}
