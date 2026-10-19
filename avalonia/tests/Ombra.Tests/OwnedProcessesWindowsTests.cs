using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Ombra.ClientCore;

namespace Ombra.Tests;

public sealed class OwnedProcessesWindowsTests
{
    private sealed class JobApi(string? failAt = null) : OwnedProcesses.IWindowsJobApi
    {
        public List<string> Calls { get; } = [];
        public uint Limits { get; private set; }
        private void Call(string operation)
        {
            Calls.Add(operation);
            if (operation == failAt) throw new Win32Exception(5, operation);
        }
        public nint Create() { Call("create"); return 123; }
        public void Configure(nint job, uint limits) { Assert.Equal((nint)123, job); Limits = limits; Call("configure"); }
        public void AssignCurrentProcess(nint job) { Assert.Equal((nint)123, job); Call("assign-current"); }
        public void Close(nint job) { Assert.Equal((nint)123, job); Call("close"); }
    }

    [Fact]
    public void Job_is_configured_before_the_app_joins_and_its_handle_is_kept_until_process_exit()
    {
        var api = new JobApi();
        Assert.Equal((nint)123, OwnedProcesses.AttachWindowsJob(api));
        Assert.Equal(["create", "configure", "assign-current"], api.Calls);
        Assert.Equal(0x2000u | 0x0800u, api.Limits); // KILL_ON_JOB_CLOSE + explicit BREAKAWAY_OK, never SILENT_BREAKAWAY_OK
    }

    [Theory]
    [InlineData("create", "create")]
    [InlineData("configure", "create,configure,close")]
    [InlineData("assign-current", "create,configure,assign-current,close")]
    public void Native_ownership_failures_abort_instead_of_falling_back_to_unprotected_children(string failure, string calls)
    {
        var api = new JobApi(failure);
        var error = Assert.Throws<Win32Exception>(() => OwnedProcesses.AttachWindowsJob(api));
        Assert.Equal(5, error.NativeErrorCode);
        Assert.Equal(calls, string.Join(',', api.Calls));
    }

    [Theory]
    [InlineData("", "\"\"")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("a\\b", "\"a\\b\"")]
    [InlineData("ends\\", "\"ends\\\\\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("a\\\"b", "\"a\\\\\\\"b\"")]
    [InlineData("Юникод&%PATH%'", "\"Юникод&%PATH%'\"")]
    public void Independent_native_command_lines_preserve_arguments(string value, string quoted) =>
        Assert.Equal(quoted, OwnedProcesses.WindowsQuoteArgument(value));

    [Fact]
    public void Shell_associations_run_from_an_independent_helper_with_literal_not_interpolated_arguments()
    {
        var start = new ProcessStartInfo("C:\\my files\\it's & %PATH%.txt") { UseShellExecute = true, WorkingDirectory = "C:\\my files" };
        start.Environment[OwnedProcesses.OwnerVariable] = "123";
        start.ArgumentList.Add("a'\"b&c");
        var helper = OwnedProcesses.WindowsShellHelper(start);
        Assert.False(helper.UseShellExecute);
        Assert.True(helper.CreateNoWindow);
        Assert.DoesNotContain(OwnedProcesses.OwnerVariable, helper.Environment.Keys);
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(helper.ArgumentList[^1]));
        Assert.Contains("$s.FileName='C:\\my files\\it''s & %PATH%.txt'", script);
        Assert.Contains("$s.Arguments='" + OwnedProcesses.WindowsArguments(start).Replace("'", "''", StringComparison.Ordinal) + "'", script);
        Assert.Contains("$s.UseShellExecute=$true", script);
        Assert.Contains("$p.WaitForExit();exit $p.ExitCode", script);
    }

    [Fact]
    public void Conflicting_argument_forms_are_rejected_not_silently_dropped()
    {
        var start = new ProcessStartInfo("app", "raw");
        start.ArgumentList.Add("list");
        Assert.Throws<InvalidOperationException>(() => OwnedProcesses.WindowsArguments(start));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task Windows_job_ends_descendants_but_not_updater_or_foreign_processes(bool crash, bool shell) =>
        NativeLifetime(crash, shell ? "windows-owned-shell" : "windows-owned-owner");

    [Fact]
    public async Task Windows_job_ends_actual_Bun_detached_children_after_the_broker_has_exited()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Native Windows Job Object proof requires Windows");
        Assert.SkipUnless(Environment.GetEnvironmentVariable("OMBRA_TEST_BUN") is { Length: > 0 } bun && File.Exists(bun),
            "Set OMBRA_TEST_BUN to the installed runtime's bun.exe for the real detached-worker proof");
        await NativeLifetime(crash: true, "windows-owned-bun");
    }

    private static async Task NativeLifetime(bool crash, string mode)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Native Windows Job Object proof requires Windows");
        var root = TestProcesses.TempDir("windows-owned-processes");
        var foreignSpec = TestProcesses.Fake("windows-owned-leaf");
        var foreignStart = new ProcessStartInfo(foreignSpec.FileName) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in foreignSpec.Arguments.Concat(foreignSpec.SessionArguments)) foreignStart.ArgumentList.Add(arg);
        foreignStart.ArgumentList.Add(Path.Combine(root, "foreign.json"));
        using var foreign = Process.Start(foreignStart)!;
        _ = foreign.SafeHandle;
        var descendants = new List<Process>();
        Process? independent = null;
        await using var owner = JournalOwnershipTests.Start(mode, root);
        try
        {
            using var ready = JsonDocument.Parse(await owner.Line());
            foreach (var name in new[] { "direct", "grandchild", "bun" })
            {
                if (ready.RootElement.GetProperty(name).ValueKind == JsonValueKind.Null) continue;
                var process = Process.GetProcessById(ready.RootElement.GetProperty(name).GetInt32());
                _ = process.SafeHandle; // retain exact OS identities before the crash, not reusable PID guesses
                descendants.Add(process);
                Assert.False(process.HasExited);
            }
            independent = Process.GetProcessById(ready.RootElement.GetProperty("independent").GetInt32());
            _ = independent.SafeHandle;
            using var independentInfo = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "independent.json")));
            Assert.Equal(JsonValueKind.Null, independentInfo.RootElement.GetProperty("owner").ValueKind);
            Assert.Equal(new[] { "", "spaces and Юникод", "trailing\\", "embedded\"quote", "a&b%PATH%'" },
                independentInfo.RootElement.GetProperty("arguments").EnumerateArray().Select(a => a.GetString()));
            using var directInfo = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "direct.json")));
            Assert.Equal(owner.Process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), directInfo.RootElement.GetProperty("owner").GetString());
            if (crash) owner.Process.Kill(entireProcessTree: false); // ONLY the owner: the kernel job must do the rest
            else owner.Process.StandardInput.Close();
            await owner.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            foreach (var descendant in descendants)
                await descendant.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(independent.HasExited);
            Assert.False(foreign.HasExited);
        }
        finally
        {
            // All cleanup uses handles retained while the exact processes were alive. Never search by a port or
            // enumerate matching names/PIDs; neither an unrelated process nor a reused PID is eligible for cleanup.
            foreach (var process in descendants.Append(independent).Append(foreign).OfType<Process>())
            {
                if (!process.HasExited) process.Kill(entireProcessTree: false);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                if (process != foreign) process.Dispose();
            }
        }
    }
}
