using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

/// <summary>Terminal panel: shells in the project folder and omp's terminal UI.</summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<TerminalViewModel> Terminals { get; } = [];

    [ObservableProperty] private bool _isTerminalOpen;
    [ObservableProperty] private TerminalViewModel? _selectedTerminal;

    /// <summary>The panel has no tab: it shows how to start one.</summary>
    public bool HasNoTerminals => Terminals.Count == 0;

    /// <summary>How to start omp's own UI in a terminal (from the client settings, through the network privacy checks);
    /// null when unknown (tests).</summary>
    public Func<string?, CancellationToken, Task<OmpGui.Rpc.OmpLaunchSpec>>? OmpTuiLaunch { get; init; }

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

    [RelayCommand]
    private void NewShell()
    {
        var (file, args) = DefaultShell();
        Add(new TerminalViewModel(TerminalKind.Shell, ShellName(file), file, args, ProjectDirectory(), new Dictionary<string, string>()));
    }

    [RelayCommand]
    private async Task OpenOmpTuiAsync()
    {
        if (OmpTuiLaunch is not { } launch) return;
        OmpGui.Rpc.OmpLaunchSpec spec;
        try { spec = await launch(ProjectDirectory(), Lifetime); }
        catch (OmpGui.ClientCore.Network.NetworkPrivacyException e)
        {
            ComposerMessage = "omp's terminal was not opened: " + e.Message;
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
        if (SelectedTerminal == t) SelectedTerminal = Terminals.Count == 0 ? null : Terminals[Math.Min(i, Terminals.Count - 1)];
        if (Terminals.Count == 0) IsTerminalOpen = false;
    }

    [RelayCommand]
    private void SelectTerminal(TerminalViewModel? t)
    {
        if (t is not null) SelectedTerminal = t;
    }

    private void Add(TerminalViewModel t)
    {
        Terminals.Add(t);
        OnPropertyChanged(nameof(HasNoTerminals));
        SelectedTerminal = t;
        IsTerminalOpen = true;
    }

    private string? ProjectDirectory() => _open.Last?.Cwd is { } cwd && Directory.Exists(cwd) ? cwd : null;

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
