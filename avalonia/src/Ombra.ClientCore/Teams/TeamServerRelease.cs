namespace Ombra.ClientCore.Teams;

/// <summary>The signed team-server release Ombra installs over SSH (published by .github/workflows/server-release.yml).</summary>
public static class TeamServerRelease
{
    public const string Version = "0.1.0";
    public const string Url = "https://github.com/samnolak/ombra/releases/download/server-v0.1.0/ombra-server-0.1.0.tar.gz";
    public const string Sha256 = "1c8128c6eb3ca885a5b299d6376299efab94da2526266ac0d0307b349029516b";

    /// <summary>Publisher key that signs SHA256SUMS; identical to server/deploy/release.pub.</summary>
    public const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEb9cL2TnBKYFRJCtbNwTGvmhOB93x
        j6eE6leAcRDiBwmNReLUomT5PRAzpFpUuIb+OA2fXADNqZTVaZSrwqNJIA==
        -----END PUBLIC KEY-----

        """;
}
