using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ombra.ClientCore.Teams;

/// <summary>
/// macOS Keychain generic-password items (SecItem* API), <c>kSecAttrSynchronizable = false</c> (never iCloud) and
/// <c>kSecAttrAccessibleWhenUnlockedThisDeviceOnly</c>. The data-protection keychain is used when the process is
/// entitled; an unsigned/unentitled process (<c>errSecMissingEntitlement</c>) uses the login keychain with the same
/// non-synchronisable item. Every other failure is <see cref="KeyStoreUnavailableException"/> — never a file.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacKeychain : ITeamsKeyStore
{
    private const int ErrSecSuccess = 0;
    private const int ErrSecItemNotFound = -25300;
    private const int ErrSecDuplicateItem = -25299;
    private const int ErrSecMissingEntitlement = -34018;
    private const int ErrSecParam = -50;

    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private static readonly Lazy<Symbols> Sym = new(() => new Symbols());
    private readonly string _service;
    private bool? _dataProtection;

    public MacKeychain(string service) => _service = service;

    /// <summary>Whether the data-protection keychain is in use (false: login keychain, unentitled process).</summary>
    public bool UsesDataProtectionKeychain => _dataProtection ?? Probe();

    public byte[]? Read(string account)
    {
        var dp = UsesDataProtectionKeychain;
        using var q = Query(account, dp);
        q.Set(Sym.Value.kSecReturnData, Sym.Value.kCFBooleanTrue);
        q.Set(Sym.Value.kSecMatchLimit, Sym.Value.kSecMatchLimitOne);
        var status = SecItemCopyMatching(q.Handle, out var data);
        if (status == ErrSecItemNotFound) return null;
        if (status != ErrSecSuccess) throw Fail("read", status);
        try { return CopyData(data); }
        finally { CFRelease(data); }
    }

    public void Write(string account, ReadOnlySpan<byte> secret)
    {
        var dp = UsesDataProtectionKeychain;
        var value = CFDataCreate(IntPtr.Zero, secret.ToArray(), secret.Length);
        try
        {
            using var add = Query(account, dp);
            add.Set(Sym.Value.kSecValueData, value);
            add.Set(Sym.Value.kSecAttrAccessible, Sym.Value.kSecAttrAccessibleWhenUnlockedThisDeviceOnly);
            var status = SecItemAdd(add.Handle, IntPtr.Zero);
            if (status == ErrSecDuplicateItem)
            {
                Delete(account);
                status = SecItemAdd(add.Handle, IntPtr.Zero);
            }
            if (status != ErrSecSuccess) throw Fail("write", status);
        }
        finally { CFRelease(value); }
    }

    public void Delete(string account)
    {
        using var q = Query(account, UsesDataProtectionKeychain);
        var status = SecItemDelete(q.Handle);
        if (status is not (ErrSecSuccess or ErrSecItemNotFound)) throw Fail("delete", status);
    }

    /// <summary>Attributes of an item as stored (for verification): synchronizable flag and accessibility class.</summary>
    public (bool Synchronizable, string? Accessible)? Describe(string account)
    {
        using var q = Query(account, UsesDataProtectionKeychain);
        q.Set(Sym.Value.kSecReturnAttributes, Sym.Value.kCFBooleanTrue);
        q.Set(Sym.Value.kSecMatchLimit, Sym.Value.kSecMatchLimitOne);
        var status = SecItemCopyMatching(q.Handle, out var attrs);
        if (status == ErrSecItemNotFound) return null;
        if (status != ErrSecSuccess) throw Fail("describe", status);
        try
        {
            var sync = CFDictionaryGetValue(attrs, Sym.Value.kSecAttrSynchronizable);
            var acc = CFDictionaryGetValue(attrs, Sym.Value.kSecAttrAccessible);
            var synchronizable = sync != IntPtr.Zero && CFGetTypeID(sync) == CFBooleanGetTypeID() && CFBooleanGetValue(sync);
            return (synchronizable, acc == IntPtr.Zero ? null : CFString(acc));
        }
        finally { CFRelease(attrs); }
    }

    /// <summary>Accessibility class string of <c>kSecAttrAccessibleWhenUnlockedThisDeviceOnly</c> (as Describe reports it).</summary>
    public static string WhenUnlockedThisDeviceOnly => CFString(Sym.Value.kSecAttrAccessibleWhenUnlockedThisDeviceOnly)!;

    private bool Probe()
    {
        // Lookups can succeed without the entitlement; adding an item tells whether this process may use the
        // data-protection keychain (errSecMissingEntitlement otherwise).
        var value = CFDataCreate(IntPtr.Zero, [0], 1);
        try
        {
            using var q = Query("probe", dataProtection: true);
            q.Set(Sym.Value.kSecValueData, value);
            q.Set(Sym.Value.kSecAttrAccessible, Sym.Value.kSecAttrAccessibleWhenUnlockedThisDeviceOnly);
            var status = SecItemAdd(q.Handle, IntPtr.Zero);
            _dataProtection = status switch
            {
                ErrSecSuccess or ErrSecDuplicateItem => true,
                ErrSecMissingEntitlement => false,
                _ => throw Fail("open", status),
            };
            if (_dataProtection.Value)
            {
                using var d = Query("probe", dataProtection: true);
                SecItemDelete(d.Handle);
            }
            return _dataProtection.Value;
        }
        finally { CFRelease(value); }
    }

    private Dict Query(string account, bool dataProtection)
    {
        var s = Sym.Value;
        var d = new Dict();
        d.Set(s.kSecClass, s.kSecClassGenericPassword);
        d.SetString(s.kSecAttrService, _service);
        d.SetString(s.kSecAttrAccount, account);
        d.Set(s.kSecAttrSynchronizable, s.kCFBooleanFalse);
        if (dataProtection) d.Set(s.kSecUseDataProtectionKeychain, s.kCFBooleanTrue);
        return d;
    }

    private static KeyStoreUnavailableException Fail(string what, int status) =>
        new($"macOS Keychain {what} failed (OSStatus {status}{(status == ErrSecParam ? ", bad parameter" : "")})", status);

    private static byte[] CopyData(IntPtr data)
    {
        var len = (int)CFDataGetLength(data);
        var bytes = new byte[len];
        if (len > 0) Marshal.Copy(CFDataGetBytePtr(data), bytes, 0, len);
        return bytes;
    }

    private static string? CFString(IntPtr s)
    {
        if (s == IntPtr.Zero || CFGetTypeID(s) != CFStringGetTypeID()) return null;
        var buf = new byte[256];
        return CFStringGetCString(s, buf, buf.Length, 0x08000100) ? System.Text.Encoding.UTF8.GetString(buf, 0, Array.IndexOf(buf, (byte)0)) : null;
    }

    private sealed class Dict : IDisposable
    {
        private readonly List<IntPtr> _owned = [];
        public IntPtr Handle { get; } = CFDictionaryCreateMutable(IntPtr.Zero, 0, Sym.Value.KeyCallBacks, Sym.Value.ValueCallBacks);
        public void Set(IntPtr key, IntPtr value) => CFDictionarySetValue(Handle, key, value);
        public void SetString(IntPtr key, string value)
        {
            var s = CFStringCreateWithCString(IntPtr.Zero, value, 0x08000100);
            _owned.Add(s);
            Set(key, s);
        }
        public void Dispose()
        {
            foreach (var o in _owned) CFRelease(o);
            CFRelease(Handle);
        }
    }

    private sealed class Symbols
    {
        public readonly IntPtr kSecClass, kSecClassGenericPassword, kSecAttrService, kSecAttrAccount, kSecValueData, kSecAttrAccessible,
            kSecAttrAccessibleWhenUnlockedThisDeviceOnly, kSecAttrSynchronizable, kSecUseDataProtectionKeychain, kSecReturnData,
            kSecReturnAttributes, kSecMatchLimit, kSecMatchLimitOne, kCFBooleanTrue, kCFBooleanFalse, KeyCallBacks, ValueCallBacks;

        public Symbols()
        {
            var sec = NativeLibrary.Load(Security);
            var cf = NativeLibrary.Load(CoreFoundation);
            IntPtr S(IntPtr lib, string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(lib, name));
            kSecClass = S(sec, "kSecClass");
            kSecClassGenericPassword = S(sec, "kSecClassGenericPassword");
            kSecAttrService = S(sec, "kSecAttrService");
            kSecAttrAccount = S(sec, "kSecAttrAccount");
            kSecValueData = S(sec, "kSecValueData");
            kSecAttrAccessible = S(sec, "kSecAttrAccessible");
            kSecAttrAccessibleWhenUnlockedThisDeviceOnly = S(sec, "kSecAttrAccessibleWhenUnlockedThisDeviceOnly");
            kSecAttrSynchronizable = S(sec, "kSecAttrSynchronizable");
            kSecUseDataProtectionKeychain = S(sec, "kSecUseDataProtectionKeychain");
            kSecReturnData = S(sec, "kSecReturnData");
            kSecReturnAttributes = S(sec, "kSecReturnAttributes");
            kSecMatchLimit = S(sec, "kSecMatchLimit");
            kSecMatchLimitOne = S(sec, "kSecMatchLimitOne");
            kCFBooleanTrue = S(cf, "kCFBooleanTrue");
            kCFBooleanFalse = S(cf, "kCFBooleanFalse");
            KeyCallBacks = NativeLibrary.GetExport(cf, "kCFTypeDictionaryKeyCallBacks");
            ValueCallBacks = NativeLibrary.GetExport(cf, "kCFTypeDictionaryValueCallBacks");
        }
    }

    [DllImport(Security)] private static extern int SecItemAdd(IntPtr attributes, IntPtr result);
    [DllImport(Security)] private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);
    [DllImport(Security)] private static extern int SecItemDelete(IntPtr query);
    [DllImport(CoreFoundation)] private static extern IntPtr CFDictionaryCreateMutable(IntPtr allocator, nint capacity, IntPtr keyCallBacks, IntPtr valueCallBacks);
    [DllImport(CoreFoundation)] private static extern void CFDictionarySetValue(IntPtr dict, IntPtr key, IntPtr value);
    [DllImport(CoreFoundation)] private static extern IntPtr CFDictionaryGetValue(IntPtr dict, IntPtr key);
    [DllImport(CoreFoundation)] private static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);
    [DllImport(CoreFoundation)] private static extern nint CFDataGetLength(IntPtr data);
    [DllImport(CoreFoundation)] private static extern IntPtr CFDataGetBytePtr(IntPtr data);
    [DllImport(CoreFoundation, CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);
    [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFStringGetCString(IntPtr s, byte[] buffer, nint size, uint encoding);
    [DllImport(CoreFoundation)] private static extern nuint CFGetTypeID(IntPtr cf);
    [DllImport(CoreFoundation)] private static extern nuint CFStringGetTypeID();
    [DllImport(CoreFoundation)] private static extern nuint CFBooleanGetTypeID();
    [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFBooleanGetValue(IntPtr b);
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr cf);
}
