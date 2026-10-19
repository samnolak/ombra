using Ombra.Rpc;

namespace Ombra.ClientCore;

/// <summary>
/// "Don't ask again" for omp's approval requests (Claude Code's "Yes, and don't ask again for …"): a request a rule covers
/// is answered "Approve" as it arrives, without a card, and the conversation says so. The engine's typed request
/// (<see cref="ApprovalRequest"/>) names the tool, the command and the scopes it allows; omp keeps no per-command grants
/// itself (its <c>tools.approval</c> allows a whole tool or nothing), so the client answers on the user's behalf. An omp
/// without typed approval gets no rules: every request is asked.
/// "No, and tell omp what to do differently": the reason goes with the Deny, and the agent reads it in the failed tool
/// result. An omp without typed approval has no room for it, so there it goes as a steering message instead.
/// </summary>
public sealed partial class SessionController
{
    /// <summary>The rules that answer approvals for the user; null: every request is asked.</summary>
    public ApprovalRuleSet? ApprovalRules { get; set; }

    /// <summary>The omp session a session rule belongs to (its file; the id before omp has one). Under <see cref="_lock"/>.</summary>
    private string? RuleSession => _state.SessionFile ?? _state.SessionId;

    /// <summary>The session a "this session" rule given now would belong to (Settings → Permissions marks those).</summary>
    public string? RuleSessionKey
    {
        get { lock (_lock) return RuleSession; }
    }

    /// <summary>Under <see cref="_lock"/>: takes the pending approvals a rule covers out of the state, with the rule.</summary>
    private List<(PendingDialog Dialog, ApprovalGrant Grant)>? TakeCoveredApprovals(IEnumerable<PendingDialog> candidates)
    {
        if (ApprovalRules is not { } rules) return null;
        List<(PendingDialog, ApprovalGrant)>? taken = null;
        foreach (var d in candidates.ToList())
            if (rules.Match(ApprovalRequest.From(d), _state.Cwd, RuleSession) is { } grant && _state.TakeDialog(d.Id) is { } pending)
                (taken ??= []).Add((pending, grant));
        return taken;
    }

    private async Task AutoAllowAsync(RpcConnection conn, List<(PendingDialog Dialog, ApprovalGrant Grant)> allowed)
    {
        foreach (var (dialog, grant) in allowed)
        {
            try
            {
                await conn.SendExtensionUiValueAsync(dialog.Id, "Approve").ConfigureAwait(false);
                Mutate(s => s.AddNotice(NoticeLevel.Info, $"Allowed automatically — {grant.Describe()}"));
            }
            catch (Exception e) when (e is RpcConnectionClosedException or ArgumentException or OperationCanceledException)
            {
                Mutate(s => s.AddNotice(NoticeLevel.Error, $"The automatic answer did not reach omp ({e.Message}) — {dialog.Headline}"));
            }
        }
    }

    /// <summary>
    /// Approves a pending request and keeps <paramref name="rule"/> for <paramref name="scope"/>, so the requests it covers
    /// are answered from now on — also any already waiting behind this one. When the rule cannot be saved the request is
    /// still approved, once, and the conversation says the rule was not kept.
    /// </summary>
    public async Task<bool> AllowWithRuleAsync(string id, ApprovalRule rule, ApprovalScope scope, CancellationToken ct = default)
    {
        var approve = new DialogAnswer.Value("Approve");
        if (ApprovalRules is not { } rules) return await AnswerDialogAsync(id, approve, ct).ConfigureAwait(false);
        PendingDialog? pending;
        string? cwd, session;
        lock (_lock)
        {
            pending = _state.Dialogs.FirstOrDefault(d => d.Id == id);
            cwd = _state.Cwd;
            session = RuleSession;
        }
        if (pending is null) return false; // answered, withdrawn or expired meanwhile: no rule from a late click either
        ApprovalGrant? grant = null;
        string? error = null;
        try
        {
            if (ApprovalRequest.From(pending) is not { } request || !rule.Matches(request))
                throw new ArgumentException("The rule does not safely cover this request.", nameof(rule));
            if (!request.Allows(scope))
                throw new ArgumentException("omp does not offer to keep this answer for that long.", nameof(scope));
            grant = rules.Add(rule, scope, cwd, session);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            error = e.Message;
        }
        var answered = await AnswerDialogAsync(id, approve, ct).ConfigureAwait(false);
        Mutate(s =>
        {
            if (grant is not null) s.AddNotice(NoticeLevel.Info, $"Won't ask again for {grant.Describe()} — the rules are in Settings → Permissions");
            else s.AddNotice(NoticeLevel.Error, $"The rule was not kept ({error}); omp will ask again next time");
        });
        if (grant is not null) await AllowCoveredPendingAsync().ConfigureAwait(false);
        return answered;
    }

    /// <summary>Answers the waiting approvals the rules now cover (after a rule was added).</summary>
    public async Task AllowCoveredPendingAsync()
    {
        if (_omp?.Connection is not { } conn) return;
        List<(PendingDialog Dialog, ApprovalGrant Grant)>? taken;
        lock (_lock) taken = TakeCoveredApprovals(_state.Dialogs);
        if (taken is null) return;
        await AutoAllowAsync(conn, taken).ConfigureAwait(false);
        _changes.Writer.TryWrite(true);
    }

    /// <summary>
    /// Denies a pending request and tells the agent why. With a typed request the reason travels with the Deny into the
    /// failed tool result; from an omp without one it is queued as a steering message first, so it is waiting when the
    /// denied tool returns. An empty feedback is a plain deny. False when the request is no longer pending (then nothing
    /// is sent).
    /// </summary>
    public async Task<bool> DenyWithFeedbackAsync(string id, string feedback, CancellationToken ct = default)
    {
        feedback = feedback.Trim();
        PendingDialog? pending;
        lock (_lock) pending = _state.Dialogs.FirstOrDefault(d => d.Id == id);
        if (pending is null) return false;
        if (feedback.Length == 0) return await AnswerDialogAsync(id, new DialogAnswer.Value("Deny"), ct).ConfigureAwait(false);
        if (pending.Approval is not null) return await AnswerDialogAsync(id, new DialogAnswer.Denied(feedback), ct).ConfigureAwait(false);
        await QueueAsync(QueueKind.Steer, feedback, [], ct).ConfigureAwait(false);
        return await AnswerDialogAsync(id, new DialogAnswer.Value("Deny"), ct).ConfigureAwait(false);
    }
}
