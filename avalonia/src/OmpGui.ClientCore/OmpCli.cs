using System.Diagnostics;
using System.Text;
using OmpGui.Rpc;

namespace OmpGui.ClientCore;

/// <summary>What an omp CLI command printed (<c>omp plugin list --json</c>, <c>omp config get … --json</c>).</summary>
public sealed record OmpCliResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>
/// Runs omp's own command-line subcommands with the same binary, profile and environment as the session (settings
/// and plugins live in omp's files; the CLI is omp's supported way to change them). Output is captured; the process
/// is killed on cancel or timeout.
/// </summary>
public static class OmpCli
{
    public static async Task<OmpCliResult> RunAsync(OmpLaunchSpec spec, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(spec.FileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (spec.WorkingDirectory is { } dir && Directory.Exists(dir)) psi.WorkingDirectory = dir;
        foreach (var a in spec.Arguments) psi.ArgumentList.Add(a);
        foreach (var (k, v) in spec.Environment)
        {
            if (v is null) psi.Environment.Remove(k);
            else psi.Environment[k] = v;
        }
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };
        try { process.Start(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new OmpCliResult(-1, "", $"could not start {spec.FileName}: {e.Message}");
        }
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or AggregateException or System.ComponentModel.Win32Exception) { }
            ct.ThrowIfCancellationRequested();
            lock (stdout) lock (stderr) return new OmpCliResult(-1, stdout.ToString(), stderr + "timed out");
        }
        process.WaitForExit(); // drains the redirected output
        lock (stdout) lock (stderr) return new OmpCliResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
