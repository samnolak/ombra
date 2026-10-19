using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Ombra.ClientCore.Teams;

/// <summary>
/// Windows (SERVER_DESIGN §2.1 SD-10): each secret is a DPAPI blob (<c>CryptProtectData</c>, CurrentUser scope, UI
/// forbidden, entropy bound to the account name) in a file under the user's local, non-roaming app data. Only the
/// DPAPI ciphertext touches disk; a failing DPAPI call is <see cref="KeyStoreUnavailableException"/>, never plaintext.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDpapiStore(string directory) : ITeamsKeyStore
{
    private const int CryptprotectUiForbidden = 0x1;

    public string Directory { get; } = directory;

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "Ombra", "teams", "keys");

    private string PathOf(string account)
    {
        if (account.Length == 0 || account.Length > 200) throw new ArgumentException("bad account", nameof(account));
        // Account names contain '/', so the file name is their hex form.
        return Path.Combine(Directory, Convert.ToHexStringLower(Encoding.UTF8.GetBytes(account)) + ".dpapi");
    }

    private static byte[] Entropy(string account) => Encoding.UTF8.GetBytes("ombra/v1/teams-key\0" + account);

    public byte[]? Read(string account)
    {
        var path = PathOf(account);
        if (!File.Exists(path)) return null;
        byte[] blob;
        try { blob = File.ReadAllBytes(path); }
        catch (IOException e) { throw new KeyStoreUnavailableException("DPAPI key file unreadable: " + e.Message); }
        return Unprotect(blob, Entropy(account));
    }

    public void Write(string account, ReadOnlySpan<byte> secret)
    {
        var blob = Protect(secret, Entropy(account));
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var path = PathOf(account);
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, blob);
            File.Move(tmp, path, overwrite: true);
        }
        catch (IOException e) { throw new KeyStoreUnavailableException("DPAPI key file not writable: " + e.Message); }
        catch (UnauthorizedAccessException e) { throw new KeyStoreUnavailableException("DPAPI key file not writable: " + e.Message); }
    }

    public void Delete(string account)
    {
        try { File.Delete(PathOf(account)); }
        catch (IOException e) { throw new KeyStoreUnavailableException("DPAPI key file not deletable: " + e.Message); }
    }

    private static byte[] Protect(ReadOnlySpan<byte> data, byte[] entropy) => Call(data.ToArray(), entropy, protect: true);

    private static byte[] Unprotect(byte[] blob, byte[] entropy) => Call(blob, entropy, protect: false);

    private static byte[] Call(byte[] data, byte[] entropy, bool protect)
    {
        var d = GCHandle.Alloc(data, GCHandleType.Pinned);
        var en = GCHandle.Alloc(entropy, GCHandleType.Pinned);
        try
        {
            var input = new DataBlob { cbData = data.Length, pbData = d.AddrOfPinnedObject() };
            var ent = new DataBlob { cbData = entropy.Length, pbData = en.AddrOfPinnedObject() };
            var ok = protect
                ? CryptProtectData(ref input, null, ref ent, IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, out var output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref ent, IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, out output);
            if (!ok)
                throw new KeyStoreUnavailableException(protect ? "CryptProtectData failed" : "CryptUnprotectData failed (other user or machine?)",
                    Marshal.GetLastPInvokeError());
            return Take(output);
        }
        finally
        {
            if (protect) Array.Clear(data); // the plaintext copy
            d.Free();
            en.Free();
        }
    }

    private static byte[] Take(DataBlob b)
    {
        try
        {
            var r = new byte[b.cbData];
            Marshal.Copy(b.pbData, r, 0, b.cbData);
            return r;
        }
        finally
        {
            if (b.pbData != IntPtr.Zero)
            {
                for (var i = 0; i < b.cbData; i++) Marshal.WriteByte(b.pbData, i, 0);
                LocalFree(b.pbData);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);
}
