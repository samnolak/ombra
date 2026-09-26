using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

public sealed partial class CommandSuggestionViewModel(string insert, string label, string? description, string? hint, string source) : ObservableObject
{
    /// <summary>Composer text after choosing it (ends with a space so arguments can follow).</summary>
    public string Insert { get; } = insert;
    public string Label { get; } = label;
    public string Description { get; } = description ?? "";
    public string Hint { get; } = hint ?? "";
    public bool HasHint => Hint.Length > 0;
    public string Source { get; } = source;
    [ObservableProperty] private bool _isSelected;
}

public sealed record TodoTaskViewModel(string Content, string Status)
{
    public string Mark => Status switch
    {
        "completed" or "done" => "✓",
        "in_progress" or "active" => "◐",
        "abandoned" or "cancelled" or "canceled" => "✗",
        _ => "○",
    };
    public bool IsDone => Status is "completed" or "done";
    public bool IsActive => Status is "in_progress" or "active";
    public bool IsDropped => Status is "abandoned" or "cancelled" or "canceled";
}

public sealed record TodoPhaseViewModel(string Name, IReadOnlyList<TodoTaskViewModel> Tasks)
{
    public bool HasName => Name.Length > 0;
}

/// <summary>Slash-command autocomplete (omp's own catalog), todo progress, context usage.</summary>
public sealed partial class MainViewModel
{
    private const int MaxSuggestions = 12;
    private IReadOnlyList<SlashCommand> _commands = [];

    public ObservableCollection<CommandSuggestionViewModel> CommandSuggestions { get; } = [];
    public ObservableCollection<TodoPhaseViewModel> TodoPhases { get; } = [];

    [ObservableProperty] private bool _isCommandMenuOpen;
    [ObservableProperty] private string _todoSummary = "";
    [ObservableProperty] private bool _hasTodos;
    /// <summary>Closed by default: one line with the task in progress and the count, so the list does not take the
    /// conversation's room (a small window had almost none left); open, the whole list.</summary>
    [ObservableProperty] private bool _isTodoExpanded;
    [ObservableProperty] private string _todoCurrent = "";
    [ObservableProperty] private string _todoProgress = "";
    [ObservableProperty] private bool _isTodoAllDone;
    private int _selectedSuggestion;

    /// <summary>Updates suggestions while the first word of the message is a slash command.</summary>
    private void UpdateCommandSuggestions()
    {
        var text = ComposerText;
        var list = new List<CommandSuggestionViewModel>();
        if (text.StartsWith('/') && !text.Contains('\n'))
        {
            var space = text.IndexOf(' ');
            if (space < 0)
            {
                var typed = text[1..];
                list.AddRange(SlashMenuCommands() // omp's catalog + its terminal-only commands, marked (MainViewModel.TerminalOnly.cs)
                    .Select(c => (c, rank: Rank(c, typed)))
                    .Where(x => x.rank >= 0)
                    .OrderBy(x => x.rank).ThenBy(x => x.c.Name, StringComparer.Ordinal)
                    .Take(MaxSuggestions)
                    .Select(x => new CommandSuggestionViewModel("/" + x.c.Name + " ", "/" + x.c.Name, x.c.Description, x.c.Hint, x.c.Source)));
            }
            else if (_commands.FirstOrDefault(c => string.Equals(c.Name, text[1..space], StringComparison.OrdinalIgnoreCase)) is { Subcommands.Count: > 0 } cmd
                     && !text[(space + 1)..].Contains(' '))
            {
                var typed = text[(space + 1)..];
                list.AddRange(cmd.Subcommands
                    .Where(sc => sc.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
                    .Take(MaxSuggestions)
                    .Select(sc => new CommandSuggestionViewModel($"/{cmd.Name} {sc.Name} ", $"/{cmd.Name} {sc.Name}", sc.Description, sc.Hint, cmd.Source)));
            }
        }
        // An exact, complete command with nothing more to offer: no menu (Enter sends it).
        if (list.Count == 1 && list[0].Insert.TrimEnd() == text.TrimEnd()) list.Clear();
        CommandSuggestions.Clear();
        foreach (var s in list) CommandSuggestions.Add(s);
        _selectedSuggestion = 0;
        if (list.Count > 0) list[0].IsSelected = true;
        IsCommandMenuOpen = list.Count > 0;
    }

    /// <summary>0: name starts with the text; 1: an alias does; 2: the name contains it; -1: no match.</summary>
    private static int Rank(SlashCommand c, string typed)
    {
        if (c.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) return 0;
        if (c.Aliases.Any(a => a.StartsWith(typed, StringComparison.OrdinalIgnoreCase))) return 1;
        return typed.Length > 1 && c.Name.Contains(typed, StringComparison.OrdinalIgnoreCase) ? 2 : -1;
    }

    /// <summary>Keyboard in the menu: moves the selection; true when handled.</summary>
    public bool MoveSuggestion(int delta)
    {
        if (!IsCommandMenuOpen || CommandSuggestions.Count == 0) return false;
        CommandSuggestions[_selectedSuggestion].IsSelected = false;
        _selectedSuggestion = (_selectedSuggestion + delta + CommandSuggestions.Count) % CommandSuggestions.Count;
        CommandSuggestions[_selectedSuggestion].IsSelected = true;
        return true;
    }

    /// <summary>Puts the selected command into the composer; true when a menu was open.</summary>
    /// <summary>
    /// Completes the highlighted command (Tab, Enter). With <paramref name="enter"/>, a command that is already typed
    /// out in full is not completed again: false lets Enter send it (typing "/context" then Enter runs it).
    /// </summary>
    public bool AcceptSuggestion(bool enter = false)
    {
        if (!IsCommandMenuOpen || CommandSuggestions.Count == 0) return false;
        var chosen = CommandSuggestions[_selectedSuggestion];
        if (enter && chosen.Insert.TrimEnd() == ComposerText.TrimEnd()) return false;
        ChooseSuggestion(chosen);
        return true;
    }

    public void CloseCommandMenu() => IsCommandMenuOpen = false;

    [RelayCommand]
    private void ChooseSuggestion(CommandSuggestionViewModel? s)
    {
        if (s is null) return;
        ComposerText = s.Insert;
        UpdateCommandSuggestions(); // offers subcommands next, if any
        CaretToEndRequested?.Invoke();
    }

    /// <summary>Raised when the composer caret should move to the end of the text.</summary>
    public event Action? CaretToEndRequested;

    private void ApplyCommandsAndProgress(SessionSnapshot s)
    {
        if (!ReferenceEquals(s.Commands, _commands))
        {
            _commands = s.Commands;
            if (IsCommandMenuOpen) UpdateCommandSuggestions();
        }
        var phases = s.Todos.Where(p => p.Tasks.Count > 0).ToList();
        // Done of what is still to do, dropped tasks left out (the Plan pane and the Views menu agree)
        var (done, total) = PlanViewModel.Count(phases.SelectMany(p => p.Tasks));
        var summary = total == 0 ? "" : string.Create(CultureInfo.InvariantCulture, $"Tasks {done}/{total}");
        if (summary != TodoSummary || TodoPhases.Count != phases.Count || !TodoPhases.Select(p => p.Tasks.Count).SequenceEqual(phases.Select(p => p.Tasks.Count)) || TodoChanged(phases))
        {
            TodoPhases.Clear();
            foreach (var p in phases) TodoPhases.Add(new TodoPhaseViewModel(phases.Count > 1 ? p.Name : "", [.. p.Tasks.Select(t => new TodoTaskViewModel(t.Content, t.Status))]));
            TodoSummary = summary;
            HasTodos = phases.Count > 0; // only empty phases were dropped above; all tasks dropped still shows the plan
            var tasks = phases.SelectMany(p => p.Tasks).ToList();
            var current = tasks.FirstOrDefault(t => t.Status is "in_progress" or "active") ?? tasks.FirstOrDefault(t => t.Status is "pending" or "todo" or "")
                ?? tasks.FirstOrDefault(t => t.Status is "blocked"); // a blocked task is open work, not "all done"
            IsTodoAllDone = HasTodos && current is null;
            TodoCurrent = current?.Content ?? "All tasks done";
            TodoProgress = string.Create(CultureInfo.InvariantCulture, $"{done}/{total}");
        }
    }

    private bool TodoChanged(List<TodoPhase> phases) =>
        !TodoPhases.SelectMany(p => p.Tasks).Select(t => (t.Content, t.Status))
            .SequenceEqual(phases.SelectMany(p => p.Tasks).Select(t => (t.Content, t.Status)));

    [RelayCommand]
    private void ToggleTodos() => IsTodoExpanded = !IsTodoExpanded;

    /// <summary>Asks the view to put the keyboard in the message box (after a menu choice).</summary>
    public event Action? FocusComposerRequested;

    /// <summary>"+" → Commands: a "/" in an empty message box opens omp's command list.</summary>
    [RelayCommand]
    private void StartSlashCommand()
    {
        if (string.IsNullOrWhiteSpace(ComposerText))
        {
            ComposerText = "/";
            ComposerCaretIndex = 1;
        }
        FocusComposerRequested?.Invoke();
    }
}
