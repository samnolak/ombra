using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

/// <summary>
/// The approval card's "Don't ask again" (a rule in <see cref="ApprovalRules"/>, answered by the session from then on) and
/// "Deny and say why" (a steering message to omp), and Settings → Permissions, where the rules are listed and removed.
/// </summary>
public partial class MainViewModel
{
    private readonly ApprovalActions _approvalActions;
    private PermissionRulesViewModel? _permissions;

    /// <summary>The "don't ask again" rules (the session's own set: it answers the requests they cover).</summary>
    public ApprovalRuleSet ApprovalRules { get; }

    /// <summary>Settings → Permissions.</summary>
    public PermissionRulesViewModel Permissions => _permissions ??= new PermissionRulesViewModel(this);

    /// <summary>The project folder and session a rule given now would be for.</summary>
    internal (string? Cwd, string? Session) RuleContext => (_open.Last?.Cwd ?? Session.Snapshot().Cwd, Session.RuleSessionKey);

    private async Task AllowWithRuleAsync(string id, ApprovalRule rule, ApprovalScope scope)
    {
        var open = _open;
        try
        {
            await open.Controller.AllowWithRuleAsync(id, rule, scope, _cts.Token);
            ApplyIfShown(open);
            // The new rule may cover what another chat is waiting for, too
            foreach (var other in _opens.Values.Where(o => o != open).ToList()) await other.Controller.AllowCoveredPendingAsync();
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // The window is closing: the request was abandoned on purpose.
        }
    }

    private async Task DenyWithFeedbackAsync(string id, string feedback)
    {
        var open = _open;
        try
        {
            await open.Controller.DenyWithFeedbackAsync(id, feedback, _cts.Token);
            ApplyIfShown(open);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // The window is closing: the request was abandoned on purpose.
        }
    }
}
