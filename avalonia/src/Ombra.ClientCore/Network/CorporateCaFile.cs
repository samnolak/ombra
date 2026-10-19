using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace Ombra.ClientCore.Network;

/// <summary>
/// Why a certificate file was accepted or refused: a fixed code (for logs) and the sentence the page shows, as an English
/// format and its arguments so the app can show it in its language (<see cref="Message"/> is the English sentence).
/// </summary>
public sealed record CaFileCheck(string Code, string Format, object?[] Args, int Count = 0, DateTimeOffset? ValidUntil = null, string? Pem = null)
{
    public bool Ok => Code == "ok";
    public string Message => string.Format(CultureInfo.InvariantCulture, Format, Args);
}

/// <summary>
/// The file mode of <see cref="CorporateTrust"/>: a PEM (one or more certificates) or DER file the user picked, checked
/// locally before the app keeps its own copy for <c>NODE_EXTRA_CA_CERTS</c>. Every certificate must be a certificate
/// authority (basicConstraints CA:true, keyUsage keyCertSign) that is valid now; a file holding a private key, a server
/// certificate, anything that is not a certificate, or nothing at all is refused. Only the count and the earliest
/// expiry are reported; names never leave the page (and never reach a log).
/// </summary>
public static class CorporateCaFile
{
    public const long MaxBytes = 1 << 20;
    public const int MaxCertificates = 200;

    private static readonly Regex Block = new("-----BEGIN ([A-Z0-9 ]+)-----(.*?)-----END \\1-----", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    public static CaFileCheck Validate(string path, DateTimeOffset now)
    {
        byte[] bytes;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return Refuse("unreadable", "The file was not found.");
            if (info.Length > MaxBytes) return Refuse("too-large", "The file is larger than a certificate file can be (1 MB).");
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Refuse("unreadable", "The file could not be read.");
        }
        return Validate(bytes, now);
    }

    public static CaFileCheck Validate(byte[] bytes, DateTimeOffset now)
    {
        if (bytes.Length == 0) return Refuse("no-certificate", "The file is empty.");
        var text = Encoding.ASCII.GetString(bytes);
        // Before anything else: a private key never goes into the app's copy, whatever else the file holds
        if (text.Contains("PRIVATE KEY", StringComparison.Ordinal))
            return Refuse("private-key", "The file contains a private key. Choose a file with only the certificate authority's certificate (ask IT for the CA certificate, not a key).");

        var certificates = new List<X509Certificate2>();
        try
        {
            if (text.Contains("-----BEGIN ", StringComparison.Ordinal))
            {
                foreach (Match m in Block.Matches(text))
                {
                    if (m.Groups[1].Value != "CERTIFICATE")
                        return Refuse("unsupported", "The file contains a \"{0}\" block. Only PEM certificates (BEGIN CERTIFICATE) are supported.", m.Groups[1].Value);
                    byte[] der;
                    try { der = Convert.FromBase64String(m.Groups[2].Value); }
                    catch (FormatException) { return Refuse("garbage", "A certificate in the file is damaged (not valid base64)."); }
                    certificates.Add(X509CertificateLoader.LoadCertificate(der));
                    if (certificates.Count > MaxCertificates) return Refuse("too-large", "The file has more than {0} certificates.", MaxCertificates);
                }
            }
            else certificates.Add(X509CertificateLoader.LoadCertificate(bytes)); // one DER certificate (.cer, .crt)
        }
        catch (CryptographicException)
        {
            Dispose(certificates);
            return Refuse("garbage", "The file is not a certificate file (PEM or DER).");
        }

        try
        {
            if (certificates.Count == 0) return Refuse("no-certificate", "The file contains no certificate.");
            var distinct = certificates.DistinctBy(c => c.Thumbprint).ToList();
            for (var i = 0; i < distinct.Count; i++)
            {
                var c = distinct[i];
                var single = distinct.Count == 1;
                var basic = c.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
                if (basic is not { CertificateAuthority: true })
                    return single
                        ? Refuse("not-ca", "The certificate is not a certificate authority (it looks like a server's or a person's certificate). Choose your organisation's root or intermediate CA certificate.")
                        : Refuse("not-ca", "Certificate {0} of {1} is not a certificate authority (it looks like a server's or a person's certificate). Choose your organisation's root or intermediate CA certificate.", i + 1, distinct.Count);
                var usage = c.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
                if (usage is null || !usage.KeyUsages.HasFlag(X509KeyUsageFlags.KeyCertSign))
                    return single
                        ? Refuse("not-ca", "The certificate is not allowed to sign certificates (no keyCertSign key usage), so it cannot be a certificate authority.")
                        : Refuse("not-ca", "Certificate {0} of {1} is not allowed to sign certificates (no keyCertSign key usage), so it cannot be a certificate authority.", i + 1, distinct.Count);
                var notAfter = new DateTimeOffset(c.NotAfter.ToUniversalTime(), TimeSpan.Zero);
                var notBefore = new DateTimeOffset(c.NotBefore.ToUniversalTime(), TimeSpan.Zero);
                if (notAfter <= now)
                    return single
                        ? Refuse("expired", "The certificate expired on {0:d MMMM yyyy}.", notAfter)
                        : Refuse("expired", "Certificate {0} of {1} expired on {2:d MMMM yyyy}.", i + 1, distinct.Count, notAfter);
                if (notBefore > now)
                    return single
                        ? Refuse("not-yet-valid", "The certificate is valid only from {0:d MMMM yyyy}.", notBefore)
                        : Refuse("not-yet-valid", "Certificate {0} of {1} is valid only from {2:d MMMM yyyy}.", i + 1, distinct.Count, notBefore);
            }
            var pem = new StringBuilder();
            foreach (var c in distinct) pem.Append(c.ExportCertificatePem()).Append('\n');
            var until = distinct.Min(c => new DateTimeOffset(c.NotAfter.ToUniversalTime(), TimeSpan.Zero));
            var format = distinct.Count == 1 ? "{0} certificate authority, valid until {1:d MMMM yyyy}." : "{0} certificate authorities, valid until {1:d MMMM yyyy}.";
            return new("ok", format, [distinct.Count, until], distinct.Count, until, pem.ToString());
        }
        finally
        {
            Dispose(certificates);
        }
    }

    /// <summary>
    /// Writes the checked certificates as the app's bundle: its folder readable only by the user (0700), the file 0600,
    /// replaced atomically. Only <paramref name="bundlePath"/> is written.
    /// </summary>
    public static void Install(string pem, string bundlePath)
    {
        var folder = Path.GetDirectoryName(bundlePath)!;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(folder);
        else
        {
            Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var temp = bundlePath + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temp, options))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            writer.Write(pem);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, bundlePath, overwrite: true);
    }

    /// <summary>Deletes the app's bundle (only that file) and its folder when that is then empty.</summary>
    public static void Remove(string bundlePath)
    {
        if (File.Exists(bundlePath)) File.Delete(bundlePath);
        var folder = Path.GetDirectoryName(bundlePath)!;
        if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
    }

    private static CaFileCheck Refuse(string code, string format, params object?[] args) => new(code, format, args);

    private static void Dispose(List<X509Certificate2> certificates)
    {
        foreach (var c in certificates) c.Dispose();
    }
}
