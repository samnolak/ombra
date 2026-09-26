using System.Diagnostics;
using System.Text;

namespace OmpGui.Rpc;

/// <summary>How to start omp in RPC mode. Either <c>omp(.exe)</c> directly, or a runtime such as Bun plus the omp CLI entry.</summary>
public sealed record OmpLaunchSpec
{
    public required string FileName { get; init; }
    /// <summary>Full argument list, including <c>--mode rpc</c>.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = ["--mode", "rpc"];
    public string? WorkingDirectory { get; init; }
    /// <summary>Variables to set (value) or remove (null) on top of the inherited environment.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();
}

public sealed class OmpStartException(string message, int? exitCode, string stderrTail, bool launchFailed = false) : Exception(message)
{
    public int? ExitCode { get; } = exitCode;
    public string StderrTail { get; } = stderrTail;
    /// <summary>The command itself could not be started (not found, not executable): omp never ran.</summary>
    public bool LaunchFailed { get; } = launchFailed;
}

/// <summary>
/// Owns one omp child process: stdio redirection, a continuous stderr drain and the RPC connection.
/// Shutdown follows the official contract: close stdin, omp drains and exits 0; kill the tree only on timeout.
/// Because omp exits on stdin EOF, it also ends when this GUI process dies and the OS closes the pipe.
/// </summary>
public sealed class OmpProcess : IAsyncDisposable
{
    private const int StderrTailLines = 200;
    private readonly Process _process;
    private readonly Queue<string> _stderrTail = new();
    private readonly Task _stderrPump;
    private int _stopped;

    public RpcConnection Connection { get; }
    public RpcReady Ready { get; private set; } = null!;
    public int ProcessId { get; }
    public Task<int> Exited { get; }
    /// <summary>Raised on the stderr pump thread for every stderr line.</summary>
    public event Action<string>? StderrLine;

    private OmpProcess(Process process, RpcConnectionOptions? options)
    {
        _process = process;
        ProcessId = process.Id;
        Connection = new RpcConnection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, options);
        _stderrPump = Task.Run(() => PumpStderrAsync(process.StandardError));
        Exited = WaitExitAsync(process);
    }

    public string StderrTail
    {
        get { lock (_stderrTail) return string.Join('\n', _stderrTail); }
    }

    /// <summary>Starts omp and waits for its <c>ready</c> frame (or its early exit, or the deadline).</summary>
    public static async Task<OmpProcess> StartAsync(OmpLaunchSpec spec, TimeSpan readyTimeout, RpcConnectionOptions? options = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(spec.FileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = spec.WorkingDirectory ?? Directory.GetCurrentDirectory(),
        };
        foreach (var a in spec.Arguments) psi.ArgumentList.Add(a);
        foreach (var (k, v) in spec.Environment)
        {
            if (v is null) psi.Environment.Remove(k);
            else psi.Environment[k] = v;
        }

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new OmpStartException($"could not start {spec.FileName}", null, "", launchFailed: true);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new OmpStartException($"could not start {spec.FileName}: {e.Message}", null, "", launchFailed: true);
        }

        var omp = new OmpProcess(process, options);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(readyTimeout);
        var ready = omp.Connection.WaitReadyAsync(deadline.Token);
        await Task.WhenAny(ready, omp.Exited).ConfigureAwait(false);
        if (ready.IsCompletedSuccessfully)
        {
            omp.Ready = ready.Result;
            return omp;
        }
        if (ct.IsCancellationRequested)
        {
            await omp.DisposeAsync().ConfigureAwait(false);
            throw new OperationCanceledException(ct);
        }
        if (ready.IsCanceled)
        {
            var tail = omp.StderrTail;
            await omp.DisposeAsync().ConfigureAwait(false);
            throw new OmpStartException($"omp did not become ready within {readyTimeout.TotalSeconds:0}s", null, tail);
        }
        // omp exited, or closed stdout, before ready: the exit code and stderr say why.
        int? code = null;
        try { code = await omp.Exited.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { }
        try { await omp._stderrPump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException) { }
        var stderr = omp.StderrTail;
        await omp.DisposeAsync().ConfigureAwait(false);
        throw new OmpStartException(code is { } c
            ? $"omp exited with code {c} before it was ready"
            : "omp closed its output before it was ready", code, stderr);
    }

    /// <summary>Graceful stop: close stdin and wait; kill the process tree when the grace period runs out.</summary>
    public async Task<int> StopAsync(TimeSpan grace)
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 0)
        {
            try { _process.StandardInput.Close(); }
            catch (Exception e) when (e is IOException or InvalidOperationException) { }
        }
        try
        {
            return await Exited.WaitAsync(grace).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Kill();
            return await Exited.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    public void Kill()
    {
        try { _process.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private static async Task<int> WaitExitAsync(Process p)
    {
        await p.WaitForExitAsync().ConfigureAwait(false);
        return p.ExitCode;
    }

    private async Task PumpStderrAsync(StreamReader stderr)
    {
        try
        {
            while (await stderr.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (_stderrTail)
                {
                    _stderrTail.Enqueue(line);
                    while (_stderrTail.Count > StderrTailLines) _stderrTail.Dequeue();
                }
                StderrLine?.Invoke(line);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (!_process.HasExited)
        {
            try { await StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
        await Connection.DisposeAsync().ConfigureAwait(false);
        await _stderrPump.WaitAsync(TimeSpan.FromSeconds(2)).ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
        _process.Dispose();
    }
}
