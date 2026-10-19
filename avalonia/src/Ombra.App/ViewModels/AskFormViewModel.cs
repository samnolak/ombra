using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

/// <summary>
/// omp's ask form (<c>extension_ui_request</c> method <c>ask</c>): every question of one ask tool call, one tab each,
/// answered together. A question takes one option (or many when it is multi-select) and always free text of the
/// user's own; the answers go to omp in the questions' order (rpc-mode.ts <c>parseAskDialogResponse</c>).
/// </summary>
public sealed partial class AskFormViewModel : ObservableObject
{
    private readonly Func<DialogAnswer, Task> _answer;

    public AskFormViewModel(IReadOnlyList<AskQuestion> questions, Func<DialogAnswer, Task> answer)
    {
        _answer = answer;
        Questions = [.. questions.Select((q, i) => new AskQuestionViewModel(q, i + 1, this))];
        _current = Questions[0];
        _current.IsCurrent = true;
    }

    public IReadOnlyList<AskQuestionViewModel> Questions { get; }

    /// <summary>One question needs no tabs.</summary>
    public bool HasTabs => Questions.Count > 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrevious), nameof(HasNext), nameof(Position))]
    private AskQuestionViewModel _current;

    partial void OnCurrentChanged(AskQuestionViewModel? oldValue, AskQuestionViewModel newValue)
    {
        if (oldValue is not null) oldValue.IsCurrent = false;
        newValue.IsCurrent = true;
    }

    public bool HasPrevious => Current.Number > 1;
    public bool HasNext => Current.Number < Questions.Count;

    /// <summary>"Question 2 of 3"; empty for a single question.</summary>
    public string Position => HasTabs ? Loc.F("Question {0} of {1}", Current.Number, Questions.Count) : "";

    /// <summary>Why the answers were not sent: a question still needs one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _error = "";

    public bool HasError => Error.Length > 0;

    [RelayCommand] private void Show(AskQuestionViewModel question) => Current = question;
    [RelayCommand] private void Next() { if (HasNext) Current = Questions[Current.Number]; }
    [RelayCommand] private void Previous() { if (HasPrevious) Current = Questions[Current.Number - 2]; }

    /// <summary>
    /// Sends every answer at once. A one-choice question needs an option or the user's own text (omp takes an empty
    /// one as no answer); a multi-select may be left empty ("none of these").
    /// </summary>
    [RelayCommand]
    private Task Submit()
    {
        if (Questions.FirstOrDefault(q => !q.IsAnswered && !q.Multi) is { } open)
        {
            Current = open;
            Error = HasTabs ? Loc.F("Answer “{0}” too, or type your own answer.", open.TabLabel) : Loc.T("Choose an answer, or type your own.");
            return Task.CompletedTask;
        }
        Error = "";
        return _answer(new DialogAnswer.Asked([.. Questions.Select(q => q.ToAnswer())]));
    }

    /// <summary>Closes without answering; omp treats it as no answer and the tool stops.</summary>
    [RelayCommand] private Task Cancel() => _answer(new DialogAnswer.Cancelled());

    /// <summary>A digit key picks that option of the question shown (1–9); false when there is none.</summary>
    public bool TryPick(int digit)
    {
        if (Current.Options.FirstOrDefault(o => o.Number == digit) is not { } option) return false;
        option.ChooseCommand.Execute(null);
        return true;
    }

    /// <summary>A one-choice question answered by a click or a digit moves on to the next question.</summary>
    internal void Chosen(AskQuestionViewModel question)
    {
        Error = "";
        if (!question.Multi && ReferenceEquals(question, Current) && HasNext) Next();
    }
}

/// <summary>One question of the ask form: its tab, its options and the user's own answer.</summary>
public sealed partial class AskQuestionViewModel : ObservableObject
{
    private readonly AskQuestion _question;
    private readonly AskFormViewModel _form;

    public AskQuestionViewModel(AskQuestion question, int number, AskFormViewModel form)
    {
        _question = question;
        _form = form;
        Number = number;
        Options = [.. question.Options.Select((o, i) => new AskChoiceViewModel(o, i == question.Recommended, i + 1, this))];
    }

    /// <summary>1-based place in the form.</summary>
    public int Number { get; }
    public string Id => _question.Id;
    public string Question => _question.Question;
    public bool Multi => _question.Multi;
    public IReadOnlyList<AskChoiceViewModel> Options { get; }

    /// <summary>The tab: the tool's short header ("Auth", "Scope"), else the question's number.</summary>
    public string TabLabel => _question.Header ?? Loc.F("Question {0}", Number);

    /// <summary>"Choose one" / "Choose any that apply".</summary>
    public string Hint => Multi ? Loc.T("Choose any that apply") : Loc.T("Choose one");

    [ObservableProperty] private bool _isCurrent;

    /// <summary>The user's own answer (omp's "Other"): instead of an option, or besides them in a multi-select.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnswered))]
    private string _customText = "";

    partial void OnCustomTextChanged(string value)
    {
        // One choice: the own answer replaces the option (omp takes one answer, not both)
        if (!Multi && value.Trim().Length > 0)
            foreach (var o in Options.Where(o => o.IsChecked)) o.IsChecked = false;
        if (value.Trim().Length > 0) _form.Error = "";
    }

    public bool IsAnswered => Options.Any(o => o.IsChecked) || CustomText.Trim().Length > 0;

    internal void Toggled(AskChoiceViewModel option)
    {
        if (option.IsChecked && !Multi)
        {
            foreach (var other in Options.Where(o => o != option && o.IsChecked)) other.IsChecked = false;
            CustomText = "";
        }
        OnPropertyChanged(nameof(IsAnswered));
    }

    internal void Chosen() => _form.Chosen(this);

    internal AskAnswer ToAnswer() =>
        new(Id, [.. Options.Where(o => o.IsChecked).Select(o => o.Label)], CustomText.Trim() is { Length: > 0 } own ? own : null);
}

/// <summary>An option of an ask question: a check box for a multi-select, a single choice otherwise.</summary>
public sealed partial class AskChoiceViewModel(AskOption option, bool recommended, int number, AskQuestionViewModel owner) : ObservableObject
{
    /// <summary>1–9: the digit that picks it; 0 past nine.</summary>
    public int Number { get; } = number <= 9 ? number : 0;
    public bool HasNumber => Number > 0;
    public string Label => option.Label;
    public string? Description => option.Description;
    public bool HasDescription => option.Description is not null;
    /// <summary>The tool's preview of what the option means (code, a layout), shown as it is.</summary>
    public string? Preview => option.Preview;
    public bool HasPreview => option.Preview is not null;
    /// <summary>omp's suggestion: a badge, never preselected.</summary>
    public bool Recommended { get; } = recommended;
    public bool Multi => owner.Multi;

    [ObservableProperty] private bool _isChecked;

    partial void OnIsCheckedChanged(bool value) => owner.Toggled(this);

    /// <summary>A click or its digit: a multi-select toggles it; a single choice picks it.</summary>
    [RelayCommand]
    private void Choose()
    {
        IsChecked = !Multi || !IsChecked;
        owner.Chosen();
    }
}
