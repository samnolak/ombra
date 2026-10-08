using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace OmpGui.ClientCore.Network;

/// <summary>Why a certificate file was accepted or refused: a fixed code (for logs) and the sentence the page shows.</summary>
public sealed record CaFileCheck(string Code, string Message, int Count = 0, DateTimeOffset? ValidUntil = null, string? Pem = null)
{
    public bool Ok => Code == "ok";
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
            if (!info.Exists) return new("unreadable", "The file was not found.");
            if (info.Length > MaxBytes) return new("too-large", "The file is larger than a certificate file can be (1 MB).");
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new("unreadable", "The file could not be read.");
        }
        return Validate(bytes, now);
    }

    public static CaFileCheck Validate(byte[] bytes, DateTimeOffset now)
    {
        if (bytes.Length == 0) return new("no-certificate", "The file is empty.");
        var text = Encoding.ASCII.GetString(bytes);
        // Before anything else: a private key never goes into the app's copy, whatever else the file holds
        if (text.Contains("PRIVATE KEY", StringComparison.Ordinal))
            return new("private-key", "The file contains a private key. Choose a file with only the certificate authority's certificate (ask IT for the CA certificate, not a key).");

        var certificates = new List<X509Certificate2>();
        try
        {
            if (text.Contains("-----BEGIN ", StringComparison.Ordinal))
            {
                foreach (Match m in Block.Matches(text))
                {
                    if (m.Groups[1].Value != "CERTIFICATE")
                        return new("unsupported", $"The file contains a \"{m.Groups[1].Value}\" block. Only PEM certificates (BEGIN CERTIFICATE) are supported.");
                    byte[] der;
                    try { der = Convert.FromBase64String(m.Groups[2].Value); }
                    catch (FormatException) { return new("garbage", "A certificate in the file is damaged (not valid base64)."); }
                    certificates.Add(X509CertificateLoader.LoadCertificate(der));
                    if (certificates.Count > MaxCertificates) return new("too-large", $"The file has more than {MaxCertificates} certificates.");
                }
            }
            else certificates.Add(X509CertificateLoader.LoadCertificate(bytes)); // one DER certificate (.cer, .crt)
        }
        catch (CryptographicException)
        {
            Dispose(certificates);
            return new("garbage", "The file is not a certificate file (PEM or DER).");
        }

        try
        {
            if (certificates.Count == 0) return new("no-certificate", "The file contains no certificate.");
            var distinct = certificates.DistinctBy(c => c.Thumbprint).ToList();
            for (var i = 0; i < distinct.Count; i++)
            {
                var c = distinct[i];
                var which = distinct.Count == 1 ? "The certificate" : $"Certificate {i + 1} of {distinct.Count}";
                var basic = c.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
                if (basic is not { CertificateAuthority: true })
                    return new("not-ca", which + " is not a certificate authority (it looks like a server's or a person's certificate). Choose your organisation's root or intermediate CA certificate.");
                var usage = c.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
                if (usage is null || !usage.KeyUsages.HasFlag(X509KeyUsageFlags.KeyCertSign))
                    return new("not-ca", which + " is not allowed to sign certificates (no keyCertSign key usage), so it cannot be a certificate authority.");
                var notAfter = new DateTimeOffset(c.NotAfter.ToUniversalTime(), TimeSpan.Zero);
                var notBefore = new DateTimeOffset(c.NotBefore.ToUniversalTime(), TimeSpan.Zero);
                if (notAfter <= now) return new("expired", which + " expired on " + Day(notAfter) + ".");
                if (notBefore > now) return new("not-yet-valid", which + " is valid only from " + Day(notBefore) + ".");
            }
            var pem = new StringBuilder();
            foreach (var c in distinct) pem.Append(c.ExportCertificatePem()).Append('\n');
            var until = distinct.Min(c => new DateTimeOffset(c.NotAfter.ToUniversalTime(), TimeSpan.Zero));
            var count = distinct.Count == 1 ? "1 certificate authority" : $"{distinct.Count} certificate authorities";
            return new("ok", $"{count}, valid until {Day(until)}.", distinct.Count, until, pem.ToString());
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

    private static string Day(DateTimeOffset d) => d.ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture);

    private static void Dispose(List<X509Certificate2> certificates)
    {
        foreach (var c in certificates) c.Dispose();
    }
}
