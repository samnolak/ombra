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
    public bool HasDetails => Details.Length > 0;
    public string? Message => Model.Message;
    public bool HasMessage => !string.IsNullOrEmpty(Model.Message);
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

    [RelayCommand] private Task Choose() => owner.Choose(option.Label);
}
