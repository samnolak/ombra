using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Ombra.ClientCore;

public sealed partial class OwnedProcesses
{
    private const uint KillOnJobClose = 0x2000;
    private const uint BreakawayOk = 0x0800;
    private const uint CreateBreakawayFromJob = 0x01000000;
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private static readonly object WindowsJobLock = new();
    // Intentionally process-lifetime, not IDisposable or a finalizable handle: closing this job also kills the app.
    // The kernel closes it on exit/crash. NULL security attributes make it non-inheritable; no child can keep it alive.
    private static nint _windowsJob;

    private static void EnsureWindowsJob()
    {
        lock (WindowsJobLock)
            if (_windowsJob == 0) _windowsJob = AttachWindowsJob(new WindowsJobApi());
    }

    internal interface IWindowsJobApi
    {
        nint Create();
        void Configure(nint job, uint limits);
        void AssignCurrentProcess(nint job);
        void Close(nint job);
    }

    internal static nint AttachWindowsJob(IWindowsJobApi api)
    {
        var job = api.Create();
        try
        {
            // BREAKAWAY_OK is deliberate, not SILENT_BREAKAWAY_OK: ordinary and detached children inherit the job.
            // Only StartIndependent requests breakaway (updates and user-owned apps). This is lifetime management,
            // not a security sandbox: a child deliberately requesting breakaway can also leave the job.
            api.Configure(job, KillOnJobClose | BreakawayOk);
            api.AssignCurrentProcess(job);
            return job;
        }
        catch
        {
            api.Close(job);
            throw;
        }
    }

    /// <summary>Starts an updater or a user-owned external app that must survive this app. Never use for engines,
    /// terminals, brokers or their jobs. Removes the Unix mark and explicitly breaks away from our Windows job.
    /// Windows failures propagate; retrying as an ordinary child would silently kill the updater at app exit.</summary>
    public static Process? StartIndependent(ProcessStartInfo start)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (!start.UseShellExecute) start.Environment.Remove(OwnerVariable);
            return Process.Start(start);
        }
        if (start.RedirectStandardInput || start.RedirectStandardOutput || start.RedirectStandardError ||
            !string.IsNullOrEmpty(start.UserName))
            throw new NotSupportedException("Independent Windows launches do not support redirected streams or alternate credentials.");
        if (start.UseShellExecute || Path.GetExtension(start.FileName).Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(start.FileName).Equals(".bat", StringComparison.OrdinalIgnoreCase))
            start = WindowsShellHelper(start);
        start.Environment.Remove(OwnerVariable);
        var command = new StringBuilder(WindowsQuoteArgument(start.FileName));
        var arguments = WindowsArguments(start);
        if (arguments.Length != 0) command.Append(' ').Append(arguments);
        // A complete, sorted Unicode environment block; never inherit the app's ownership mark into its replacement.
        var environment = string.Join('\0', start.Environment.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
            .Where(e => e.Value is not null).Select(e => e.Key + "=" + e.Value)) + "\0\0";
        var block = Marshal.StringToHGlobalUni(environment);
        try
        {
            var info = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
            var flags = CreateSuspended | CreateUnicodeEnvironment;
            if (_windowsJob != 0) flags |= CreateBreakawayFromJob;
            if (start.CreateNoWindow) flags |= CreateNoWindow;
            if (!CreateProcessW(null, command, 0, 0, false, flags, block,
                    string.IsNullOrEmpty(start.WorkingDirectory) ? null : start.WorkingDirectory, ref info, out var child))
                throw NativeError("Could not start independent process");
            Process? process = null;
            try
            {
                // Open the managed handle before resuming: even a very short-lived updater has a stable identity.
                // The native process handle remains open throughout; no reused PID can be adopted here.
                process = Process.GetProcessById(unchecked((int)child.ProcessId));
                _ = process.SafeHandle;
                if (ResumeThread(child.Thread) == uint.MaxValue) throw NativeError("Could not resume independent process");
                return process;
            }
            catch
            {
                TerminateProcess(child.Process, 1); // only the exact child created above, never a PID lookup
                process?.Dispose();
                throw;
            }
            finally
            {
                CloseHandle(child.Thread);
                CloseHandle(child.Process);
            }
        }
        finally { Marshal.FreeHGlobal(block); }
    }

    // ShellExecute has no breakaway flag. Run it from a breakaway helper instead, never in the app's job. Encoded
    // PowerShell carries literal strings (no cmd.exe interpolation of paths with &, %, quotes or Unicode).
    internal static ProcessStartInfo WindowsShellHelper(ProcessStartInfo start)
    {
        static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        var script = "$ErrorActionPreference='Stop';$s=New-Object System.Diagnostics.ProcessStartInfo;" +
            "$s.UseShellExecute=$true;$s.FileName=" + Literal(start.FileName) + ";$s.Arguments=" + Literal(WindowsArguments(start)) +
            ";$s.WorkingDirectory=" + Literal(start.WorkingDirectory) + ";$s.Verb=" + Literal(start.Verb) +
            ";$p=[System.Diagnostics.Process]::Start($s);if($null -ne $p){$p.WaitForExit();exit $p.ExitCode}";
        var helper = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            helper.ArgumentList.Add(argument);
        helper.Environment.Clear();
        foreach (var variable in start.Environment) helper.Environment[variable.Key] = variable.Value;
        helper.Environment.Remove(OwnerVariable);
        return helper;
    }

    internal static string WindowsArguments(ProcessStartInfo start)
    {
        if (start.ArgumentList.Count == 0) return start.Arguments;
        if (start.Arguments.Length != 0) throw new InvalidOperationException("Use Arguments or ArgumentList, not both.");
        return string.Join(' ', start.ArgumentList.Select(WindowsQuoteArgument));
    }

    internal static string WindowsQuoteArgument(string argument)
    {
        var quoted = new StringBuilder().Append('"');
        var slashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\') { slashes++; continue; }
            quoted.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c);
            slashes = 0;
        }
        return quoted.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static Win32Exception NativeError(string operation) => new(Marshal.GetLastWin32Error(), operation);

    private sealed class WindowsJobApi : IWindowsJobApi
    {
        public nint Create()
        {
            var job = CreateJobObjectW(0, null);
            return job != 0 ? job : throw NativeError("Could not create owned-process Job Object");
        }

        public void Configure(nint job, uint limits)
        {
            var info = new ExtendedLimitInformation { Basic = new BasicLimitInformation { LimitFlags = limits } };
            if (!SetInformationJobObject(job, 9, ref info, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
                throw NativeError("Could not configure owned-process Job Object");
        }

        public void AssignCurrentProcess(nint job)
        {
            // Assignment occurs before any engine/terminal launch. A restrictive outer job may reject nesting;
            // report that failure rather than silently running an unowned tree.
            if (!AssignProcessToJobObject(job, GetCurrentProcess()))
                throw NativeError("Could not join owned-process Job Object; startup cannot continue safely");
        }

        public void Close(nint job) => CloseHandle(job);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public IoCounters Io;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedSize;
        public nint ReservedBytes, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(nint job, int informationClass, ref ExtendedLimitInformation information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string? application, StringBuilder command, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, nint environment, string? directory, ref StartupInfo startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(nint process, uint exitCode);
}
