using System.Diagnostics;

namespace Ombra.App.Nightly;

/// <summary>Where the runtime is, as the pane shows it.</summary>
internal enum NightlyState
{
    /// <summary>Not running and not wanted (no pane, or stopped on purpose).</summary>
    Stopped,
    /// <summary>The process is starting and has not connected yet.</summary>
    Starting,
    /// <summary>Connected and authenticated.</summary>
    Running,
    /// <summary>It exited or failed to connect; another start is scheduled.</summary>
    Restarting,
    /// <summary>Too many failures in a row (or it cannot start at all): waits for <see cref="NightlySupervisor.Retry"/>.</summary>
    Failed,
}

/// <summary>
/// Keeps one runtime process alive: starts it, notices when it exits or never becomes ready, starts it again after a
/// growing delay, gives up after <see cref="MaxAttempts"/> failures in a row, and on <see cref="Stop()"/> ends the process
/// with all its descendants (Nightly's content, GPU and utility processes), so nothing is left running. A stop on
/// purpose may first ask the process to quit by itself (<see cref="Stop(Func{bool}, TimeSpan)"/>: Nightly writes its
/// session and releases its profile), and a start waits until the previous process is gone (one profile, one process).
/// Every member runs through <c>post</c> (the UI thread in the app): exits and timers are posted there too.
/// </summary>
internal sealed class NightlySupervisor : IDisposable
{
    public const int MaxAttempts = 5;
    /// <summary>Running this long resets the failure count: a crash after it is a first failure again.</summary>
    public static readonly TimeSpan HealthyAfter = TimeSpan.FromSeconds(30);

    private readonly Func<Process> _start;
    private readonly Action<Action> _post;
    private readonly TimeSpan _readyTimeout;
    private readonly Func<int, TimeSpan> _delay;
    private Process? _process;
    private Timer? _timer;
    private int _generation;
    private DateTime _readyAt;
    private bool _disposed;
    /// <summary>Descendants seen while the process ran (pid → executable): a crashed parent's helpers are re-parented to
    /// launchd, where only this record finds them.</summary>
    private readonly Dictionary<int, string?> _descendants = [];
    private Timer? _watch;
    /// <summary>The previous process's ending (a graceful stop waits for it to quit): the next start waits for it.</summary>
    private Task _ending = Task.CompletedTask;

    /// <param name="start">Starts the process (throws when it cannot: missing binary, bad profile).</param>
    /// <param name="post">Runs an action on the owner's thread, in order.</param>
    /// <param name="readyTimeout">How long a start may take to <see cref="MarkReady"/>.</param>
    /// <param name="delay">The wait before restart attempt n (1-based); default 0.5 s doubling to 8 s.</param>
    public NightlySupervisor(Func<Process> start, Action<Action> post, TimeSpan readyTimeout, Func<int, TimeSpan>? delay = null)
    {
        _start = start;
        _post = post;
        _readyTimeout = readyTimeout;
        _delay = delay ?? (n => TimeSpan.FromMilliseconds(Math.Min(8000, 500 * (1 << Math.Min(4, n - 1)))));
    }

    public NightlyState State { get; private set; } = NightlyState.Stopped;
    /// <summary>Why it is restarting or failed (shown to the user); null otherwise.</summary>
    public string? Detail { get; private set; }
    /// <summary>Failures in a row (the restart being waited for is this number).</summary>
    public int Failures { get; private set; }
    /// <summary>The running process's id (0 when none).</summary>
    public int Pid { get; private set; }
    /// <summary>Processes started so far (restarts included).</summary>
    public int Starts { get; private set; }

    public event Action? StateChanged;
    /// <summary>A new process started (its pid in <see cref="Pid"/>): whatever belonged to the previous one is void.</summary>
    public event Action<Process>? ProcessStarted;

    /// <summary>Starts unless already started (any state but <see cref="NightlyState.Stopped"/> and <see cref="NightlyState.Failed"/>).
    /// While the previous process is still quitting it is <see cref="NightlyState.Starting"/> and launches once that one is gone.</summary>
    public void Start()
    {
        if (_disposed || State is not (NightlyState.Stopped or NightlyState.Failed)) return;
        Failures = 0;
        if (!_ending.IsCompleted)
        {
            var generation = ++_generation;
            Detail = null;
            Set(NightlyState.Starting);
            _ending.ContinueWith(_ => _post(() =>
            {
                if (generation == _generation && _process is null && State == NightlyState.Starting) Launch();
            }), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return;
        }
        Launch();
    }

    /// <summary>After <see cref="NightlyState.Failed"/>: tries again from a clean count.</summary>
    public void Retry()
    {
        if (State == NightlyState.Failed) Start();
    }

    /// <summary>The process connected: it is running.</summary>
    public void MarkReady()
    {
        if (_process is null || State != NightlyState.Starting) return;
        CancelTimer();
        _readyAt = DateTime.UtcNow;
        Detail = null;
        Set(NightlyState.Running);
    }

    /// <summary>The running process is unusable (it refused the handshake, stopped answering): ends it and restarts.</summary>
    public void Fail(string reason)
    {
        if (_process is null) return;
        End(_process);
        OnFailure(reason);
    }

    /// <summary>Ends the process and its descendants at once; nothing restarts until <see cref="Start"/>.</summary>
    public void Stop() => Stop(null, TimeSpan.Zero);

    /// <summary>
    /// Stops on purpose: <paramref name="requestQuit"/> asks the process to quit by itself (false: it could not be
    /// asked), and only what is still running after <paramref name="grace"/> is killed, with every descendant it left.
    /// The state is <see cref="NightlyState.Stopped"/> at once; the task completes when nothing of the process runs.
    /// </summary>
    public Task Stop(Func<bool>? requestQuit, TimeSpan grace)
    {
        CancelTimer();
        _generation++;
        var process = _process;
        // Asked before the state changes: the owner forgets its connection on Stopped.
        var asked = process is not null && !process.HasExited && grace > TimeSpan.Zero && requestQuit?.Invoke() == true;
        if (process is not null) _ending = asked ? EndAfter(process, grace) : End(process);
        _process = null;
        Pid = 0;
        Detail = null;
        Set(NightlyState.Stopped);
        return _ending;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
    }

    private void Launch()
    {
        CancelTimer();
        var generation = ++_generation;
        Process process;
        try
        {
            process = _start();
        }
        catch (Exception e)
        {
            // Nothing to wait for: the binary or the profile is unusable, a restart would fail the same way.
            _process = null;
            Pid = 0;
            Failures = MaxAttempts;
            Detail = e.Message;
            Set(NightlyState.Failed);
            return;
        }
        _process = process;
        Pid = process.Id;
        Starts++;
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => _post(() => OnExited(generation, process));
        _watch = new Timer(_ => _post(() =>
        {
            if (generation == _generation && _process == process) Remember(process.Id);
        }), null, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1));
        Set(NightlyState.Starting);
        ProcessStarted?.Invoke(process);
        _timer = new Timer(_ => _post(() =>
        {
            if (generation == _generation && State == NightlyState.Starting)
                Fail(Localization.Loc.F("Nightly did not connect within {0} seconds.", (int)_readyTimeout.TotalSeconds));
        }), null, _readyTimeout, Timeout.InfiniteTimeSpan);
        // It may have exited before the handler was attached.
        if (process.HasExited) _post(() => OnExited(generation, process));
    }

    private void OnExited(int generation, Process process)
    {
        if (generation != _generation || _process != process) return; // stopped or replaced on purpose
        var code = process.ExitCode;
        End(process); // its helpers, if any outlived it
        OnFailure(Localization.Loc.F("Nightly exited unexpectedly (code {0}).", code));
    }

    private void OnFailure(string reason)
    {
        CancelTimer();
        var generation = ++_generation;
        if (State == NightlyState.Running && DateTime.UtcNow - _readyAt >= HealthyAfter) Failures = 0;
        _process = null;
        Pid = 0;
        Failures++;
        Detail = reason;
        if (Failures >= MaxAttempts)
        {
            Set(NightlyState.Failed);
            return;
        }
        Set(NightlyState.Restarting);
        _timer = new Timer(_ => _post(() =>
        {
            if (generation == _generation && State == NightlyState.Restarting) Launch();
        }), null, _delay(Failures), Timeout.InfiniteTimeSpan);
    }

    private void Set(NightlyState state)
    {
        State = state;
        StateChanged?.Invoke();
    }

    private void CancelTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>Descendants seen so far (tests).</summary>
    internal IReadOnlyCollection<int> KnownDescendants => _descendants.Keys;

    private void Remember(int pid)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var queue = new Queue<int>(NightlyMach.Children(pid));
        while (queue.Count > 0 && _descendants.Count < 256)
        {
            var p = queue.Dequeue();
            if (!_descendants.ContainsKey(p)) _descendants[p] = NightlyMach.ExecutableOf(p);
            foreach (var child in NightlyMach.Children(p)) queue.Enqueue(child);
        }
    }

    /// <summary>The process with its live tree, and the descendants it left behind (now launchd's, same executable).</summary>
    private Task End(Process process)
    {
        if (Detach(process) is not { } ending) return Task.CompletedTask;
        Finish(ending.Pid, ending.Descendants);
        return Task.CompletedTask;
    }

    /// <summary>Waits up to <paramref name="grace"/> for the process to quit by itself, then ends what is left (off the owner's thread).</summary>
    private Task EndAfter(Process process, TimeSpan grace)
    {
        if (Detach(process) is not { } ending) return Task.CompletedTask;
        return Task.Run(async () =>
        {
            using var timeout = new CancellationTokenSource(grace);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Services.ClientLog.Shared.Write("nightly", $"pid {ending.Pid} did not quit within {grace.TotalSeconds:0} s; ending it");
            }
            catch (InvalidOperationException)
            {
                // not ours to wait for any more: ended below
            }
            // Quit by itself: its pid may already be someone else's, so only what it left behind is ended.
            Finish(ending.Pid, ending.Descendants, alive: !process.HasExited);
        });
    }

    /// <summary>Stops watching the process: its pid and every descendant seen so far, or null when it has no id.</summary>
    private (int Pid, Dictionary<int, string?> Descendants)? Detach(Process process)
    {
        _watch?.Dispose();
        _watch = null;
        int pid;
        try { pid = process.Id; }
        catch (InvalidOperationException) { return null; }
        if (!process.HasExited) Remember(pid);
        var descendants = new Dictionary<int, string?>(_descendants);
        _descendants.Clear();
        return (pid, descendants);
    }

    /// <summary>Kills what is left of <paramref name="pid"/>'s tree (when it is still <paramref name="alive"/>) and the
    /// descendants it left to launchd (same executable).</summary>
    private static void Finish(int pid, Dictionary<int, string?> descendants, bool alive = true)
    {
        if (alive) KillTree(pid);
        if (!OperatingSystem.IsMacOS() || descendants.Count == 0) return;
        var adopted = NightlyMach.Children(1).ToHashSet();
        foreach (var (p, path) in descendants)
            if (adopted.Contains(p) && path is not null && NightlyMach.ExecutableOf(p) == path) NightlyMach.Signal(p, 9);
    }

    internal static void KillTree(int pid)
    {
        if (!OperatingSystem.IsMacOS())
        {
            try { Process.GetProcessById(pid).Kill(entireProcessTree: true); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return;
        }
        var tree = new List<int>();
        var queue = new Queue<int>([pid]);
        while (queue.Count > 0 && tree.Count < 256)
        {
            var p = queue.Dequeue();
            tree.Add(p);
            foreach (var child in NightlyMach.Children(p)) queue.Enqueue(child);
        }
        const int SigKill = 9;
        foreach (var p in tree) NightlyMach.Signal(p, SigKill);
    }
}
