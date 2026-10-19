using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

/// <summary>
/// The dialog omp is waiting on (tool approval, ask tool, extension select/confirm/input/editor).
/// Answers go through <see cref="SessionController.AnswerDialogAsync"/>, which drops a late answer.
/// </summary>
public sealed partial class DialogViewModel : ObservableObject
{
    private readonly Func<string, DialogAnswer, Task> _answer;
    private readonly ApprovalActions? _actions;
    private bool _answered;

    public DialogViewModel(PendingDialog model, Func<string, DialogAnswer, Task> answer, ApprovalActions? actions = null)
    {
        Model = model;
        _answer = answer;
        _actions = model.Kind == DialogKind.Approval ? actions : null;
        _inputText = model.Prefill ?? "";
        Options = [.. model.Options.Select((o, i) => new DialogOptionViewModel(o, this, i + 1))];
        Fields = [.. model.Fields.Select(f => new ElicitFieldViewModel(f))];
        Ask = model.Kind == DialogKind.Ask && model.Questions.Count > 0 ? new AskFormViewModel(model.Questions, AnswerAsync) : null;
        SuggestedRule = _actions is null ? null : ApprovalRule.Suggest(ApprovalRequest.From(model));
        UpdateRemaining(DateTimeOffset.UtcNow);
    }

    public PendingDialog Model { get; }
    public string Id => Model.Id;
    public DialogKind Kind => Model.Kind;
    public bool IsApproval => Kind == DialogKind.Approval;
    public bool IsSelect => Kind == DialogKind.Select;
    public bool IsConfirm => Kind == DialogKind.Confirm;
    public bool IsInput => Kind == DialogKind.Input;
    public bool IsEditor => Kind == DialogKind.Editor;
    public bool IsTextEntry => IsInput || IsEditor;
    public bool IsElicitation => Kind == DialogKind.Elicitation;

    /// <summary>omp's ask form: every question of one ask tool call, one tab each (null for other dialogs).</summary>
    public AskFormViewModel? Ask { get; }
    public bool IsAsk => Ask is not null;

    /// <summary>For a connector's form: its fields, in order.</summary>
    public IReadOnlyList<ElicitFieldViewModel> Fields { get; }

    /// <summary>For a connector's form: why it was not sent (a field's check failed).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFormError))]
    private string _formError = "";

    public bool HasFormError => FormError.Length > 0;

    public string Caption => Kind switch
    {
        DialogKind.Approval => Loc.T("Permission needed"),
        DialogKind.Confirm => Loc.T("Confirmation needed"),
        DialogKind.Elicitation => Loc.F("Form from connector “{0}”", Model.Server),
        _ => Loc.T("omp is asking"),
    };

    private string SourceHeadline => Model.Title.IndexOf('\n') is var at && at >= 0 ? Model.Title[..at] : Model.Title;
    public string Headline => IsApproval ? ApprovalDisplay.Format(SourceHeadline) : Model.Headline;
    /// <summary>Unmodified approval source, separate from every display-only escape.</summary>
    public string SourceDetails => IsApproval && Model.Title.IndexOf('\n') is var at && at >= 0 ? Model.Title[(at + 1)..] : Model.Details;
    /// <summary>For approvals: visible tool details (command, paths). Otherwise the rest of the title.</summary>
    public string Details => IsApproval ? ApprovalDisplay.Format(SourceDetails, preserveNewlines: true) : Model.Details;
    public bool HasDetails => Preview.Length > 0;
    public string? Message => IsElicitation ? Model.Title : IsApproval && Model.Message is { } message ? ApprovalDisplay.Format(message, preserveNewlines: true) : Model.Message;
    public bool HasMessage => !string.IsNullOrEmpty(Message);

    /// <summary>The engine's typed request; null from an omp without structured approval (the card shows its text then).</summary>
    public ApprovalRequest? Request => IsApproval ? Model.Approval : null;

    /// <summary>The tool an approval is for; empty for other dialogs and for an omp without typed approval.</summary>
    public string ToolName => Request?.Tool ?? "";

    /// <summary>The card's title: what omp wants, in words ("omp wants to run a command"); a question's own words otherwise.</summary>
    public string Title => IsElicitation ? Loc.F("Connector “{0}” asks for information", Model.Server)
        : Ask is { Questions.Count: > 1 } form ? Loc.F("omp has {0} questions", form.Questions.Count)
        : ToolName.Length > 0 ? ApprovalTitle(ToolName) : Headline;

    /// <summary>What the call may do, from the engine's tier: shown beside the title.</summary>
    public string RiskText => Request?.Risk switch
    {
        ApprovalRisk.Read => Loc.T("Reads only"),
        ApprovalRisk.Write => Loc.T("Changes files"),
        ApprovalRisk.Exec => Loc.T("Runs programs"),
        _ => "",
    };
    public bool HasRisk => RiskText.Length > 0;

    /// <summary>
    /// What the card shows in its code box: for a typed approval the tool's details without the label the title already
    /// says (a command, not "Command: …"); from an omp without typed approval, its text as sent.
    /// </summary>
    public string Preview => IsApproval ? ApprovalParts().Preview : IsElicitation || IsAsk ? "" : Details;

    /// <summary>Why omp asks (the tool's reason, an MCP origin, provider safety checks), under the preview.</summary>
    public string Note => IsApproval ? ApprovalParts().Note : "";
    public bool HasNote => Note.Length > 0;

    private (string Preview, string Note) ApprovalParts()
    {
        if (Request is not { } request) return (Details, HiddenNote(null));
        var notes = new List<string>();
        if (request.Reason is { } reason) notes.Add(ApprovalDisplay.Format(reason).Trim());
        if (request.FromMcp)
            notes.Add(request.McpServer is { } server ? Loc.F("Tool of MCP server “{0}”", ApprovalDisplay.Format(server)) : Loc.T("MCP server tool"));
        // The folder decides which scripts and build files run; a rule never answers a command run elsewhere.
        if (request.Cwd is { } cwd) notes.Add(Loc.F("Runs in {0}", ApprovalDisplay.Format(cwd)));
        if (request.SafetyCheckLines.Count > 0)
            notes.Add(Loc.T("Provider safety checks:") + " " + string.Join("; ", request.SafetyCheckLines.Select(l => ApprovalDisplay.Format(l))));
        if (HiddenNote(request.Command) is { Length: > 0 } hidden) notes.Add(hidden);
        // bash: the complete command the engine sent, never a shortened display line
        var preview = request.Command is { } command ? ApprovalDisplay.Format(command)
            : string.Join('\n', request.Details.Select(l => ApprovalDisplay.Format(l, preserveNewlines: true)));
        // One labelled value the title already names: the value alone (edit's "File: …", a long command's "Command: …")
        foreach (var label in (string[])["Command: ", "File: ", "Path: "])
            if (request.Command is null && preview.StartsWith(label, StringComparison.Ordinal) && (label == "Command: " || !preview.Contains('\n')))
                preview = preview[label.Length..];
        return (preview, string.Join(" · ", notes.Where(n => n.Length > 0)));
    }

    private string HiddenNote(string? command) =>
        ApprovalDisplay.HasHiddenCharacters(Model.Title, preserveNewlines: true)
        || command is not null && ApprovalDisplay.HasHiddenCharacters(command)
        || Model.Message is { } message && ApprovalDisplay.HasHiddenCharacters(message, preserveNewlines: true)
        || Request?.Details.Any(l => ApprovalDisplay.HasHiddenCharacters(l, preserveNewlines: true)) == true
            ? Loc.T("Hidden or control characters are shown as [U+XXXX]. Review the original request carefully.")
            : "";

    /// <summary>Claude Code's wording with omp as the subject: "omp wants to run a command".</summary>
    public static string ApprovalTitle(string tool) => tool switch
    {
        "bash" => Loc.T("omp wants to run a command"),
        "edit" or "ast_edit" => Loc.T("omp wants to make changes"),
        "write" => Loc.T("omp wants to write a file"),
        "read" => Loc.T("omp wants to read a file"),
        "eval" => Loc.T("omp wants to run code"),
        "fetch" or "browser" => Loc.T("omp wants to open a web page"),
        "task" => Loc.T("omp wants to start a subagent"),
        _ => Loc.F("omp wants to use {0}", ToolRowViewModel.DisplayNameOf(tool)),
    };

    public string Placeholder => Model.Placeholder ?? "";
    public IReadOnlyList<DialogOptionViewModel> Options { get; }

    [ObservableProperty] private string _inputText;
    [ObservableProperty] private string _remaining = "";
    [ObservableProperty] private string _queueText = "";

    public bool ReplyInFlight { get; private set; }
    public bool PreserveReply { get; init; } = true;
    private bool _replyPreserved;
    public string ReplyForRecovery => !PreserveReply || !IsTextEntry || _replyPreserved || string.IsNullOrWhiteSpace(InputText) ? ""
        : "> " + string.Join("\n", new[] { Model.Headline, Model.Details, Model.Message ?? "" }.Where(s => s.Length > 0))
            .Replace("\r", "").Replace("\n", "\n> ") + "\n" + InputText;
    public event Action? DraftChanged;
    partial void OnInputTextChanged(string value) => DraftChanged?.Invoke();
    internal void MarkReplyPreserved() { _replyPreserved = true; DraftChanged?.Invoke(); }
    internal void CompleteReply(bool accepted)
    {
        ReplyInFlight = false;
        if (accepted) _replyPreserved = true;
        else _answered = false;
        DraftChanged?.Invoke();
    }

    public bool HasDeadline => Model.Deadline is not null;

    /// <summary>Cosmetic countdown; the Core closes the dialog at the deadline whatever the UI shows.</summary>
    public void UpdateRemaining(DateTimeOffset now)
    {
        if (Model.Deadline is not { } d) return;
        var left = d - now;
        Remaining = left <= TimeSpan.Zero ? Loc.T("closing…") : Loc.F("{0}s left", Math.Ceiling(left.TotalSeconds).ToString("0", CultureInfo.InvariantCulture));
    }

    private async Task AnswerAsync(DialogAnswer a)
    {
        if (_answered) return;
        _answered = true;
        ReplyInFlight = IsTextEntry;
        try { await _answer(Id, a); }
        finally { ReplyInFlight = false; }
    }

    [RelayCommand] private Task Allow() => AnswerAsync(new DialogAnswer.Value("Approve"));
    [RelayCommand] private Task Deny() => AnswerAsync(new DialogAnswer.Value("Deny"));

    // ── Claude Code's "Yes, and don't ask again for …" and "No, and tell omp what to do differently" ──

    /// <summary>The rule this request offers (<c>bash(npm test:*)</c>, <c>write</c>); null when none fits or the card cannot keep rules.</summary>
    public ApprovalRule? SuggestedRule { get; }
    public bool CanAllowWithRule => SuggestedRule is not null && (CanAllowSession || CanAllowSaved);
    /// <summary>The engine offers to keep this answer for the session ("This session").</summary>
    public bool CanAllowSession => SuggestedRule is not null && Request?.Allows(ApprovalScope.Session) == true;
    /// <summary>The engine offers to keep this answer beyond the session ("This project", "Always").</summary>
    public bool CanAllowSaved => SuggestedRule is not null && Request?.Allows(ApprovalScope.Always) == true;
    /// <summary>"Don't ask again for commands starting with “npm test”".</summary>
    public string AllowWithRuleText => SuggestedRule is { } r ? Loc.F("Don't ask again for {0}", AnswerNotices.RuleDescription(r.Description)) : "";
    /// <summary>The rule as written in Settings → Permissions (the tooltip of the scope buttons).</summary>
    public string RuleText => SuggestedRule is { } rule ? ApprovalDisplay.Format(rule.Text) : "";

    /// <summary>Allow now and keep the rule: <paramref name="scope"/> is "session", "project" or "always".</summary>
    [RelayCommand]
    private async Task AllowWithRule(string? scope)
    {
        if (_answered || _actions is null || SuggestedRule is not { } rule) return;
        var s = scope switch { "session" => ApprovalScope.Session, "project" => ApprovalScope.Project, "always" => ApprovalScope.Always, _ => (ApprovalScope?)null };
        if (s is null || Request?.Allows(s.Value) != true) return;
        _answered = true;
        await _actions.AllowWithRule(Id, rule, s.Value);
    }

    public bool CanGiveFeedback => _actions is not null;
    /// <summary>The "tell omp what to do instead" box is open.</summary>
    [ObservableProperty] private bool _isWritingFeedback;
    [ObservableProperty] private string _feedbackText = "";

    [RelayCommand] private void StartFeedback() { if (CanGiveFeedback && !_answered) IsWritingFeedback = true; }
    [RelayCommand] private void CancelFeedback() => IsWritingFeedback = false;

    /// <summary>Deny with the words as the reason the agent reads in the tool result (a steering message from an omp without typed approval). Empty: a plain deny.</summary>
    [RelayCommand]
    private async Task SendFeedback()
    {
        if (_answered || _actions is null) return;
        _answered = true;
        await _actions.DenyWithFeedback(Id, FeedbackText);
    }

    /// <summary>A digit key: 1 / 2 / 3 answer an approval (Allow / Deny / Deny and say why), 1–9 pick a choice. False when the digit means nothing here.</summary>
    public bool TryPick(int digit)
    {
        if (IsApproval && digit is 1 or 2)
        {
            (digit == 1 ? AllowCommand : DenyCommand).Execute(null);
            return true;
        }
        if (IsApproval && digit == 3 && CanGiveFeedback)
        {
            StartFeedback();
            return true;
        }
        if (IsSelect && Options.FirstOrDefault(o => o.Number == digit) is { } option)
        {
            option.ChooseCommand.Execute(null);
            return true;
        }
        if (Ask is { } form) return form.TryPick(digit);
        return false;
    }
    [RelayCommand] private Task Yes() => AnswerAsync(new DialogAnswer.Confirmed(true));
    [RelayCommand] private Task No() => AnswerAsync(new DialogAnswer.Confirmed(false));
    [RelayCommand] private Task Submit() => AnswerAsync(new DialogAnswer.Value(InputText));
    [RelayCommand] private Task Dismiss() => AnswerAsync(new DialogAnswer.Cancelled());

    /// <summary>A connector's form: every field checked first (all errors shown at once), then the values go to omp.</summary>
    [RelayCommand]
    private Task SendForm()
    {
        var content = new System.Text.Json.Nodes.JsonObject();
        var ok = true;
        foreach (var field in Fields) ok &= field.TryAdd(content);
        FormError = ok ? "" : Loc.T("Check the marked fields.");
        return ok ? AnswerAsync(new DialogAnswer.Elicited(true, content)) : Task.CompletedTask;
    }

    /// <summary>A connector's form: say no (the server is told the user declined).</summary>
    [RelayCommand] private Task DeclineForm() => AnswerAsync(new DialogAnswer.Elicited(false, null));
    internal Task Choose(string label) => AnswerAsync(new DialogAnswer.Value(label));
}

/// <summary>What an approval card can do beyond Allow and Deny (SessionController.AllowWithRuleAsync / DenyWithFeedbackAsync).</summary>
public sealed record ApprovalActions(Func<string, ApprovalRule, ApprovalScope, Task> AllowWithRule, Func<string, string, Task> DenyWithFeedback);

public sealed partial class DialogOptionViewModel(DialogOption option, DialogViewModel owner, int number = 0) : ObservableObject
{
    /// <summary>1–9: the key that picks it (Claude Code's menus); 0 past nine.</summary>
    public int Number { get; } = number <= 9 ? number : 0;
    public bool HasNumber => Number > 0;
    public string Label => option.Label;
    public string? Description => option.Description;
    public bool HasDescription => option.Description is not null;
    public bool Recommended => option.Recommended;
    /// <summary>The label as shown: omp's "(Recommended)" suffix becomes a badge beside it (the answer keeps omp's label).</summary>
    public string DisplayLabel => Recommended && Label.TrimEnd() is var t && t.Length > "(Recommended)".Length
        ? t[..^"(Recommended)".Length].TrimEnd()
        : Label;

    [RelayCommand] private Task Choose() => owner.Choose(option.Label);
}
