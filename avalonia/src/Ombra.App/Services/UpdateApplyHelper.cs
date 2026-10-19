using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Ombra.ClientCore;

namespace Ombra.App.Services;

// This is the installed app running from a private copy, not from either directory it renames.
// The copy includes the apphost, managed assemblies, runtime and native dependencies.
internal static class UpdateApplyHelper
{
    internal const string ReadyMarker = ".ombra-update-ready";
    private const string HelperMarker = ".ombra-update-helper";
    private const string CompletedMarker = ".completed";
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal sealed record Request(string Token, string Helper, int Pid, long? ParentStartTicks,
        string Target, string NewRoot, string Staging, string Previous, string State, string Executable,
        string From, string To, bool Relaunch, string[] Args);

    internal sealed class Operations
    {
        internal Action<string, string> Move { get; init; } = Directory.Move;
        internal Action<string> Delete { get; init; } = path => Directory.Delete(path, true);
        internal Func<TimeSpan, Task> Delay { get; init; } = delay => Task.Delay(delay);
        internal Func<long> Timestamp { get; init; } = Stopwatch.GetTimestamp;
        internal long Frequency { get; init; } = Stopwatch.Frequency;
        internal Func<int, long?, Task<bool>> WaitForParent { get; init; } = WaitForParentAsync;
        internal Action<ProcessStartInfo> Launch { get; init; } = psi => OwnedProcesses.StartIndependent(psi)?.Dispose();
    }

    internal static string Prepare(UpdateInstaller installer, StagedUpdate staged, string from, int pid, bool relaunch,
        IReadOnlyList<string> args, Action<ProcessStartInfo> launch)
    {
        var state = Path.GetFullPath(installer.StateDirectory);
        var helper = Path.Combine(state, "apply-helper-" + Guid.NewGuid().ToString("N"));
        long? parentStart = null;
        try { using var parent = Process.GetProcessById(pid); parentStart = parent.StartTime.ToUniversalTime().Ticks; }
        catch (ArgumentException) { }
        var request = new Request(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), helper, pid, parentStart,
            installer.InstallRoot, Path.GetFullPath(staged.NewRoot), Path.GetFullPath(staged.Staging), installer.PreviousDirectory,
            state, installer.Executable, from, staged.Version, relaunch, args.ToArray());
        Validate(request, requireHelper: false);
        Directory.CreateDirectory(state);
        Directory.CreateDirectory(helper);
        try
        {
            File.WriteAllText(Path.Combine(helper, HelperMarker), request.Token);
            CopyTree(request.Target, Path.Combine(helper, "app"));
            var file = Path.Combine(helper, "request.json");
            File.WriteAllText(file, JsonSerializer.Serialize(request));
            var start = new ProcessStartInfo(Path.Combine(helper, "app", request.Executable))
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = helper,
            };
            start.ArgumentList.Add("--apply-update");
            start.ArgumentList.Add(file);
            start.ArgumentList.Add(request.Token);
            start.Environment.Remove(OwnedProcesses.OwnerVariable);
            File.Delete(Path.Combine(state, "last-update.json"));
            launch(start);
            return file;
        }
        catch
        {
            TryCleanup(helper);
            throw;
        }
    }

    // Strict dispatch: no UI/config/update switches may be combined with this private command.
    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length != 3 || args[0] != "--apply-update")
                throw new InvalidDataException("--apply-update requires exactly a request file and its token");
            var file = FullPath(args[1]);
            NoLinks(file);
            var request = JsonSerializer.Deserialize<Request>(await File.ReadAllTextAsync(file).ConfigureAwait(false))
                ?? throw new InvalidDataException("empty update request");
            if (string.IsNullOrEmpty(request.Token) || request.Token.Length != 64 || request.Token != args[2]
                || !Same(file, Path.Combine(request.Helper, "request.json"))
                || Environment.ProcessPath is not { } self || !Same(self, Path.Combine(request.Helper, "app", request.Executable)))
                throw new InvalidDataException("update request does not belong to this helper");
            Validate(request, requireHelper: true, requirePayload: false);
            NoLinks(Path.Combine(request.Helper, ".running"));
            NoLinks(Path.Combine(request.Helper, CompletedMarker));
            using var lease = new FileStream(Path.Combine(request.Helper, ".running"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            // Consume the request before waiting: a helper cannot replay a previously admitted request.
            File.Delete(file);
            try { return await ApplyAsync(request, new Operations()).ConfigureAwait(false); }
            finally { File.WriteAllText(Path.Combine(request.Helper, CompletedMarker), request.Token); }
        }
        catch (Exception e) when (IsExpected(e))
        {
            Console.Error.WriteLine("Update helper refused or failed: " + e.Message);
            return 1;
        }
    }

    internal static async Task<int> ApplyAsync(Request request, Operations operations)
    {
        Validate(request, requireHelper: true, requirePayload: false);
        void Log(string message) => File.AppendAllText(Path.Combine(request.State, "apply.log"), $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
        void Result(bool ok, string? error)
        {
            var file = Path.Combine(request.State, "last-update.json");
            var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(new UpdateResult(ok, request.From, request.To, error, request.Previous)));
                File.Move(temp, file, overwrite: true);
            }
            finally { File.Delete(temp); }
        }
        var movedOld = false;
        var installed = false;
        try
        {
            Log($"updating {request.From} -> {request.To}: waiting for Ombra (pid {request.Pid}) to quit");
            if (!await operations.WaitForParent(request.Pid, request.ParentStartTicks).ConfigureAwait(false))
                throw new IOException("Ombra did not quit within 2 minutes; nothing was changed");
            Validate(request, requireHelper: true); // recheck after the parent wait, before any destructive operation
            if (Directory.Exists(request.Previous))
                await RetryAsync(() => operations.Delete(request.Previous), operations).ConfigureAwait(false);
            await RetryAsync(() => operations.Move(request.Target, request.Previous), operations).ConfigureAwait(false);
            movedOld = true;
            await RetryAsync(() => operations.Move(request.NewRoot, request.Target), operations).ConfigureAwait(false);
            installed = true;
        }
        catch (Exception e) when (IsExpected(e))
        {
            var error = e.Message;
            if (movedOld && !installed)
            {
                try
                {
                    await RetryAsync(() => operations.Move(request.Previous, request.Target), operations).ConfigureAwait(false);
                    error = "could not move the new version in; the current version was put back: " + error;
                }
                catch (Exception rollback) when (IsExpected(rollback))
                {
                    error = $"could not move the new version in; rollback failed: {rollback.Message}; the previous version remains at {request.Previous}. Original failure: {error}";
                }
            }
            Log("failed: " + error);
            Result(false, error);
            return 1;
        }
        // The swap is committed. Cleanup/relaunch failures must not claim that the old app was restored.
        try { operations.Delete(request.Staging); }
        catch (Exception e) when (IsExpected(e)) { Log("staging cleanup deferred: " + e.Message); }
        Result(true, null);
        Log($"updated to {request.To}; the previous version is in {request.Previous}");
        if (request.Relaunch)
        {
            var start = new ProcessStartInfo(Path.Combine(request.Target, request.Executable)) { UseShellExecute = false, WorkingDirectory = request.Target };
            foreach (var arg in request.Args) start.ArgumentList.Add(arg);
            start.Environment.Remove(OwnedProcesses.OwnerVariable);
            try { operations.Launch(start); }
            catch (Exception e) when (IsExpected(e))
            {
                var error = "update installed, but restart failed: " + e.Message;
                Log(error);
                Result(true, error);
                return 1;
            }
        }
        return 0;
    }

    internal static async Task RetryAsync(Action action, Operations operations)
    {
        var start = operations.Timestamp();
        while (true)
        {
            try { action(); return; }
            catch (Exception e) when (IsTransient(e))
            {
                var remaining = TimeSpan.FromSeconds(60 - (operations.Timestamp() - start) / (double)operations.Frequency);
                if (remaining <= TimeSpan.Zero) throw;
                await operations.Delay(remaining < TimeSpan.FromMilliseconds(250) ? remaining : TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(Exception e) => e is UnauthorizedAccessException
        || e is IOException && (e.HResult & 0xffff) is 5 or 32 or 33;

    private static bool IsExpected(Exception e) => e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
        or ArgumentException or InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception;

    private static async Task<bool> WaitForParentAsync(int pid, long? startTicks)
    {
        if (startTicks is null) return true; // parent was already gone at preparation; never wait on a reused PID
        try
        {
            using var parent = Process.GetProcessById(pid);
            if (parent.StartTime.ToUniversalTime().Ticks != startTicks) return true;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try { await parent.WaitForExitAsync(timeout.Token).ConfigureAwait(false); return true; }
            catch (OperationCanceledException) { return false; }
        }
        catch (ArgumentException) { return true; }
    }

    internal static void Validate(Request r, bool requireHelper, bool requirePayload = true)
    {
        if (r.Pid <= 0 || r.Pid == Environment.ProcessId && requireHelper || r.Args is null || r.Args.Any(a => a is null || a.Contains('\0'))
            || string.IsNullOrWhiteSpace(r.Executable) || Path.IsPathRooted(r.Executable)
            || r.Executable.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is ".." or "." or ""))
            throw new InvalidDataException("invalid update process, executable or arguments");
        foreach (var path in new[] { r.Target, r.NewRoot, r.Staging, r.Previous, r.State, r.Helper })
        {
            FullPath(path);
            NoLinks(path);
        }
        var parent = Path.GetDirectoryName(r.Target) ?? throw new InvalidDataException("cannot update a filesystem root");
        if (!Same(r.Previous, Path.Combine(parent, "." + Path.GetFileName(r.Target) + ".previous"))
            || !Same(Path.GetDirectoryName(r.Staging)!, parent)
            || !Path.GetFileName(r.Staging).StartsWith("." + Path.GetFileName(r.Target) + ".update-", PathComparison)
            || !Inside(r.NewRoot, r.Staging) || !Inside(r.Helper, r.State)
            || !Same(Path.GetDirectoryName(r.Helper)!, r.State) || !Path.GetFileName(r.Helper).StartsWith("apply-helper-", StringComparison.Ordinal)
            || new[] { r.Target, r.Staging, r.Previous }.Any(p => Overlap(p, r.State)))
            throw new InvalidDataException("unsafe update directory layout");
        NoLinks(Path.Combine(r.State, "apply.log"));
        NoLinks(Path.Combine(r.State, "last-update.json"));
        NoLinks(Path.Combine(r.Helper, HelperMarker));
        if (requireHelper && File.ReadAllText(Path.Combine(r.Helper, HelperMarker)) != r.Token)
            throw new InvalidDataException("unowned update helper directory");
        if (!requirePayload) return;
        if (!File.Exists(Path.Combine(r.Staging, ".ombra-update-staging"))
            || !File.Exists(Path.Combine(r.Target, r.Executable)) || !File.Exists(Path.Combine(r.NewRoot, r.Executable)))
            throw new InvalidDataException("the installed or prepared app is missing");
        NoLinks(Path.Combine(r.Target, r.Executable));
        NoLinks(Path.Combine(r.NewRoot, r.Executable));
        var receipt = JsonSerializer.Deserialize<StagedUpdate>(File.ReadAllText(Path.Combine(r.Staging, ReadyMarker)));
        if (receipt is null || receipt.Version != r.To || !Same(receipt.Staging, r.Staging) || !Same(receipt.NewRoot, r.NewRoot))
            throw new InvalidDataException("the prepared update does not match its admission receipt");
        if (File.Exists(r.Previous) || Directory.Exists(r.Previous) && !File.Exists(Path.Combine(r.Previous, r.Executable)))
            throw new IOException($"{r.Previous} is in the way and is not an earlier Ombra; move it elsewhere");
        if (Directory.Exists(r.Previous)) CheckTree(r.Previous);
        CheckTree(r.Staging);
    }

    private static string FullPath(string path)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path) || !Same(path, Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))))
            throw new InvalidDataException("update paths must be canonical absolute paths");
        return path;
    }
    private static bool Same(string a, string b) => string.Equals(a, b, PathComparison);
    private static bool Inside(string path, string parent) => path.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, PathComparison);
    private static bool Overlap(string a, string b) => Same(a, b) || Inside(a, b) || Inside(b, a);
    private static void NoLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("update paths must not traverse symbolic links or junctions: " + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    private static void CheckTree(string path)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("update trees must not contain symbolic links or junctions");
            if (Directory.Exists(entry)) CheckTree(entry);
        }
    }
    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("helper source must not contain symbolic links or junctions");
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if (Directory.Exists(entry)) CopyTree(entry, target);
            else File.Copy(entry, target, overwrite: false);
        }
    }

    internal static void CleanupCompleted(string state)
    {
        try
        {
            if (!Directory.Exists(state)) return;
            NoLinks(Path.GetFullPath(state));
            foreach (var helper in Directory.EnumerateDirectories(state, "apply-helper-*"))
            {
                if (!File.Exists(Path.Combine(helper, HelperMarker)) || !File.Exists(Path.Combine(helper, CompletedMarker))) continue;
                try
                {
                    NoLinks(helper);
                    NoLinks(Path.Combine(helper, HelperMarker));
                    NoLinks(Path.Combine(helper, CompletedMarker));
                    var token = File.ReadAllText(Path.Combine(helper, HelperMarker));
                    if (token.Length != 64 || File.ReadAllText(Path.Combine(helper, CompletedMarker)) != token) continue;
                    NoLinks(Path.Combine(helper, ".running"));
                    using (new FileStream(Path.Combine(helper, ".running"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) { }
                    TryCleanup(helper);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (InvalidDataException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (InvalidDataException) { }
    }

    private static void TryCleanup(string helper)
    {
        try
        {
            NoLinks(helper);
            CheckTree(helper);
            // Keep ownership/completion markers until all locked runtime files are gone, so a later start retries.
            foreach (var entry in Directory.EnumerateFileSystemEntries(helper))
            {
                if (Path.GetFileName(entry) is HelperMarker or CompletedMarker) continue;
                if (Directory.Exists(entry)) Directory.Delete(entry, true); else File.Delete(entry);
            }
            File.Delete(Path.Combine(helper, CompletedMarker));
            File.Delete(Path.Combine(helper, HelperMarker));
            Directory.Delete(helper);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (InvalidDataException) { }
    }
}
