using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

public sealed partial class CommandSuggestionViewModel(string insert, string label, string? description, string? hint, string source, bool terminalOnly = false) : ObservableObject
{
    /// <summary>Composer text after choosing it (ends with a space so arguments can follow).</summary>
    public string Insert { get; } = insert;
    public string Label { get; } = label;
    public string Description { get; } = description ?? "";
    public string Hint { get; } = hint ?? "";
    public bool HasHint => Hint.Length > 0;

    /// <summary>Where a command comes from, when that tells the user something (a skill, an MCP prompt…); omp's own
    /// commands and the window's have none.</summary>
    public string Tag { get; } = source switch
    {
        "skill" => Loc.T("Skill"),
        "extension" => Loc.T("Extension"),
        "custom" => Loc.T("Custom"),
        "mcp_prompt" => "MCP",
        "file" => Loc.T("Prompt file"),
        _ => "",
    };
    public bool HasTag => Tag.Length > 0;

    /// <summary>Runs only in omp's terminal UI: listed last, under a "Terminal only" heading.</summary>
    public bool IsTerminalOnly { get; } = terminalOnly;

    /// <summary>The first terminal-only entry carries the heading.</summary>
    public bool StartsTerminalGroup { get; init; }

    /// <summary>A file or folder of the project in the "@" menu; <see cref="Insert"/> is its <c>@path</c> reference.</summary>
    public bool IsMention { get; init; }
    public bool IsFolder { get; init; }
    public bool IsFile => IsMention && !IsFolder;
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
    private IReadOnlyList<SlashCommand> _commands = [];
    private IReadOnlyList<TodoPhase>? _appliedTodos;

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

    /// <summary>The selected entry of the slash menu (the view keeps it scrolled into view).</summary>
    public int SelectedSuggestionIndex => _selectedSuggestion;

    /// <summary>
    /// Updates suggestions while the first word of the message is a slash command. What runs in this window comes
    /// first; what runs only in omp's terminal UI follows under its own heading. The menu scrolls: every match is listed.
    /// While an "@" reference is being completed the menu lists files instead (MainViewModel.Mentions.cs).
    /// </summary>
    private void UpdateCommandSuggestions()
    {
        if (_mention is not null)
        {
            // The caret moves after the text does: the "@" menu looks again once the box has settled, and keeps its
            // entries meanwhile (clearing them on every key made it flicker)
            PostMentionCheck();
            return;
        }
        UpdateSlashSuggestions();
    }

    /// <summary>A <c>/word</c> typed after other text, ending at the caret: only a skill can go there (omp runs a
    /// <c>/skill:</c> token anywhere in a message, other commands only at its start). Start, end of the word, query.</summary>
    internal sealed record SkillToken(int Start, int End, string Query);

    private SkillToken? _skillToken;

    internal static SkillToken? FindSkillToken(string text, int caret)
    {
        if (caret < 2 || caret > text.Length || !SkillTokensAllowed(text)) return null;
        var at = caret - 1;
        while (text[at] != '/')
        {
            if (char.IsWhiteSpace(text[at]) || --at < 0) return null;
        }
        // A word of its own after other text (at the message's start the slash menu lists every command)
        if (at == 0 || !char.IsWhiteSpace(text[at - 1]) || string.IsNullOrWhiteSpace(text[..at])) return null;
        var end = caret;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
        return new SkillToken(at, end, text[(at + 1)..caret]);
    }

    // omp reads /skill: tokens in a message unless it is another slash command or a !/$ shell line (SkillTokens)
    private static bool SkillTokensAllowed(string text)
    {
        var t = text.TrimStart();
        return t.StartsWith("/skill:", StringComparison.Ordinal) || !(t.StartsWith('/') || t.StartsWith('!') || t.StartsWith('$'));
    }

    private void UpdateSlashSuggestions()
    {
        var text = ComposerText;
        var list = new List<CommandSuggestionViewModel>();
        _skillToken = text.StartsWith('/') && !text.Contains('\n') && !text.Contains(' ') ? null : FindSkillToken(text, ComposerCaretIndex);
        if (_skillToken is { } skill)
        {
            // Mid-message: the skills whose name (with or without "skill:") starts with what was typed
            var typed = skill.Query.StartsWith("skill:", StringComparison.OrdinalIgnoreCase) ? skill.Query[6..] : skill.Query;
            list.AddRange(SlashMenuCommands()
                .Where(c => c.Source == "skill" && c.Name.StartsWith("skill:", StringComparison.Ordinal)
                    && c.Name[6..].StartsWith(typed, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c.Name, StringComparer.Ordinal)
                .Select(c => new CommandSuggestionViewModel("/" + c.Name + " ", "/" + c.Name, c.Description, c.Hint, c.Source)));
            if (list.Count == 1 && list[0].Label == "/" + skill.Query) list.Clear(); // typed out in full
            CommandSuggestions.Clear();
            foreach (var s in list) CommandSuggestions.Add(s);
            _selectedSuggestion = 0;
            if (list.Count > 0) list[0].IsSelected = true;
            ShowNoCommandMatch = false; // a path ("see /tmp") is no command typed wrong
            IsCommandMenuOpen = list.Count > 0;
            return;
        }
        if (text.StartsWith('/') && !text.Contains('\n'))
        {
            var space = text.IndexOf(' ');
            if (space < 0)
            {
                var typed = text[1..];
                var first = true;
                list.AddRange(SlashMenuCommands() // omp's catalog + its terminal-only commands (MainViewModel.TerminalOnly.cs)
                    .Select(c => (c, rank: Rank(c, typed), tui: c.Source == TerminalOnlySource))
                    .Where(x => x.rank >= 0)
                    .OrderBy(x => x.tui).ThenBy(x => x.rank).ThenBy(x => x.c.Name, StringComparer.Ordinal)
                    .Select(x =>
                    {
                        var s = new CommandSuggestionViewModel("/" + x.c.Name + " ", "/" + x.c.Name, x.c.Description, x.c.Hint, x.c.Source, x.tui)
                        {
                            StartsTerminalGroup = x.tui && first,
                        };
                        if (x.tui) first = false;
                        return s;
                    }));
            }
            else if (_commands.FirstOrDefault(c => string.Equals(c.Name, text[1..space], StringComparison.OrdinalIgnoreCase)) is { Subcommands.Count: > 0 } cmd
                     && !text[(space + 1)..].Contains(' '))
            {
                var typed = text[(space + 1)..];
                list.AddRange(cmd.Subcommands
                    .Where(sc => sc.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
                    .Select(sc => new CommandSuggestionViewModel($"/{cmd.Name} {sc.Name} ", $"/{cmd.Name} {sc.Name}", sc.Description, sc.Hint, cmd.Source)));
            }
        }
        // A slash word that matches nothing: the menu stays, saying so (closing it read as the menu having broken)
        var noMatch = list.Count == 0 && text.Length > 1 && text.StartsWith('/') && !text.Contains(' ') && !text.Contains('\n');
        // An exact, complete command with nothing more to offer: no menu (Enter sends it).
        if (list.Count == 1 && list[0].Insert.TrimEnd() == text.TrimEnd()) list.Clear();
        CommandSuggestions.Clear();
        foreach (var s in list) CommandSuggestions.Add(s);
        _selectedSuggestion = 0;
        if (list.Count > 0) list[0].IsSelected = true;
        ShowNoCommandMatch = noMatch;
        NoMatchText = Loc.T("No matching commands");
        IsCommandMenuOpen = list.Count > 0 || noMatch;
    }

    /// <summary>The menu's "No matching commands" (or files) row.</summary>
    [ObservableProperty] private bool _showNoCommandMatch;

    [ObservableProperty] private string _noMatchText = Loc.T("No matching commands");

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

    /// <summary>
    /// Completes the highlighted command or file (Tab, Enter). With <paramref name="enter"/>, a command that is already
    /// typed out in full is not completed again: false lets Enter send it (typing "/context" then Enter runs it).
    /// </summary>
    public bool AcceptSuggestion(bool enter = false)
    {
        if (!IsCommandMenuOpen || CommandSuggestions.Count == 0) return false;
        var chosen = CommandSuggestions[_selectedSuggestion];
        if (enter && !chosen.IsMention && chosen.Insert.TrimEnd() == ComposerText.TrimEnd()) return false;
        ChooseSuggestion(chosen);
        return true;
    }

    /// <summary>Esc: closes the menu only. An "@" reference dismissed this way stays closed while it is typed on.</summary>
    public void CloseCommandMenu()
    {
        DismissMention();
        IsCommandMenuOpen = false;
    }

    [RelayCommand]
    private void ChooseSuggestion(CommandSuggestionViewModel? s)
    {
        if (s is null) return;
        if (s.IsMention)
        {
            InsertMention(s);
            return;
        }
        if (_skillToken is { } token && token.End <= ComposerText.Length)
        {
            // A skill after other text: in place of the word typed, the rest of the message kept
            var text = ComposerText;
            var rest = text[token.End..].TrimStart(' ');
            ComposerText = text[..token.Start] + s.Insert + rest;
            ComposerCaretIndex = token.Start + s.Insert.Length;
            _skillToken = null;
            IsCommandMenuOpen = false;
            return;
        }
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
        // Core keeps the same list until omp reports another one: nothing to compare on most applies
        if (ReferenceEquals(s.Todos, _appliedTodos)) return;
        _appliedTodos = s.Todos;
        var phases = s.Todos.Where(p => p.Tasks.Count > 0).ToList();
        // Done of what is still to do, dropped tasks left out (the Plan pane and the ⋯ menu agree)
        var (done, total) = PlanViewModel.Count(phases.SelectMany(p => p.Tasks));
        var summary = total == 0 ? "" : Loc.F("Tasks {0}/{1}", done, total);
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
            TodoCurrent = current?.Content ?? Loc.T("All tasks done");
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

    /// <summary>"+" → Commands and skills: a "/" in an empty message box opens omp's command list; after what is
    /// written (an image, text) a " /" at the end lists the skills that can go with it.</summary>
    [RelayCommand]
    private void StartSlashCommand()
    {
        if (string.IsNullOrWhiteSpace(ComposerText))
        {
            ComposerText = "/";
            ComposerCaretIndex = 1;
        }
        else if (SkillTokensAllowed(ComposerText))
        {
            var text = ComposerText.TrimEnd(' ');
            ComposerText = text + (text.EndsWith('\n') ? "/" : " /");
            ComposerCaretIndex = ComposerText.Length;
            UpdateSlashSuggestions(); // the caret is where the "/" is already: no wait for the view
        }
        FocusComposerRequested?.Invoke();
    }
}
