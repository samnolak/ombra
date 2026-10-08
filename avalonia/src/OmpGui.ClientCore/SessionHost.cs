namespace OmpGui.ClientCore;

/// <summary>
/// The omp processes of one window: one <see cref="SessionController"/> per open chat, each its own omp that keeps
/// working while another chat is shown. omp holds a lock on the session file it writes, so a chat is never started in a
/// second process: <see cref="Find"/> returns the one that has it open. Opening one more than <see cref="MaxProcesses"/>
/// closes the chats used longest ago that may go (<c>canClose</c>: idle, nothing typed in it); a chat that is working
/// or waiting for the user never is, even when that leaves more processes than the limit. Used on the UI thread.
/// </summary>
public sealed class SessionHost : IAsyncDisposable
{
    public const int DefaultMaxProcesses = 4;

    /// <summary>The open chats, the one used longest ago first.</summary>
    private readonly List<SessionController> _open = [];
    private readonly Func<SessionController, bool> _canClose;
    private int _maxProcesses = DefaultMaxProcesses;

    /// <param name="first">The chat the window starts with (shown, not started yet).</param>
    /// <param name="canClose">Whether a chat that is not shown may be closed to stay under the limit; by default when
    /// its omp is <see cref="IsIdle"/>.</param>
    public SessionHost(SessionController first, Func<SessionController, bool>? canClose = null)
    {
        _open.Add(first);
        Active = first;
        _canClose = canClose ?? (c => IsIdle(c.Snapshot()));
    }

    /// <summary>How many omp processes may run at once before idle ones are closed (at least 1).</summary>
    public int MaxProcesses
    {
        get => _maxProcesses;
        set => _maxProcesses = Math.Max(1, value);
    }

    /// <summary>The chat the window shows.</summary>
    public SessionController Active { get; private set; }

    /// <summary>Every open chat, the one used longest ago first.</summary>
    public IReadOnlyList<SessionController> All => _open;

    /// <summary>Raised (on the caller's thread) when a chat leaves the host: closed by the user, or to stay under the limit.</summary>
    public event Action<SessionController>? Closed;

    /// <summary>The open chat whose omp has <paramref name="sessionFile"/> open, or was started to resume it and has not
    /// reported its file yet; null when none.</summary>
    public SessionController? Find(string sessionFile) =>
        _open.FirstOrDefault(c => (c.SessionFile ?? c.CurrentLaunch.ResumeSessionFile) is { } f && SessionCatalog.SamePath(f, sessionFile));

    /// <summary>
    /// A new chat, shown at once (the caller starts it): another omp launched like the others with
    /// <paramref name="request"/>. Chats over the limit that may go are closed (their omp stops in the background).
    /// </summary>
    public SessionController Open(LaunchRequest request)
    {
        var c = Active.Sibling(request);
        _open.Add(c);
        Active = c;
        Trim();
        return c;
    }

    /// <summary>Shows an open chat: it becomes the one used last.</summary>
    public void Activate(SessionController c)
    {
        if (!_open.Remove(c)) throw new InvalidOperationException("Not an open chat of this window.");
        _open.Add(c);
        Active = c;
    }

    /// <summary>Closes the chats used longest ago, while there are more than the limit and one of them may go.</summary>
    public void Trim()
    {
        var over = _open.Count - MaxProcesses;
        foreach (var c in _open.ToList())
        {
            if (over <= 0) return;
            if (c == Active || !_canClose(c)) continue;
            over--;
            _ = CloseAsync(c);
        }
    }

    /// <summary>Takes a chat that is not shown out of the host and stops its omp (stdin EOF, then the process tree).</summary>
    public Task CloseAsync(SessionController c)
    {
        if (c == Active) throw new InvalidOperationException("The chat shown cannot be closed; show another one first.");
        if (!_open.Remove(c)) return Task.CompletedTask;
        Closed?.Invoke(c);
        return c.DisposeAsync().AsTask();
    }

    /// <summary>Nothing would be lost by stopping this omp: not starting, running or stopping, and nothing waits on the user.</summary>
    public static bool IsIdle(SessionSnapshot s) =>
        s.Phase is SessionPhase.Ready or SessionPhase.Stopped or SessionPhase.Faulted
        && s.Dialogs.Count == 0 && s.Queued.Count == 0 && s.SigningIn is null;

    /// <summary>Stops every omp at once (each closes its stdin, then its process tree is killed after a grace period).</summary>
    public async ValueTask DisposeAsync()
    {
        var all = _open.ToList();
        await Task.WhenAll(all.Select(c => c.DisposeAsync().AsTask())).ConfigureAwait(false);
    }
}
