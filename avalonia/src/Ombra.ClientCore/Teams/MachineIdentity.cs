using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Ombra.ClientCore.Teams;

/// <summary>Hardware identity of this machine, mixed into the installId (SD-10 clone detection).</summary>
public interface IMachineIdentity
{
    /// <summary>Stable per-machine bytes; a profile restored on another machine sees different ones.</summary>
    byte[] MachineId();
}

/// <summary>macOS: <c>IOPlatformUUID</c> of the IOPlatformExpertDevice (not carried by Time Machine or Migration Assistant).</summary>
[SupportedOSPlatform("macos")]
public sealed class MacMachineIdentity : IMachineIdentity
{
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    public byte[] MachineId()
    {
        var matching = IOServiceMatching("IOPlatformExpertDevice");
        var service = IOServiceGetMatchingService(0, matching); // consumes `matching`
        if (service == 0) throw new KeyStoreUnavailableException("IOPlatformExpertDevice not found");
        try
        {
            var key = CFStringCreateWithCString(IntPtr.Zero, "IOPlatformUUID", 0x08000100);
            try
            {
                var value = IORegistryEntryCreateCFProperty(service, key, IntPtr.Zero, 0);
                if (value == IntPtr.Zero) throw new KeyStoreUnavailableException("IOPlatformUUID unavailable");
                try
                {
                    var buf = new byte[128];
                    if (!CFStringGetCString(value, buf, buf.Length, 0x08000100)) throw new KeyStoreUnavailableException("IOPlatformUUID unreadable");
                    return buf.AsSpan(0, Array.IndexOf(buf, (byte)0)).ToArray();
                }
                finally { CFRelease(value); }
            }
            finally { CFRelease(key); }
        }
        finally { IOObjectRelease(service); }
    }

    [DllImport(IOKit, CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern IntPtr IOServiceMatching([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(IOKit)] private static extern uint IOServiceGetMatchingService(uint mainPort, IntPtr matching);
    [DllImport(IOKit)] private static extern IntPtr IORegistryEntryCreateCFProperty(uint entry, IntPtr key, IntPtr allocator, uint options);
    [DllImport(IOKit)] private static extern int IOObjectRelease(uint obj);
    [DllImport(CoreFoundation, CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);
    [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFStringGetCString(IntPtr s, byte[] buffer, nint size, uint encoding);
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr cf);
}

/// <summary>Windows: <c>HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid</c> (created at OS install, not in user profiles).</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsMachineIdentity : IMachineIdentity
{
    public byte[] MachineId()
    {
        using var key = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64)
            .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        return key?.GetValue("MachineGuid") is string g && g.Length > 0
            ? System.Text.Encoding.UTF8.GetBytes(g)
            : throw new KeyStoreUnavailableException("MachineGuid unavailable");
    }
}

/// <summary>Linux: <c>/etc/machine-id</c> (or the D-Bus copy); a home directory restored on another machine sees another id.</summary>
public sealed class LinuxMachineIdentity(string path = "/etc/machine-id", string fallback = "/var/lib/dbus/machine-id") : IMachineIdentity
{
    public byte[] MachineId()
    {
        foreach (var p in new[] { path, fallback })
            if (File.Exists(p) && File.ReadAllText(p).Trim() is { Length: > 0 } id) return System.Text.Encoding.UTF8.GetBytes(id);
        throw new KeyStoreUnavailableException("machine-id unavailable");
    }
}

public static class InstallId
{
    /// <summary><c>installId = SHA-256("ombra/v1/install" ‖ 0x00 ‖ installSecret ‖ machineId)</c>, 16 bytes.</summary>
    public static byte[] Derive(ReadOnlySpan<byte> installSecret, ReadOnlySpan<byte> machineId)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData("ombra/v1/install\0"u8);
        h.AppendData(installSecret);
        h.AppendData(machineId);
        return h.GetHashAndReset().AsSpan(0, 16).ToArray();
    }

    public static IMachineIdentity ForCurrentPlatform()
    {
        if (OperatingSystem.IsMacOS()) return new MacMachineIdentity();
        if (OperatingSystem.IsWindows()) return new WindowsMachineIdentity();
        if (OperatingSystem.IsLinux()) return new LinuxMachineIdentity();
        throw new KeyStoreUnavailableException("no machine identity on this platform");
    }
}
