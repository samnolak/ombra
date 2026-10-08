namespace OmpGui.ClientCore.Browser;

/// <summary>What a web page (or the engine on its behalf) asks: the kind of card the browser pane shows.</summary>
public enum BrowserDialogKind
{
    Credentials,
    Alert,
    Confirm,
    Prompt,
    BeforeUnload,
    FileChooser,
    Download,
    Permission,
    PopupNotice,
}

/// <summary>What a page asks permission for (camera and microphone, location, …).</summary>
public enum PermissionKind
{
    Camera,
    Microphone,
    CameraAndMicrophone,
    Geolocation,
    Notifications,
    Fullscreen,
    StorageAccess,
    Clipboard,
    Other,
}

/// <summary>The answer to a permission card.</summary>
public enum PermissionAnswer
{
    Deny,
    AllowOnce,
    AlwaysForSite,
}

/// <summary>The answer to a download card: save to the downloads folder, choose where, or don't download.</summary>
public enum DownloadAnswer
{
    Cancel,
    Save,
    SaveAs,
}

/// <summary>
/// One question from a page. <see cref="PageUrl"/> is the page (or the challenged address, for sign-in) and
/// <see cref="CausedByAgent"/> says whether omp's browser tool caused it (its input, script or navigation was running):
/// those are answered by the agent (<see cref="DialogBroker.AgentPending"/>), except sign-in, which is always the user's.
/// </summary>
public abstract record BrowserDialogRequest
{
    public Uri? PageUrl { get; init; }
    public bool CausedByAgent { get; init; }
    public abstract BrowserDialogKind Kind { get; }
}

/// <summary>An HTTP authentication challenge (Basic, Digest, NTLM, Negotiate; <see cref="IsProxy"/> for a proxy).</summary>
/// <param name="FailedBefore">An earlier answer for the same challenge was refused (WebKit's previousFailureCount &gt; 0).</param>
/// <param name="UserName">The user name to fill in (the engine's proposed credential), if any.</param>
public sealed record CredentialRequest(string Host, int Port, string? Realm, bool IsProxy, bool FailedBefore,
    string? UserName = null, string? AuthScheme = null) : BrowserDialogRequest
{
    public override BrowserDialogKind Kind => BrowserDialogKind.Credentials;
}

/// <summary>A user name and password for a <see cref="CredentialRequest"/>; never logged, never sent to omp.</summary>
public sealed record CredentialAnswer(string User, string Password)
{
    public override string ToString() => $"CredentialAnswer {{ User = {User} }}";
}

/// <summary><c>window.alert</c>; answered with null (OK).</summary>
public sealed record AlertRequest(string Message) : BrowserDialogRequest
{
    public override BrowserDialogKind Kind => BrowserDialogKind.Alert;
}

/// <summary><c>window.confirm</c>; answered with true (OK) or false (Cancel).</summary>
public sealed record ConfirmRequest(string Message) : BrowserDialogRequest
{
    public override BrowserDialogKind Kind => BrowserDialogKind.Confirm;
}

/// <summary><c>window.prompt</c>; answered with the text, or null (Cancel).</summary>
public sealed record PromptRequest(string Message, string? Default) : BrowserDialogRequest
{
    public override BrowserDialogKind Kind => BrowserDialogKind.Prompt;
}

/// <summary>A page's <c>beforeunload</c> handler asks whether to leave; answered with true (leave) or false (stay).</summary>
public sealed record BeforeUnloadRequest(string? Message) : BrowserDialogRequest
{
    public override BrowserDialogKind Kind => BrowserDialogKind.BeforeUnload;
}

/// <summary>An <c>&lt;input type=file&gt;</c>; answered with the chosen paths, or null (nothing chosen).</summary>
/// <param name="Accept">The input's accept list (MIME types or extensions), empty for any file.</param>
public sealed record FileChooserRequest(bool Multiple, IReadOnlyList<string> Accept, bool AllowDirectories = false) : BrowserDialogRequest
{
    public override BrowserDialogKind Kind => BrowserDialogKind.FileChooser;
}

/// <summary>A download the page started; answered with a <see cref="DownloadAnswer"/>.</summary>
public sealed record DownloadRequest(string FileName, long? Size, Uri? Url) : BrowserDialogRequest
{
    public override BrowserDialogKind Kind => BrowserDialogKind.Download;
}

/// <summary>A site asks for a permission; answered with a <see cref="PermissionAnswer"/>.</summary>
public sealed record PermissionRequest(PermissionKind Permission, string Origin) : BrowserDialogRequest
{
    public override BrowserDialogKind Kind => BrowserDialogKind.Permission;
}

/// <summary>A page opened (or tried to open) a new window; answered with true (open it) or false.</summary>
public sealed record PopupNoticeRequest(Uri? Url) : BrowserDialogRequest
{
    public override BrowserDialogKind Kind => BrowserDialogKind.PopupNotice;
}

/// <summary>
/// A pending question: completed once, by <see cref="Complete"/> (an answer) or <see cref="Cancel"/> (the answer the
/// engine gives when nobody answers: OK for an alert, Cancel for confirm/prompt/sign-in/file, leave for beforeunload,
/// Deny, don't download, don't open). Thread-safe; later calls return false and change nothing.
/// </summary>
public sealed class BrowserDialog
{
    private readonly TaskCompletionSource<object?> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DialogBroker _broker;
    private int _completed;
    internal CancellationTokenRegistration Registration;

    internal BrowserDialog(DialogBroker broker, BrowserDialogRequest request, long id)
    {
        _broker = broker;
        Request = request;
        Id = id;
        // Sign-in is always the user's: the agent never sees or types credentials.
        RoutedToAgent = request.CausedByAgent && request.Kind != BrowserDialogKind.Credentials;
    }

    /// <summary>Increasing per broker: the order the questions came in.</summary>
    public long Id { get; }
    public BrowserDialogRequest Request { get; }
    public BrowserDialogKind Kind => Request.Kind;
    public bool CausedByAgent => Request.CausedByAgent;

    /// <summary>The agent answers it (it caused it) rather than the user; <see cref="DialogBroker.HandToUser"/> changes that.</summary>
    public bool RoutedToAgent { get; internal set; }

    /// <summary>A JavaScript dialog: the page's scripts wait until it is answered.</summary>
    public bool IsModal => Kind is BrowserDialogKind.Alert or BrowserDialogKind.Confirm or BrowserDialogKind.Prompt or BrowserDialogKind.BeforeUnload;

    /// <summary>The page's text for JavaScript dialogs; null for the rest.</summary>
    public string? Message => Request switch
    {
        AlertRequest a => a.Message,
        ConfirmRequest c => c.Message,
        PromptRequest p => p.Message,
        BeforeUnloadRequest b => b.Message,
        _ => null,
    };

    /// <summary>The <see cref="Kind"/> as the agent's browser reports it ("alert", "confirm", "prompt", "beforeunload", …).</summary>
    public string KindName => Kind switch
    {
        BrowserDialogKind.BeforeUnload => "beforeunload",
        BrowserDialogKind.FileChooser => "filechooser",
        BrowserDialogKind.PopupNotice => "popup",
        _ => Kind.ToString().ToLowerInvariant(),
    };

    public bool IsCompleted => Volatile.Read(ref _completed) != 0;

    internal Task<object?> Task => _done.Task;

    /// <summary>
    /// Answers it: null for an alert; bool for confirm, beforeunload and popup; string? for prompt;
    /// <see cref="CredentialAnswer"/>? for sign-in; IReadOnlyList&lt;string&gt;? for files; <see cref="DownloadAnswer"/>;
    /// <see cref="PermissionAnswer"/>. Throws <see cref="ArgumentException"/> for an answer of the wrong type.
    /// </summary>
    public bool Complete(object? answer)
    {
        if (!Fits(Kind, answer)) throw new ArgumentException($"{answer?.GetType().Name ?? "null"} is not an answer to a {KindName} dialog.", nameof(answer));
        return Finish(answer);
    }

    /// <summary>Closes it with the answer nobody-answered gets (see the class remarks).</summary>
    public bool Cancel() => Finish(DefaultAnswer(Kind));

    private bool Finish(object? answer)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0) return false;
        Registration.Dispose();
        _broker.Remove(this);
        _done.TrySetResult(answer);
        return true;
    }

    internal static object? DefaultAnswer(BrowserDialogKind kind) => kind switch
    {
        BrowserDialogKind.BeforeUnload => true,
        BrowserDialogKind.Confirm or BrowserDialogKind.PopupNotice => false,
        BrowserDialogKind.Download => DownloadAnswer.Cancel,
        BrowserDialogKind.Permission => PermissionAnswer.Deny,
        _ => null,
    };

    private static bool Fits(BrowserDialogKind kind, object? answer) => kind switch
    {
        BrowserDialogKind.Alert => answer is null,
        BrowserDialogKind.Confirm or BrowserDialogKind.BeforeUnload or BrowserDialogKind.PopupNotice => answer is bool,
        BrowserDialogKind.Prompt => answer is null or string,
        BrowserDialogKind.Credentials => answer is null or CredentialAnswer,
        BrowserDialogKind.FileChooser => answer is null or IReadOnlyList<string>,
        BrowserDialogKind.Download => answer is DownloadAnswer,
        BrowserDialogKind.Permission => answer is PermissionAnswer,
        _ => false,
    };
}

/// <summary>
/// Every native window a page of the browser pane would open (sign-in, alert/confirm/prompt, beforeunload, file
/// chooser, download, permission, popup), as questions answered in the pane. One broker per page. The user sees one
/// card at a time, in the order they came (<see cref="Current"/>); what the agent caused waits for the agent
/// (<see cref="AgentPending"/>, <see cref="CurrentModal"/>) unless handed to the user. Each question is answered exactly
/// once: by its card, by the agent, by its <see cref="CancellationToken"/>, or by <see cref="CancelAll"/> (navigation,
/// page closed, web process crashed), never by an exception. Callable from any thread; <see cref="Changed"/> is raised on
/// the calling thread, outside the broker's lock.
/// </summary>
public sealed class DialogBroker : IBrowserAuthHandler, IDisposable
{
    private readonly object _gate = new();
    private readonly List<BrowserDialog> _pending = [];
    private long _nextId;
    private bool _disposed;

    /// <summary>A question came, was answered, or moved from the agent to the user.</summary>
    public event EventHandler? Changed;

    /// <summary>The card the user sees: the oldest question not waiting for the agent; null when there is none.</summary>
    public BrowserDialog? Current
    {
        get { lock (_gate) return _pending.FirstOrDefault(d => !d.RoutedToAgent); }
    }

    /// <summary>How many user cards wait behind <see cref="Current"/>.</summary>
    public int QueuedBehindCurrent
    {
        get { lock (_gate) return Math.Max(0, _pending.Count(d => !d.RoutedToAgent) - 1); }
    }

    /// <summary>
    /// The oldest pending JavaScript dialog (alert, confirm, prompt, beforeunload), whoever caused it: while it is open
    /// the page's scripts wait, so the agent's browser reports it (<c>dialog_open</c>) instead of running scripts.
    /// </summary>
    public BrowserDialog? CurrentModal
    {
        get { lock (_gate) return _pending.FirstOrDefault(d => d.IsModal); }
    }

    /// <summary>The questions the agent caused and answers itself, oldest first.</summary>
    public IReadOnlyList<BrowserDialog> AgentPending
    {
        get { lock (_gate) return [.. _pending.Where(d => d.RoutedToAgent)]; }
    }

    /// <summary>Every unanswered question, oldest first.</summary>
    public IReadOnlyList<BrowserDialog> Pending
    {
        get { lock (_gate) return [.. _pending]; }
    }

    public Task<CredentialAnswer?> RequestCredentialsAsync(CredentialRequest request, CancellationToken ct) => AskAsync<CredentialAnswer?>(request, ct);
    public async Task AlertAsync(AlertRequest request, CancellationToken ct) => await AskAsync<object?>(request, ct).ConfigureAwait(false);
    public Task<bool> ConfirmAsync(ConfirmRequest request, CancellationToken ct) => AskAsync<bool>(request, ct);
    public Task<string?> PromptAsync(PromptRequest request, CancellationToken ct) => AskAsync<string?>(request, ct);
    public Task<bool> BeforeUnloadAsync(BeforeUnloadRequest request, CancellationToken ct) => AskAsync<bool>(request, ct);
    public Task<IReadOnlyList<string>?> ChooseFilesAsync(FileChooserRequest request, CancellationToken ct) => AskAsync<IReadOnlyList<string>?>(request, ct);
    public Task<DownloadAnswer> DownloadAsync(DownloadRequest request, CancellationToken ct) => AskAsync<DownloadAnswer>(request, ct);
    public Task<PermissionAnswer> PermissionAsync(PermissionRequest request, CancellationToken ct) => AskAsync<PermissionAnswer>(request, ct);
    public Task<bool> PopupNoticeAsync(PopupNoticeRequest request, CancellationToken ct) => AskAsync<bool>(request, ct);

    /// <summary>The agent answers the oldest JavaScript dialog (see <see cref="BrowserDialog.Complete"/> for the answer types).</summary>
    public bool TryAnswerModal(object? answer) => CurrentModal is { } d && d.Complete(answer);

    /// <summary>A question the agent caused goes to the user instead (the agent can't answer it, or gave up).</summary>
    public bool HandToUser(BrowserDialog dialog)
    {
        lock (_gate)
        {
            if (!dialog.RoutedToAgent || !_pending.Contains(dialog)) return false;
            dialog.RoutedToAgent = false;
        }
        Raise();
        return true;
    }

    /// <summary>Closes every question with its nobody-answered answer: the page navigated away, closed, or crashed.</summary>
    public void CancelAll()
    {
        List<BrowserDialog> all;
        lock (_gate) all = [.. _pending];
        foreach (var d in all) d.Cancel();
    }

    /// <summary>The page is gone: every question is cancelled, and new ones get the nobody-answered answer at once.</summary>
    public void Dispose()
    {
        lock (_gate) _disposed = true;
        CancelAll();
    }

    private async Task<T> AskAsync<T>(BrowserDialogRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        BrowserDialog dialog;
        lock (_gate)
        {
            if (_disposed || ct.IsCancellationRequested) return (T)BrowserDialog.DefaultAnswer(request.Kind)!;
            dialog = new BrowserDialog(this, request, ++_nextId);
            _pending.Add(dialog);
        }
        // Registered after it is listed: a token cancelled in between cancels it right here, synchronously.
        dialog.Registration = ct.Register(static d => ((BrowserDialog)d!).Cancel(), dialog);
        if (dialog.IsCompleted) dialog.Registration.Dispose();
        Raise();
        return (T)(await dialog.Task.ConfigureAwait(false))!;
    }

    internal void Remove(BrowserDialog dialog)
    {
        bool removed;
        lock (_gate) removed = _pending.Remove(dialog);
        if (removed) Raise();
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}
