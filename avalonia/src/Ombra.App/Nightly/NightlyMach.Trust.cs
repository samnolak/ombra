using System.Runtime.InteropServices;

namespace Ombra.App.Nightly;

// Release trust without a Developer ID (docs/RELEASE_GATES.md, SECURITY.md): both apps are signed ad hoc, so each pins
// the other by its code directory hash (cdhash). Ombra checks Nightly against the cdhash of the pinned runtime manifest
// (nightly-runtime.json); Nightly checks Ombra against the cdhash Ombra writes into the runtime profile
// (zen.embed.peer-cdhash). Both use the same code requirement, `cdhash H"<hex>"`, on the running process the
// kernel's audit token names.
internal static unsafe partial class NightlyMach
{
    /// <summary>The code requirement that only code with this code directory hash satisfies.</summary>
    public static string CdhashRequirement(string cdhash) =>
        NightlyRuntimePackage.IsCdhash(cdhash) ? $"cdhash H\"{cdhash}\"" : throw new ArgumentException($"not a cdhash: {cdhash}", nameof(cdhash));

    /// <summary>
    /// Whether the running process the audit token names has a valid code signature and its code directory hash is
    /// <paramref name="cdhash"/> (<c>SecCodeCheckValidity</c> with <see cref="CdhashRequirement"/>): an unsigned,
    /// modified or different binary does not.
    /// </summary>
    public static bool HasCdhash(byte[] auditToken, string cdhash)
    {
        if (!NightlyRuntimePackage.IsCdhash(cdhash) || auditToken.Length != AuditTokenSize) return false;
        nint data = 0, attributes = 0, code = 0, text = 0, requirement = 0;
        try
        {
            fixed (byte* t = auditToken) data = CFDataCreate(0, t, auditToken.Length);
            var security = NativeLibrary.Load(Security);
            var cf = NativeLibrary.Load(CoreFoundation);
            var key = *(nint*)NativeLibrary.GetExport(security, "kSecGuestAttributeAudit");
            var value = data;
            attributes = CFDictionaryCreate(0, &key, &value, 1, NativeLibrary.GetExport(cf, "kCFTypeDictionaryKeyCallBacks"),
                NativeLibrary.GetExport(cf, "kCFTypeDictionaryValueCallBacks"));
            if (SecCodeCopyGuestWithAttributes(0, attributes, 0, out code) != 0) return false;
            text = CFStringCreateWithCString(0, CdhashRequirement(cdhash), 0x08000100 /* kCFStringEncodingUTF8 */);
            if (SecRequirementCreateWithString(text, 0, out requirement) != 0) return false;
            return SecCodeCheckValidity(code, 0, requirement) == 0;
        }
        finally
        {
            CFRelease(requirement);
            CFRelease(text);
            CFRelease(code);
            CFRelease(attributes);
            CFRelease(data);
        }
    }

    /// <summary>
    /// The code directory hash of this process's main executable when its signature is valid: what Nightly pins Ombra
    /// by when Ombra has no Developer ID (<see cref="OwnDesignatedRequirement"/> is null). Null when unsigned, and for
    /// the shared <c>dotnet</c> host (its hash would admit any .NET program of the user).
    /// </summary>
    public static string? OwnCdhash()
    {
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "") == "dotnet") return null;
        nint code = 0, staticCode = 0, info = 0;
        try
        {
            if (SecCodeCopySelf(0, out code) != 0 || SecCodeCheckValidity(code, 0, 0) != 0) return null;
            if (SecCodeCopyStaticCode(code, 0, out staticCode) != 0) return null;
            if (SecCodeCopySigningInformation(staticCode, 2 /* kSecCSSigningInformation */, out info) != 0) return null;
            var key = *(nint*)NativeLibrary.GetExport(NativeLibrary.Load(Security), "kSecCodeInfoUnique");
            var unique = CFDictionaryGetValue(info, key);
            if (unique == 0 || CFDataGetLength(unique) != 20) return null;
            return Convert.ToHexStringLower(new ReadOnlySpan<byte>(CFDataGetBytePtr(unique), 20));
        }
        finally
        {
            CFRelease(info);
            CFRelease(staticCode);
            CFRelease(code);
        }
    }

    [LibraryImport(CoreFoundation)]
    private static partial nint CFDataCreate(nint allocator, byte* bytes, nint length);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFDataGetLength(nint data);

    [LibraryImport(CoreFoundation)]
    private static partial byte* CFDataGetBytePtr(nint data);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFDictionaryCreate(nint allocator, nint* keys, nint* values, nint count, nint keyCallbacks, nint valueCallbacks);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFDictionaryGetValue(nint dictionary, nint key);

    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint CFStringCreateWithCString(nint allocator, string text, uint encoding);

    [LibraryImport(Security)]
    private static partial int SecCodeCopyGuestWithAttributes(nint host, nint attributes, uint flags, out nint guest);

    [LibraryImport(Security)]
    private static partial int SecCodeCheckValidity(nint code, uint flags, nint requirement);

    [LibraryImport(Security)]
    private static partial int SecRequirementCreateWithString(nint text, uint flags, out nint requirement);

    [LibraryImport(Security)]
    private static partial int SecCodeCopySigningInformation(nint code, uint flags, out nint information);
}
