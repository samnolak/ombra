using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

/// <summary>Terminal panel, under the chat and the right panel: shells in the project folder and omp's terminal UI.</summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<TerminalViewModel> Terminals { get; } = [];

    [ObservableProperty] private bool _isTerminalOpen;
    [ObservableProperty] private TerminalViewModel? _selectedTerminal;

    /// <summary>The panel has no tab: it shows how to start one.</summary>
    public bool HasNoTerminals => Terminals.Count == 0;

    /// <summary>How to start omp's own UI in a terminal (from the client settings, through the network privacy checks);
    /// null when unknown (tests).</summary>
    public Func<string?, CancellationToken, Task<Ombra.Rpc.OmpLaunchSpec>>? OmpTuiLaunch { get; init; }

    partial void OnSelectedTerminalChanged(TerminalViewModel? oldValue, TerminalViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
    }

    [RelayCommand]
    private void ToggleTerminal()
    {
        IsTerminalOpen = !IsTerminalOpen;
        if (IsTerminalOpen && Terminals.Count == 0) NewShell();
    }

    [RelayCommand(CanExecute = nameof(CanMutateSession))]
    private void NewShell() => NewShellIn(ProjectDirectory());

    /// <summary>A shell in <paramref name="folder"/> (a path's menu: Open in terminal; null: the shell's own start).</summary>
    internal void NewShellIn(string? folder)
    {
        if (IsReadOnly) return;
        var (file, args) = DefaultShell();
        Add(new TerminalViewModel(TerminalKind.Shell, ShellName(file), file, args, folder, new Dictionary<string, string>()));
    }

    [RelayCommand(CanExecute = nameof(CanMutateSession))]
    private async Task OpenOmpTuiAsync()
    {
        if (IsReadOnly) return;
        if (OmpTuiLaunch is not { } launch) return;
        Ombra.Rpc.OmpLaunchSpec spec;
        try { spec = await launch(ProjectDirectory(), Lifetime); }
        catch (Ombra.ClientCore.Network.NetworkPrivacyException e)
        {
            ComposerMessage = Loc.F("omp's terminal was not opened: {0}", e.Message);
            return;
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { return; }
        // A variable the settings remove for omp (null) is passed empty: the terminal library can only add variables,
        // and an empty value reads as unset to omp (so a key hidden from omp is not inherited by its TUI either).
        var env = spec.Environment?.ToDictionary(kv => kv.Key, kv => kv.Value ?? "") ?? [];
        Add(new TerminalViewModel(TerminalKind.OmpTui, "omp", spec.FileName, spec.Arguments, spec.WorkingDirectory ?? ProjectDirectory(), env));
    }

    [RelayCommand]
    private void CloseTerminal(TerminalViewModel? t)
    {
        if (t is null) return;
        var i = Terminals.IndexOf(t);
        Terminals.Remove(t); // the view kills its process
        t.Lease?.Dispose();
        OnPropertyChanged(nameof(HasNoTerminals));
        OnTerminalsChanged();
        if (SelectedTerminal == t) SelectedTerminal = Terminals.Count == 0 ? null : Terminals[Math.Min(i, Terminals.Count - 1)];
        if (Terminals.Count == 0) IsTerminalOpen = false;
    }

    [RelayCommand]
    private void SelectTerminal(TerminalViewModel? t)
    {
        if (t is not null) SelectedTerminal = t;
    }

    /// <summary>Shows the terminal panel, on <paramref name="t"/> when given (the summary's background terminals); with
    /// no tab yet a shell starts (the new-tab page's Terminal).</summary>
    [RelayCommand]
    private void ShowTerminal(TerminalViewModel? t)
    {
        if (t is not null && Terminals.Contains(t)) SelectedTerminal = t;
        IsTerminalOpen = true;
        if (Terminals.Count == 0) NewShell();
    }

    private void OnTerminalsChanged()
    {
        OnPropertyChanged(nameof(HasLiveTerminals));
        OnPropertyChanged(nameof(HasNoBackgroundWork));
    }

    private void Add(TerminalViewModel t)
    {
        Terminals.Add(t);
        OnPropertyChanged(nameof(HasNoTerminals));
        t.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TerminalViewModel.HasExited)) OnTerminalsChanged(); };
        OnTerminalsChanged();
        SelectedTerminal = t;
        IsTerminalOpen = true;
    }

    private string? ProjectDirectory() => !IsReadOnly && _open.Last?.Cwd is { } cwd && Directory.Exists(cwd) ? cwd : null;

    /// <summary>
    /// The user's shell: $SHELL (macOS, Linux), else %ComSpec% (Windows), else sh. $SHELL counts only when it names an
    /// existing file: started from Git Bash on Windows it holds an MSYS path such as /usr/bin/bash.
    /// </summary>
    internal static (string File, string[] Args) DefaultShell()
    {
        if (Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } shell && File.Exists(shell)) return (shell, ["-l"]);
        if (Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } comspec) return (comspec, []);
        return ("/bin/sh", []);
    }

    private static string ShellName(string file) => Path.GetFileNameWithoutExtension(file);
}
