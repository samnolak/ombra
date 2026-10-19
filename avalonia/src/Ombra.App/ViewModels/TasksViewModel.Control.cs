using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;

namespace Ombra.App.ViewModels;

/// <summary>A running subagent's controls: Stop (<c>cancel_subagent</c>) and Message (<c>steer_subagent</c>).</summary>
public sealed partial class SubagentRowViewModel
{
    /// <summary>What the user writes to this subagent; cleared once omp delivered it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    private string _messageText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStop))]
    private bool _isStopping;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    private bool _isSending;

    /// <summary>Why the last Stop or Message failed; shown on the row until the next attempt.</summary>
    [ObservableProperty] private string _actionError = "";
    [ObservableProperty] private bool _hasActionError;

    public bool CanStop => IsRunning && !IsStopping;
    public bool CanSend => IsRunning && !IsSending && !string.IsNullOrWhiteSpace(MessageText);

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanSend));
    }

    [RelayCommand]
    public async Task StopAsync()
    {
        if (!CanStop) return;
        IsStopping = true;
        SetActionError("");
        try
        {
            if (await _owner.CancelAsync(Id) is { } error) SetActionError(Loc.T("Could not stop it: ") + error);
        }
        finally
        {
            IsStopping = false;
        }
    }

    [RelayCommand]
    public async Task SendMessageAsync()
    {
        if (!CanSend) return;
        var text = MessageText.Trim();
        IsSending = true;
        SetActionError("");
        try
        {
            if (await _owner.SteerAsync(Id, text) is { } error) SetActionError(Loc.T("Could not send the message: ") + error);
            else if (MessageText.Trim() == text) MessageText = ""; // keep anything typed while it was on its way
        }
        finally
        {
            IsSending = false;
        }
    }

    private void SetActionError(string error)
    {
        ActionError = Ombra.App.Controls.VisibleText.FromUntrustedOutput(error);
        HasActionError = ActionError.Length > 0;
    }
}

public sealed partial class TasksViewModel
{
    /// <summary>Two or more subagents run: one button stops them all.</summary>
    [ObservableProperty] private bool _canStopAll;

    [RelayCommand]
    public Task StopAllAsync() => Task.WhenAll(Subagents.Where(r => r.CanStop).ToList().Select(r => r.StopAsync()));

    internal Task<string?> CancelAsync(string id) =>
        _owner?.CancelSubagentAsync(id) ?? Task.FromResult<string?>(Loc.T("omp is not running"));

    internal Task<string?> SteerAsync(string id, string message) =>
        _owner?.SteerSubagentAsync(id, message) ?? Task.FromResult<string?>(Loc.T("omp is not running"));
}
