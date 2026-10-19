using System.Reflection;
using System.Runtime.InteropServices;

namespace Ombra.Core.Interop;

/// <summary>Buffer allocated by ombra-core; released exactly once with <c>ombra_buf_free</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct OmbraBuf
{
    public nint Ptr;
    public nuint Len;
}

/// <summary>
/// Raw P/Invoke surface of ombra-core (src/ffi.rs). The only unsafe code of the wrapper is here and in
/// <see cref="Ffi"/>: spans are pinned for the duration of each call, every returned buffer is copied
/// into managed memory and freed in a <c>finally</c>.
/// </summary>
internal static partial class Native
{
    internal const string Lib = "ombra_core";

    // Explicit static constructor: runs before the first P/Invoke and installs the resolver once.
    static Native() => NativeLoader.Install();

    [LibraryImport(Lib)]
    internal static unsafe partial ulong ombra_state_open(byte* path, nuint pathLen, byte* key, nuint keyLen,
        byte* install, nuint installLen, OmbraBuf* err);

    [LibraryImport(Lib)]
    internal static unsafe partial int ombra_state_call(ulong handle, byte* req, nuint reqLen, OmbraBuf* output);

    [LibraryImport(Lib)]
    internal static partial int ombra_state_close(ulong handle);

    [LibraryImport(Lib)]
    internal static unsafe partial int ombra_call(byte* req, nuint reqLen, OmbraBuf* output);

    [LibraryImport(Lib)]
    internal static partial void ombra_buf_free(OmbraBuf buf);
}

internal static class Ffi
{
    /// <summary>Copy a native buffer into a managed array and free it.</summary>
    internal static unsafe byte[] Take(OmbraBuf b)
    {
        try
        {
            if (b.Ptr == 0 || b.Len == 0) return [];
            return new ReadOnlySpan<byte>((void*)b.Ptr, checked((int)b.Len)).ToArray();
        }
        finally
        {
            if (b.Ptr != 0) Native.ombra_buf_free(b);
        }
    }

    internal static string Rc(int rc) => rc switch
    {
        -1 => "unknown or closed state handle",
        -2 => "invalid arguments",
        -3 => "internal panic in ombra-core",
        _ => $"rc={rc}",
    };
}

/// <summary>
/// Resolves "ombra_core": <c>OMBRA_CORE_NATIVE_PATH</c> env var, then AppContext switch
/// <c>Ombra.Core.NativePath</c> (absolute or relative to the app directory), then the default probe.
/// </summary>
internal static class NativeLoader
{
    internal static void Install() => NativeLibrary.SetDllImportResolver(typeof(NativeLoader).Assembly, Resolve);

    private static nint Resolve(string name, Assembly asm, DllImportSearchPath? path)
    {
        if (name != Native.Lib) return 0;
        var configured = Environment.GetEnvironmentVariable("OMBRA_CORE_NATIVE_PATH")
            ?? AppContext.GetData("Ombra.Core.NativePath") as string;
        if (!string.IsNullOrEmpty(configured))
        {
            var full = Path.IsPathRooted(configured) ? configured : Path.Combine(AppContext.BaseDirectory, configured);
            return NativeLibrary.Load(full);
        }
        return NativeLibrary.TryLoad(name, asm, path, out var h) ? h : 0;
    }
}
