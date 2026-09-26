using System.Runtime.InteropServices;

namespace OmpGui.App.Platform;

/// <summary>Platform Layer: which pinned Bun build (see <c>RuntimePack.Bun</c>) runs omp on this machine.</summary>
public static class RuntimePlatform
{
    /// <summary>
    /// <c>linux-x64</c>, <c>darwin-aarch64</c>, <c>windows-x64-baseline</c>… or null when there is no pinned build
    /// (other CPUs, musl Linux: omp's native modules are built for glibc). x64 CPUs without AVX2 get Bun's baseline build.
    /// </summary>
    /// <summary>
    /// The package this process updates to: <c>win-x64</c>, <c>osx-arm64</c>, <c>linux-x64</c>… (portable RIDs, as the
    /// packages and update.json name them). <see cref="RuntimeInformation.RuntimeIdentifier"/> is that for the
    /// self-contained packages, but a framework-dependent run on a distro-built .NET reports the distro's own RID
    /// (<c>ubuntu.24.04-x64</c>), which no package carries. The process architecture decides (an x64 app under
    /// Rosetta 2 updates to the x64 package it is).
    /// </summary>
    public static string PackageRid()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsLinux() ? (IsMusl() ? "linux-musl" : "linux") : null;
        var cpu = RuntimeInformation.ProcessArchitecture switch { Architecture.X64 => "x64", Architecture.Arm64 => "arm64", Architecture.X86 => "x86", Architecture.Arm => "arm", _ => null };
        return os is null || cpu is null ? RuntimeInformation.RuntimeIdentifier : $"{os}-{cpu}";
    }

    public static string? Key(out string description)
    {
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "darwin" : OperatingSystem.IsLinux() ? "linux" : null;
        var cpu = RuntimeInformation.OSArchitecture switch { Architecture.X64 => "x64", Architecture.Arm64 => "aarch64", _ => null };
        description = $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";
        if (os is null || cpu is null) return null;
        if (os == "linux" && IsMusl()) return null;
        var key = $"{os}-{cpu}";
        // Under emulation (x64 process on an Arm64 OS) OSArchitecture is the OS's, so this only checks real x64 CPUs.
        if (cpu == "x64" && !System.Runtime.Intrinsics.X86.Avx2.IsSupported) key += "-baseline";
        return key;
    }

    private static bool IsMusl()
    {
        try { return Directory.Exists("/lib") && Directory.EnumerateFiles("/lib", "ld-musl-*").Any(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
