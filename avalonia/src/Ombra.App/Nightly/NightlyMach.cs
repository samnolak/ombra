using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Ombra.App.Nightly;

/// <summary>
/// Mach, IOSurface, libproc and code-signing calls for the Nightly embed channel (<see cref="NightlyChannel"/>,
/// <see cref="NightlyRuntime"/>): the service port Nightly looks up, the receive loop, sending, and who a sender is.
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe partial class NightlyMach
{
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private const string IOSurfaceLib = "/System/Library/Frameworks/IOSurface.framework/IOSurface";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";

    public const int KernSuccess = 0;
    private const int MachSendMsg = 1, MachRcvMsg = 2, MachSendTimeout = 0x10;
    // MACH_RCV_TRAILER_TYPE(MACH_MSG_TRAILER_FORMAT_0) | MACH_RCV_TRAILER_ELEMENTS(MACH_RCV_TRAILER_AUDIT)
    private const int RcvAuditTrailer = 3 << 24;
    private const int PortRightReceive = 1, PortLimitsInfo = 1;
    private const int ProcPpidOnly = 6;

    private static readonly nint System = NativeLibrary.Load(LibSystem);
    public static uint BootstrapPort => (uint)Marshal.ReadInt32(NativeLibrary.GetExport(System, "bootstrap_port"));
    public static uint TaskSelf => (uint)Marshal.ReadInt32(NativeLibrary.GetExport(System, "mach_task_self_"));

    /// <summary>A receive right registered under <paramref name="name"/> in the user's bootstrap namespace; 0 on failure.</summary>
    public static uint CheckIn(string name, int queueLimit)
    {
        if (bootstrap_check_in(BootstrapPort, name, out var port) != KernSuccess) return 0;
        var limit = queueLimit;
        mach_port_set_attributes(TaskSelf, port, PortLimitsInfo, &limit, 1);
        return port;
    }

    /// <summary>Drops the receive right: a thread blocked in <see cref="Receive"/> on it returns an error.</summary>
    public static void DestroyReceiveRight(uint port) => mach_port_mod_refs(TaskSelf, port, PortRightReceive, -1);

    public static void Deallocate(uint name)
    {
        if (name != 0) mach_port_deallocate(TaskSelf, name);
    }

    /// <summary>One message into <paramref name="buffer"/> (blocks); the kernel's code otherwise.</summary>
    public static int Receive(uint port, byte[] buffer)
    {
        fixed (byte* b = buffer)
            return mach_msg(b, MachRcvMsg | RcvAuditTrailer, 0, (uint)buffer.Length - 52, port, 0, 0);
    }

    /// <summary>The sender of a message <see cref="Receive"/> filled: audit token pid, effective uid and pid version.</summary>
    public static (int Pid, uint Euid, uint PidVersion) Sender(ReadOnlySpan<byte> message)
    {
        var size = BitConverter.ToInt32(message[4..]);
        var token = ((size + 3) & ~3) + 20; // trailer: type, size, seqno, sender security token (8), then the audit token
        return (BitConverter.ToInt32(message[(token + 20)..]), BitConverter.ToUInt32(message[(token + 4)..]),
            BitConverter.ToUInt32(message[(token + 28)..]));
    }

    /// <summary>Sends <paramref name="message"/> (its header's size and id set) to <paramref name="port"/>, waiting at most <paramref name="timeoutMs"/>.</summary>
    public static int Send(uint port, byte[] message, uint timeoutMs)
    {
        fixed (byte* b = message)
        {
            *(uint*)(b + 8) = port;
            *(uint*)(b + 12) = 0;
            return mach_msg(b, MachSendMsg | MachSendTimeout, (uint)message.Length, 0, 0, timeoutMs, 0);
        }
    }

    /// <summary>
    /// The out-of-line memory a received complex message carries: the first OOL region's bytes when it is at most
    /// <paramref name="limit"/> bytes, null otherwise. Every OOL region the kernel mapped in is unmapped, whoever sent
    /// it; port rights stay for the caller.
    /// </summary>
    public static byte[]? TakeOutOfLine(ReadOnlySpan<byte> message, int limit)
    {
        var size = BitConverter.ToInt32(message[4..]);
        if ((BitConverter.ToUInt32(message) & 0x80000000) == 0 || size < 28) return null;
        var count = BitConverter.ToUInt32(message[24..]);
        byte[]? first = null;
        var offset = 28;
        for (var i = 0u; i < count && offset + 12 <= size; i++)
        {
            var type = message[offset + 11]; // every descriptor's type is its twelfth byte
            if (type == 0) // MACH_MSG_PORT_DESCRIPTOR
            {
                offset += 12;
                continue;
            }
            if (offset + 16 > size) break;
            if (type is 1 or 2 or 3) // OOL, OOL ports, OOL volatile
            {
                var address = (nint)BitConverter.ToInt64(message[offset..]);
                var length = BitConverter.ToUInt32(message[(offset + 12)..]);
                var bytes = type == 2 ? length * 4u : length; // OOL ports: a count of port names
                if (type != 2 && first is null && bytes <= (uint)limit)
                {
                    first = new byte[bytes];
                    if (bytes > 0) Marshal.Copy(address, first, 0, (int)bytes);
                }
                if (address != 0 && bytes > 0) vm_deallocate(TaskSelf, address, bytes);
            }
            offset += 16;
        }
        return first;
    }

    /// <summary>The IOSurface a send right names (+1: <see cref="CFRelease"/> it); the right itself is released.</summary>
    public static nint SurfaceFromPort(uint port)
    {
        var surface = IOSurfaceLookupFromMachPort(port);
        Deallocate(port);
        return surface;
    }

    public static void CFRelease(nint obj)
    {
        if (obj != 0) CFReleaseNative(obj);
    }

    public static nint CFRetain(nint obj) => obj == 0 ? 0 : CFRetainNative(obj);

    /// <summary>The executable of <paramref name="pid"/>, symlinks resolved; null when the process is gone.</summary>
    public static string? ExecutableOf(int pid)
    {
        var buffer = stackalloc byte[4096];
        var n = proc_pidpath(pid, buffer, 4096);
        return n > 0 ? RealPath(Encoding.UTF8.GetString(buffer, n)) : null;
    }

    public static string RealPath(string path)
    {
        var p = realpath(path, 0);
        if (p == 0) return path;
        try { return Marshal.PtrToStringUTF8(p)!; }
        finally { free(p); }
    }

    public static uint EffectiveUserId => geteuid();

    /// <summary>The live children of <paramref name="pid"/>.</summary>
    public static List<int> Children(int pid)
    {
        var pids = new int[4096];
        int bytes;
        fixed (int* p = pids) bytes = proc_listpids(ProcPpidOnly, (uint)pid, p, pids.Length * sizeof(int));
        var list = new List<int>();
        for (var i = 0; i < bytes / sizeof(int); i++)
            if (pids[i] > 0) list.Add(pids[i]);
        return list;
    }

    public static bool Signal(int pid, int signal) => kill(pid, signal) == 0;

    public static bool IsAlive(int pid) => kill(pid, 0) == 0 || Marshal.GetLastPInvokeError() == 1 /* EPERM: alive, not ours */;

    /// <summary>
    /// The designated requirement of this process's code, when it is a signed app of its own (a Team ID under Apple's
    /// anchor): what Nightly checks the embedder against. Null for ad-hoc or unsigned code and for the shared
    /// <c>dotnet</c> host (a requirement naming Microsoft's signature would admit any .NET program of the user).
    /// </summary>
    public static string? OwnDesignatedRequirement()
    {
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "") == "dotnet") return null;
        nint code = 0, staticCode = 0, requirement = 0, text = 0;
        try
        {
            if (SecCodeCopySelf(0, out code) != 0) return null;
            if (SecCodeCopyStaticCode(code, 0, out staticCode) != 0) return null;
            if (SecCodeCopyDesignatedRequirement(staticCode, 0, out requirement) != 0) return null;
            if (SecRequirementCopyString(requirement, 0, out text) != 0) return null;
            var s = Ombra.App.Platform.Mac.MacObjC.ToStr(text);
            return s is not null && s.Contains("anchor apple generic", StringComparison.Ordinal) && s.Contains("subject.OU", StringComparison.Ordinal) ? s : null;
        }
        finally
        {
            CFRelease(text);
            CFRelease(requirement);
            CFRelease(staticCode);
            CFRelease(code);
        }
    }

    [LibraryImport(LibSystem, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int bootstrap_check_in(uint bootstrapPort, string serviceName, out uint port);

    [LibraryImport(LibSystem)]
    private static partial int mach_msg(byte* msg, int option, uint sendSize, uint rcvSize, uint rcvName, uint timeout, uint notify);

    [LibraryImport(LibSystem)]
    private static partial int vm_deallocate(uint task, nint address, nuint size);

    [LibraryImport(LibSystem)]
    private static partial int mach_port_deallocate(uint task, uint name);

    [LibraryImport(LibSystem)]
    private static partial int mach_port_mod_refs(uint task, uint name, int right, int delta);

    [LibraryImport(LibSystem)]
    private static partial int mach_port_set_attributes(uint task, uint name, int flavor, int* info, uint count);

    [LibraryImport(LibSystem)]
    private static partial int proc_pidpath(int pid, byte* buffer, uint size);

    [LibraryImport(LibSystem)]
    private static partial int proc_listpids(uint type, uint typeinfo, int* buffer, int bufferSize);

    [LibraryImport(LibSystem, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint realpath(string path, nint resolved);

    [LibraryImport(LibSystem)]
    private static partial void free(nint p);

    [LibraryImport(LibSystem)]
    private static partial uint geteuid();

    [LibraryImport(LibSystem, SetLastError = true)]
    private static partial int kill(int pid, int signal);

    [LibraryImport(IOSurfaceLib)]
    private static partial nint IOSurfaceLookupFromMachPort(uint port);

    [LibraryImport(CoreFoundation, EntryPoint = "CFRelease")]
    private static partial void CFReleaseNative(nint obj);

    [LibraryImport(CoreFoundation, EntryPoint = "CFRetain")]
    private static partial nint CFRetainNative(nint obj);

    [LibraryImport(Security)]
    private static partial int SecCodeCopySelf(uint flags, out nint code);

    [LibraryImport(Security)]
    private static partial int SecCodeCopyStaticCode(nint code, uint flags, out nint staticCode);

    [LibraryImport(Security)]
    private static partial int SecCodeCopyDesignatedRequirement(nint code, uint flags, out nint requirement);

    [LibraryImport(Security)]
    private static partial int SecRequirementCopyString(nint requirement, uint flags, out nint text);
}
