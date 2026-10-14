namespace Ombra.ClientCore;

/// <summary>
/// The omp processes of one window: one <see cref="SessionController"/> per open chat, each its own omp that keeps
/// working while another chat is shown. omp holds a lock on the session file it writes, so a chat is never started in a
/// second process: <see cref="Find"/> returns the one that has it open. Opening one more than <see cref="MaxProcesses"/>
/// closes the chats used longest ago that may go (<c>canClose</c>: idle, nothing typed in it); a chat that is working
/// or waiting for the user never is, even when that leaves more processes than the limit. Besides the open chats the
/// host may keep one <see cref="Spare"/>: an omp started ahead for the next new chat (an omp takes seconds to start; a
/// new chat that takes over a ready one can be used at once). Used on the UI thread.
/// </summary>
public sealed class SessionHost : IAsyncDisposable
{
    public const int DefaultMaxProcesses = 4;

    /// <summary>The open chats, the one used longest ago first.</summary>
    private readonly List<SessionController> _open = [];
    private readonly Func<SessionController, bool> _canClose;
    private int _maxProcesses = DefaultMaxProcesses;
    private int _launchGeneration;
    private int _spareGeneration;
    private CancellationTokenSource? _spareCancel;
    private Task _spareStart = Task.CompletedTask;

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

    /// <summary>Raised once the omp of a chat that left the host (or of a dropped spare) has stopped, on whichever thread
    /// that happened: what it left on disk can be looked at now without omp writing to it.</summary>
    public event Action<SessionController>? Stopped;

    /// <summary>The omp started ahead for the next new chat: not an open chat (never in <see cref="All"/>, never counted
    /// against <see cref="MaxProcesses"/>), nothing sent to it until a new chat takes it over.</summary>
    public SessionController? Spare { get; private set; }

    /// <summary>The open chat whose omp has <paramref name="sessionFile"/> open, or was started to resume it and has not
    /// reported its file yet; null when none.</summary>
    public SessionController? Find(string sessionFile) =>
        _open.FirstOrDefault(c => (c.SessionFile ?? c.CurrentLaunch.ResumeSessionFile) is { } f && SessionCatalog.SamePath(f, sessionFile));

    /// <summary>
    /// A new chat, shown at once (the caller starts it): another omp launched like the others with
    /// <paramref name="request"/>. Chats over the limit that may go are closed (their omp stops in the background).
    /// </summary>
    public SessionController Open(LaunchRequest request) => Add(Active.Sibling(request));

    /// <summary>
    /// Starts the <see cref="Spare"/> with <paramref name="request"/> (its own folder, the permission mode a new chat would
    /// get), unless there is one. The returned task ends when the spare is ready or failed to start; a spare that fails
    /// is dropped by the next <see cref="TakeSpare"/>, without a word: nobody asked for it.
    /// </summary>
    public Task PrepareSpareAsync(LaunchRequest request, CancellationToken ct)
    {
        if (Spare is not null) return _spareStart;
        var spare = Active.Sibling(request);
        Spare = spare;
        _spareGeneration = _launchGeneration;
        _spareCancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _spareCancel.Token;
        // Off the caller's thread: building omp's launch starts processes, and the caller is the UI's
        return _spareStart = Task.Run(() => StartSpareAsync(spare, token));
    }

    private static async Task StartSpareAsync(SessionController spare, CancellationToken ct)
    {
        try { await spare.StartAsync(ct).ConfigureAwait(false); }
        catch (Exception)
        {
            // Its snapshot says it did not start (or it was dropped meanwhile): TakeSpare drops it
        }
    }

    /// <summary>
    /// The spare, out of the host's hands, when it can be a new chat launched like <paramref name="request"/> (whose own
    /// folder does not matter): ready, idle, untouched, and started with the same launch since the last
    /// <see cref="InvalidateSpare"/>. Null otherwise; a spare that never could be (another permission mode, crashed,
    /// started before a change) is dropped, and one still starting the right way is kept for the next new chat.
    /// Shown with <see cref="Adopt"/>.
    /// </summary>
    public SessionController? TakeSpare(LaunchRequest request)
    {
        if (Spare is not { } spare) return null;
        var s = spare.Snapshot();
        var same = _spareGeneration == _launchGeneration && request with { WorkingDirectory = spare.CurrentLaunch.WorkingDirectory } == spare.CurrentLaunch;
        if (same && s.Phase is SessionPhase.NotStarted or SessionPhase.Starting) return null;
        if (!same || s.Phase != SessionPhase.Ready || !IsIdle(s) || s.Items.Any(i => i is not NoticeItem))
        {
            _ = DropSpareAsync();
            return null;
        }
        Spare = null;
        _spareCancel?.Dispose();
        _spareCancel = null;
        return spare;
    }

    /// <summary>Something that changes how omp starts changed (settings, runtime, network privacy): the spare, started
    /// the old way, stops, and one prepared from now on is started the new way.</summary>
    public void InvalidateSpare()
    {
        _launchGeneration++;
        _ = DropSpareAsync();
    }

    /// <summary>Stops the spare: a start under way is cancelled first, so no omp is left starting with nobody to stop it.</summary>
    private async Task DropSpareAsync()
    {
        if (Spare is not { } spare) return;
        Spare = null;
        var start = _spareStart;
        var cancel = _spareCancel;
        _spareCancel = null;
        cancel?.Cancel();
        await start.ConfigureAwait(false); // never throws (StartSpareAsync)
        cancel?.Dispose();
        await StopAsync(spare).ConfigureAwait(false);
    }

    /// <summary>A started omp (the spare <see cref="TakeSpare"/> returned) as a new chat, shown at once.</summary>
    public SessionController Adopt(SessionController started) => Add(started);

    private SessionController Add(SessionController c)
    {
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
        return StopAsync(c);
    }

    private async Task StopAsync(SessionController c)
    {
        await c.DisposeAsync().ConfigureAwait(false);
        Stopped?.Invoke(c);
    }

    /// <summary>Nothing would be lost by stopping this omp: not starting, running or stopping, and nothing waits on the user.</summary>
    public static bool IsIdle(SessionSnapshot s) =>
        s.Phase is SessionPhase.Ready or SessionPhase.Stopped or SessionPhase.Faulted
        && s.Dialogs.Count == 0 && s.Queued.Count == 0 && s.SigningIn is null;

    /// <summary>Stops every omp at once, the spare's too (each closes its stdin, then its process tree is killed after a
    /// grace period).</summary>
    public async ValueTask DisposeAsync()
    {
        var all = _open.Select(StopAsync).Append(DropSpareAsync()).ToList();
        await Task.WhenAll(all).ConfigureAwait(false);
    }
}
