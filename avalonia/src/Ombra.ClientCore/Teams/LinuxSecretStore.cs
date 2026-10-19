using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ombra.ClientCore.Teams;

/// <summary>
/// Linux (SERVER_DESIGN §2.1 SD-10): secrets in the Secret Service (GNOME Keyring, KWallet, KeePassXC…) through
/// libsecret-1, schema <c>app.ombra.teams</c> with attributes <c>service</c> and <c>account</c>, base64 text in the
/// user's default collection. No libsecret, no D-Bus session or a locked/absent keyring is
/// <see cref="KeyStoreUnavailableException"/>: there is no file or plaintext fallback.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxSecretStore : ITeamsKeyStore
{
    private const string LibSecret = "libsecret-1.so.0";
    private const string GLib = "libglib-2.0.so.0";
    private const int SecretSchemaNone = 0;
    private const int AttributeString = 0;

    private readonly string _service;
    private readonly IntPtr _schema;
    private readonly IntPtr _glib;
    private readonly IntPtr _strHash;
    private readonly IntPtr _strEqual;

    public LinuxSecretStore(string service)
    {
        _service = service;
        try
        {
            NativeLibrary.Load(LibSecret);
            _glib = NativeLibrary.Load(GLib);
        }
        catch (DllNotFoundException e)
        {
            throw new KeyStoreUnavailableException("libsecret-1 (Secret Service client) is not installed: " + e.Message);
        }
        _strHash = NativeLibrary.GetExport(_glib, "g_str_hash");
        _strEqual = NativeLibrary.GetExport(_glib, "g_str_equal");
        _schema = BuildSchema();
    }

    /// <summary>
    /// <c>SecretSchema { const gchar *name; SecretSchemaFlags flags; SecretSchemaAttribute attributes[32]; gint reserved;
    /// gpointer reserved1..7; }</c> with <c>SecretSchemaAttribute { const gchar *name; SecretSchemaAttributeType type; }</c>.
    /// Allocated once and never freed (libsecret keeps a pointer to it).
    /// </summary>
    private static IntPtr BuildSchema()
    {
        var p = IntPtr.Size;
        var attr = p + p; // name pointer + int, padded to pointer alignment
        var size = p + p + 32 * attr + p + 7 * p;
        var s = Marshal.AllocHGlobal(size);
        for (var i = 0; i < size; i++) Marshal.WriteByte(s, i, 0);
        Marshal.WriteIntPtr(s, 0, Marshal.StringToCoTaskMemUTF8(TeamsKeyStore.Service));
        Marshal.WriteInt32(s, p, SecretSchemaNone);
        var a0 = p + p;
        Marshal.WriteIntPtr(s, a0, Marshal.StringToCoTaskMemUTF8("service"));
        Marshal.WriteInt32(s, a0 + p, AttributeString);
        Marshal.WriteIntPtr(s, a0 + attr, Marshal.StringToCoTaskMemUTF8("account"));
        Marshal.WriteInt32(s, a0 + attr + p, AttributeString);
        return s;
    }

    private IntPtr Attributes(string account, List<IntPtr> owned)
    {
        var table = g_hash_table_new(_strHash, _strEqual);
        foreach (var (k, v) in new[] { ("service", _service), ("account", account) })
        {
            var kp = Marshal.StringToCoTaskMemUTF8(k);
            var vp = Marshal.StringToCoTaskMemUTF8(v);
            owned.Add(kp);
            owned.Add(vp);
            g_hash_table_insert(table, kp, vp);
        }
        return table;
    }

    private T WithAttributes<T>(string account, Func<IntPtr, T> f)
    {
        var owned = new List<IntPtr>();
        var table = Attributes(account, owned);
        try { return f(table); }
        finally
        {
            g_hash_table_unref(table);
            foreach (var x in owned) Marshal.FreeCoTaskMem(x);
        }
    }

    private static void Check(IntPtr error, string what)
    {
        if (error == IntPtr.Zero) return;
        // GError { GQuark domain; gint code; gchar *message; }
        var code = Marshal.ReadInt32(error, 4);
        var message = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8)) ?? "";
        g_error_free(error);
        throw new KeyStoreUnavailableException($"Secret Service {what} failed: {message}", code);
    }

    public byte[]? Read(string account) => WithAttributes(account, table =>
    {
        var value = secret_password_lookupv_sync(_schema, table, IntPtr.Zero, out var error);
        Check(error, "lookup");
        if (value == IntPtr.Zero) return null;
        try { return Convert.FromBase64String(Marshal.PtrToStringUTF8(value)!); }
        finally { secret_password_free(value); }
    });

    public void Write(string account, ReadOnlySpan<byte> secret)
    {
        var text = Marshal.StringToCoTaskMemUTF8(Convert.ToBase64String(secret));
        var label = Marshal.StringToCoTaskMemUTF8("Ombra team key (" + account + ")");
        try
        {
            WithAttributes(account, table =>
            {
                var ok = secret_password_storev_sync(_schema, table, IntPtr.Zero, label, text, IntPtr.Zero, out var error);
                Check(error, "store");
                if (!ok) throw new KeyStoreUnavailableException("Secret Service store failed");
                return 0;
            });
        }
        finally
        {
            // Overwrite the base64 copy before freeing it.
            for (var i = 0; Marshal.ReadByte(text, i) != 0; i++) Marshal.WriteByte(text, i, 0);
            Marshal.FreeCoTaskMem(text);
            Marshal.FreeCoTaskMem(label);
        }
    }

    public void Delete(string account) => WithAttributes(account, table =>
    {
        secret_password_clearv_sync(_schema, table, IntPtr.Zero, out var error);
        Check(error, "clear");
        return 0;
    });

    [DllImport(LibSecret)] private static extern IntPtr secret_password_lookupv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);
    [DllImport(LibSecret)] [return: MarshalAs(UnmanagedType.Bool)] // gboolean = 32-bit int
    private static extern bool secret_password_storev_sync(IntPtr schema, IntPtr attributes, IntPtr collection, IntPtr label, IntPtr password, IntPtr cancellable, out IntPtr error);
    [DllImport(LibSecret)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool secret_password_clearv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);
    [DllImport(LibSecret)] private static extern void secret_password_free(IntPtr password);
    [DllImport(GLib)] private static extern IntPtr g_hash_table_new(IntPtr hash, IntPtr equal);
    [DllImport(GLib)] private static extern void g_hash_table_insert(IntPtr table, IntPtr key, IntPtr value);
    [DllImport(GLib)] private static extern void g_hash_table_unref(IntPtr table);
    [DllImport(GLib)] private static extern void g_error_free(IntPtr error);
}
