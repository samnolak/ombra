using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

/// <summary>
/// The dialog omp is waiting on (tool approval, ask tool, extension select/confirm/input/editor).
/// Answers go through <see cref="SessionController.AnswerDialogAsync"/>, which drops a late answer.
/// </summary>
public sealed partial class DialogViewModel : ObservableObject
{
    private readonly Func<string, DialogAnswer, Task> _answer;
    private bool _answered;

    public DialogViewModel(PendingDialog model, Func<string, DialogAnswer, Task> answer)
    {
        Model = model;
        _answer = answer;
        _inputText = model.Prefill ?? "";
        Options = [.. model.Options.Select((o, i) => new DialogOptionViewModel(o, this, i + 1))];
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

    public string Caption => Kind switch
    {
        DialogKind.Approval => "Permission needed",
        DialogKind.Confirm => "Confirmation needed",
        _ => "omp is asking",
    };

    public string Headline => Model.Headline;
    /// <summary>For approvals: the tool's details (command, paths). Otherwise the rest of the title.</summary>
    public string Details => Model.Details;
    public bool HasDetails => Preview.Length > 0;
    public string? Message => Model.Message;
    public bool HasMessage => !string.IsNullOrEmpty(Model.Message);

    private const string ApprovalPrefix = "Allow tool: ";

    /// <summary>The tool an approval is for ("bash" from omp's "Allow tool: bash"); empty for other dialogs.</summary>
    public string ToolName => IsApproval && Headline.StartsWith(ApprovalPrefix, StringComparison.Ordinal) ? Headline[ApprovalPrefix.Length..].Trim() : "";

    /// <summary>The card's title: what omp wants, in words ("omp wants to run a command"); a question's own words otherwise.</summary>
    public string Title => IsApproval && ToolName.Length > 0 ? ApprovalTitle(ToolName) : Headline;

    /// <summary>
    /// What the card shows in its code box: for an approval the tool's details without omp's labels where the title
    /// says it (a command, not "Command: …"), and without the reason and origin lines (<see cref="Note"/>).
    /// </summary>
    public string Preview => IsApproval ? ApprovalParts().Preview : Details;

    /// <summary>Why omp asks, as omp put it ("Reason: …", "Origin: MCP server tool"), under the preview.</summary>
    public string Note => IsApproval ? ApprovalParts().Note : "";
    public bool HasNote => Note.Length > 0;

    private (string Preview, string Note) ApprovalParts()
    {
        var notes = new List<string>();
        var rest = new List<string>();
        foreach (var line in Details.Split('\n'))
        {
            if (rest.Count == 0 && (line.StartsWith("Reason: ", StringComparison.Ordinal) || line.StartsWith("Origin: ", StringComparison.Ordinal)))
                notes.Add(line[(line.IndexOf(": ", StringComparison.Ordinal) + 2)..].Trim());
            else rest.Add(line);
        }
        var preview = string.Join('\n', rest).Trim();
        // One labelled value the title already names: the value alone (bash's "Command: …", edit's "File: …")
        foreach (var label in (string[])["Command: ", "File: ", "Path: "])
            if (preview.StartsWith(label, StringComparison.Ordinal) && !preview.Contains('\n'))
                preview = preview[label.Length..];
        return (preview, string.Join(" · ", notes.Where(n => n.Length > 0)));
    }

    /// <summary>Claude Code's wording with omp as the subject: "omp wants to run a command".</summary>
    public static string ApprovalTitle(string tool) => tool switch
    {
        "bash" => "omp wants to run a command",
        "edit" or "ast_edit" => "omp wants to make changes",
        "write" => "omp wants to write a file",
        "read" => "omp wants to read a file",
        "eval" => "omp wants to run code",
        "fetch" or "browser" => "omp wants to open a web page",
        "task" => "omp wants to start a subagent",
        _ => $"omp wants to use {ToolRowViewModel.DisplayNameOf(tool)}",
    };

    public string Placeholder => Model.Placeholder ?? "";
    public IReadOnlyList<DialogOptionViewModel> Options { get; }

    [ObservableProperty] private string _inputText;
    [ObservableProperty] private string _remaining = "";
    [ObservableProperty] private string _queueText = "";

    public bool HasDeadline => Model.Deadline is not null;

    /// <summary>Cosmetic countdown; the Core closes the dialog at the deadline whatever the UI shows.</summary>
    public void UpdateRemaining(DateTimeOffset now)
    {
        if (Model.Deadline is not { } d) return;
        var left = d - now;
        Remaining = left <= TimeSpan.Zero ? "closing…" : string.Create(CultureInfo.InvariantCulture, $"{Math.Ceiling(left.TotalSeconds):0}s left");
    }

    private async Task AnswerAsync(DialogAnswer a)
    {
        if (_answered) return;
        _answered = true;
        await _answer(Id, a);
    }

    [RelayCommand] private Task Allow() => AnswerAsync(new DialogAnswer.Value("Approve"));
    [RelayCommand] private Task Deny() => AnswerAsync(new DialogAnswer.Value("Deny"));

    /// <summary>A digit key: 1 / 2 answer an approval (Allow / Deny), 1–9 pick a choice. False when the digit means nothing here.</summary>
    public bool TryPick(int digit)
    {
        if (IsApproval && digit is 1 or 2)
        {
            (digit == 1 ? AllowCommand : DenyCommand).Execute(null);
            return true;
        }
        if (IsSelect && Options.FirstOrDefault(o => o.Number == digit) is { } option)
        {
            option.ChooseCommand.Execute(null);
            return true;
        }
        return false;
    }
    [RelayCommand] private Task Yes() => AnswerAsync(new DialogAnswer.Confirmed(true));
    [RelayCommand] private Task No() => AnswerAsync(new DialogAnswer.Confirmed(false));
    [RelayCommand] private Task Submit() => AnswerAsync(new DialogAnswer.Value(InputText));
    [RelayCommand] private Task Dismiss() => AnswerAsync(new DialogAnswer.Cancelled());
    internal Task Choose(string label) => AnswerAsync(new DialogAnswer.Value(label));
}

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
