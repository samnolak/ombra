using System.Security.Cryptography;
using System.Text;

namespace OmpGui.Tests;

/// <summary>A throwaway release key for the tests: manifests are signed with it the way tools/release/sign-manifest.sh does.</summary>
internal static class TestSigning
{
    private static readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>The public half as the client takes it (SubjectPublicKeyInfo, base64).</summary>
    public static readonly string PublicKey = Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo());

    /// <summary><c>update.json.sig</c> for <paramref name="manifest"/>: base64 of a DER ECDSA signature over SHA-256.</summary>
    public static byte[] Sign(byte[] manifest) =>
        Encoding.ASCII.GetBytes(Convert.ToBase64String(Key.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) + "\n");
}
