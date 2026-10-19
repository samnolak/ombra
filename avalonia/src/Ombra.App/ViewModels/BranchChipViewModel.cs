using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;

namespace Ombra.App.ViewModels;

/// <summary>
/// The header's branch chip (Claude Code shows the session's branch and worktree the same way): the project folder's
/// branch, marked when the folder is a linked worktree; hidden outside git. Read off the UI thread when the project
/// changes, a reply ends, the window comes back to the front, or a page changed the checkout.
/// </summary>
public sealed partial class BranchChipViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private int _requests;
    private bool _wasRunning;

    public BranchChipViewModel(MainViewModel main)
    {
        _main = main;
        main.PropertyChanged += OnMainChanged;
        _wasRunning = main.IsRunning;
        Refresh(); // made on first use, possibly after the project is known
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible), nameof(Tooltip), nameof(AccessibleName))]
    private string? _branch;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tooltip), nameof(AccessibleName))]
    private bool _isWorktree;

    /// <summary>HEAD is not on a branch: the label is a commit.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tooltip), nameof(AccessibleName), nameof(CopyLabel))]
    private bool _isDetached;

    public string CopyLabel => IsDetached ? Loc.T("Copy commit") : Loc.T("Copy branch name");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    private string? _mainCheckout;

    public bool IsVisible => Branch is not null;

    public bool CanCopy => Branch is not null;

    public string Tooltip => Branch is null ? "" : (IsDetached ? Loc.F("No branch: at commit {0}", Branch) : Loc.F("Branch {0}", Branch))
        + (IsWorktree ? MainCheckout is { } m ? "\n" + Loc.F("In a worktree of {0}", GitProbe.Tilde(m)) : "\n" + Loc.T("In a worktree") : "");

    public string AccessibleName => Branch is null ? Loc.T("Branch") : (IsDetached ? Loc.F("Commit {0}", Branch) : Loc.F("Branch {0}", Branch)) + (IsWorktree ? Loc.T(", worktree") : "");

    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.ProjectPath):
                Refresh();
                break;
            case nameof(MainViewModel.Phase):
                // A reply may have switched branches or made a worktree (git in bash): read again when it ends.
                var running = _main.IsRunning;
                if (_wasRunning && !running) Refresh();
                _wasRunning = running;
                break;
        }
    }

    /// <summary>Reads the branch again; overlapping requests collapse into one more read.</summary>
    public void Refresh()
    {
        if (Interlocked.Increment(ref _requests) != 1) return;
        if (_main.IsClosing)
        {
            Interlocked.Exchange(ref _requests, 0);
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var seen = Volatile.Read(ref _requests);
                    await ReadAsync().ConfigureAwait(false);
                    if (Interlocked.CompareExchange(ref _requests, 0, seen) == seen) return;
                }
            }
            catch (OperationCanceledException) { Interlocked.Exchange(ref _requests, 0); }
        });
    }

    /// <summary>For tests and callers that wait: one read now.</summary>
    public Task RefreshNowAsync() => ReadAsync();

    private async Task ReadAsync()
    {
        var ct = _main.Lifetime;
        ct.ThrowIfCancellationRequested();
        var folder = await _main.UiDispatcher.InvokeAsync(() => _main.ProjectFolder, DispatcherPriority.Normal, ct);
        ct.ThrowIfCancellationRequested();
        var head = folder is null ? null : (await GitProbe.ReadHeadAsync(_main.RunTool, folder, ct).ConfigureAwait(false)).Head;
        ct.ThrowIfCancellationRequested();
        await _main.UiDispatcher.InvokeAsync(() =>
        {
            if (!_main.IsClosing) Set(head);
        }, DispatcherPriority.Normal, ct);
    }

    private void Set(GitHead? head)
    {
        Branch = head?.Label;
        IsDetached = head is { Branch: null, ShortCommit: not null };
        IsWorktree = head?.IsLinkedWorktree == true;
        MainCheckout = head?.MainCheckout;
    }

    [RelayCommand]
    private void CopyBranch()
    {
        if (Branch is { } b) _main.CopyText(b);
    }

    [RelayCommand]
    private async Task NewWorktreeAsync()
    {
        _main.GitSettings.StartNewWorktree();
        await _main.OpenSettingsAtAsync("git");
    }

    [RelayCommand]
    private Task OpenGitSettings() => _main.OpenSettingsAtAsync("git");
}
