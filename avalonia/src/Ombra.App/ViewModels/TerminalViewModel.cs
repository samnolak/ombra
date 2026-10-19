using CommunityToolkit.Mvvm.ComponentModel;
using Ombra.App.Localization;

namespace Ombra.App.ViewModels;

public enum TerminalKind { Shell, OmpTui }

/// <summary>
/// One terminal tab. The view owns the terminal control and its PTY process (ConPTY on Windows, a Unix PTY on
/// macOS/Linux, chosen by Porta.Pty at run time); this holds what to start and what happened to it.
/// </summary>
public sealed partial class TerminalViewModel(TerminalKind kind, string title, string file, IReadOnlyList<string> args,
    string? workingDirectory, IReadOnlyDictionary<string, string> environment) : ObservableObject
{
    public TerminalKind Kind { get; } = kind;
    public string File { get; } = file;
    public IReadOnlyList<string> Args { get; } = args;
    public string? WorkingDirectory { get; } = workingDirectory;
    public IReadOnlyDictionary<string, string> Environment { get; } = environment;

    [ObservableProperty] private string _title = title;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _hasExited;
    [ObservableProperty] private string _status = "";

    /// <summary>The process was launched once; a tab is never relaunched behind the user's back.</summary>
    public bool Started { get; set; }

    /// <summary>Held while the tab's process runs (strict network privacy's sign-in allowance for <c>omp login</c>).</summary>
    public IDisposable? Lease { get; init; }

    /// <summary>The tab's last lines (at most the count given), oldest first; set by the view that owns the terminal,
    /// null while there is none. UI thread only.</summary>
    public Func<int, IReadOnlyList<string>>? ReadLines { get; set; }

    public void OnExited(int code)
    {
        HasExited = true;
        Status = Loc.F("exited ({0})", code);
        Lease?.Dispose();
    }
}
