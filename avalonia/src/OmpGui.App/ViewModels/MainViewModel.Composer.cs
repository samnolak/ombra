using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmpGui.App.Services;
using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

public sealed partial class AttachmentViewModel(ImageAttachment model, MainViewModel owner) : ObservableObject, IDisposable
{
    public ImageAttachment Model { get; } = model;
    public string Name => Model.Name;
    public string Size => Model.Data.Length >= 1024 ? $"{Model.Data.Length / 1024} KB" : $"{Model.Data.Length} B";
    public Bitmap? Thumbnail { get; } = Decode(model.Data);

    [RelayCommand] private void Remove() => owner.RemoveAttachment(this);

    private static Bitmap? Decode(byte[] data)
    {
        try
        {
            using var s = new MemoryStream(data);
            return Bitmap.DecodeToHeight(s, 48);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    public void Dispose() => Thumbnail?.Dispose();
}

public sealed record QueuedItemViewModel(QueuedMessage Model)
{
    public string Text => Model.Text;
    public string Kind => Model.Kind == QueueKind.Steer ? "steer" : "queued";
    public string Tooltip => Model.Kind == QueueKind.Steer
        ? "Reaches the agent at its next step"
        : "Sent when the agent finishes its current work";
}

/// <summary>Composer: attachments, file references, messages while the agent works.</summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<AttachmentViewModel> Attachments { get; } = [];
    public ObservableCollection<QueuedItemViewModel> QueuedMessages { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasComposerMessage))]
    private string _composerMessage = "";

    public bool HasComposerMessage => ComposerMessage.Length > 0;
    public bool HasAttachments => Attachments.Count > 0;
    public bool HasQueued => QueuedMessages.Count > 0;
    public bool CanQueue => IsRunning && Phase == SessionPhase.Running && HasContent;
    private bool HasContent => !string.IsNullOrWhiteSpace(ComposerText) || Attachments.Count > 0 || Preview.HasAnnotations;

    /// <summary>Raised when the user asked to attach images; the view shows the platform file picker.</summary>
    public event Func<Task<IReadOnlyList<string>>>? PickImagesRequested;

    partial void OnComposerTextChanged(string value)
    {
        UpdateCommandSuggestions();
        OnPropertyChanged(nameof(CanQueue));
        SteerCommand.NotifyCanExecuteChanged();
    }

    public bool TryAddImage(ImageAttachment? image, string source)
    {
        if (image is null)
        {
            ComposerMessage = $"{source}: not an image omp can take, or too large even after scaling.";
            return false;
        }
        if (Attachments.Count >= ImageAttachments.MaxImages)
        {
            ComposerMessage = $"At most {ImageAttachments.MaxImages} images per message (omp takes one message of up to 1 MiB).";
            return false;
        }
        Attachments.Add(new AttachmentViewModel(image, this));
        ComposerMessage = "";
        AttachmentsChanged();
        return true;
    }

    /// <summary>Paths of dropped or pasted files that are not images go into the message as references.</summary>
    public void InsertPaths(IEnumerable<string> paths)
    {
        var text = string.Join(" ", paths.Select(p => p.Contains(' ') ? $"\"{p}\"" : p));
        if (text.Length == 0) return;
        ComposerText = ComposerText.Length == 0 || ComposerText.EndsWith(' ') || ComposerText.EndsWith('\n') ? ComposerText + text : ComposerText + " " + text;
    }

    /// <summary>Files dropped or pasted: images become attachments, everything else a path reference.</summary>
    public async Task AddFilesAsync(IReadOnlyList<string> paths)
    {
        var others = new List<string>();
        foreach (var path in paths)
        {
            if (!ImageAttachments.IsImagePath(path))
            {
                others.Add(path);
                continue;
            }
            byte[] data;
            try { data = await File.ReadAllBytesAsync(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                ComposerMessage = $"Could not read {Path.GetFileName(path)}: {e.Message}";
                continue;
            }
            var image = await Task.Run(() => ImageAttachments.FromBytes(Path.GetFileName(path), data));
            TryAddImage(image, Path.GetFileName(path));
        }
        InsertPaths(others);
    }

    internal void RemoveAttachment(AttachmentViewModel a)
    {
        Attachments.Remove(a);
        a.Dispose();
        AttachmentsChanged();
    }

    private void AttachmentsChanged()
    {
        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(CanQueue));
        SendCommand.NotifyCanExecuteChanged();
        SteerCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task AttachImagesAsync()
    {
        if (PickImagesRequested is not { } pick) return;
        await AddFilesAsync(await pick());
    }

    /// <summary>Takes the composer content (text + images) and clears it: the message is being sent, so the
    /// conversation goes to its latest message (chat apps do, even when the reader had scrolled up).</summary>
    private (string Text, ImageAttachment[] Images) TakeComposer()
    {
        var text = ComposerText.Trim();
        var comments = TakeAnnotations();
        // Alone, the comments get a first line that says what to do (it is also what names a new session)
        if (comments.Length > 0) text = (text.Length > 0 ? text : PreviewViewModel.CommentsOnlyLine) + "\n\n" + comments;
        var images = Attachments.Select(a => a.Model).ToArray();
        ComposerText = "";
        foreach (var a in Attachments) a.Dispose();
        Attachments.Clear();
        ComposerMessage = "";
        AttachmentsChanged();
        ScrollToLatestRequested?.Invoke();
        return (text, images);
    }

    private bool CanSteer() => Phase == SessionPhase.Running && HasContent;

    [RelayCommand(CanExecute = nameof(CanSteer))]
    private async Task SteerAsync()
    {
        try
        {
            if (HandleTerminalOnlyCommand(ComposerText)) return;
            var (text, images) = TakeComposer();
            await _session.QueueAsync(QueueKind.Steer, text, images, _cts.Token);
            Apply(_session.Snapshot());
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
    }

    private void ApplyQueue(SessionSnapshot s)
    {
        if (QueuedMessages.Select(q => q.Model).SequenceEqual(s.Queued)) return;
        QueuedMessages.Clear();
        foreach (var q in s.Queued) QueuedMessages.Add(new QueuedItemViewModel(q));
        OnPropertyChanged(nameof(HasQueued));
    }
}
