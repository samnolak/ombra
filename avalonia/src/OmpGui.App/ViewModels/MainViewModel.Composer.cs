using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmpGui.App.Services;
using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

/// <summary>An image in the message box: its preview (a click opens it in the viewer), name and size; × removes it.</summary>
public sealed partial class AttachmentViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _owner;

    public AttachmentViewModel(ImageAttachment model, MainViewModel owner)
    {
        _owner = owner;
        Model = model;
        // ←/→ in the viewer go through the images in the box now
        Preview = new ImagePreview(model, () => [.. owner.Attachments.Select(a => a.Model)]);
    }

    public ImageAttachment Model { get; }
    public ImagePreview Preview { get; }
    public string Name => Model.Name;
    public string Size => Model.Data.Length >= 1024 ? $"{Model.Data.Length / 1024} KB" : $"{Model.Data.Length} B";

    [RelayCommand] private void Remove() => _owner.RemoveAttachment(this);

    public void Dispose() => Preview.Dispose();
}

/// <summary>A message sent while omp works, until omp delivers it: × withdraws it, ↑ in an empty box edits it.</summary>
public sealed partial class QueuedItemViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _owner;

    public QueuedItemViewModel(QueuedMessage model, MainViewModel owner)
    {
        _owner = owner;
        Model = model;
        Previews = [.. model.Images.Select(i => new ImagePreview(i, () => model.Images))];
    }

    public QueuedMessage Model { get; }
    public IReadOnlyList<ImagePreview> Previews { get; }
    public bool HasImages => Previews.Count > 0;
    public string Text => Model.Text;
    public string Kind => Model.Kind == QueueKind.Steer ? "Steer" : "Queued";
    public string Tooltip => Model.Kind == QueueKind.Steer
        ? "omp reads this at its next step. ↑ in the empty message box edits it."
        : "Sent when omp finishes its current work. ↑ in the empty message box edits it.";

    [RelayCommand] private Task Remove() => _owner.WithdrawQueuedAsync(this, edit: false);

    public void Dispose()
    {
        foreach (var p in Previews) p.Dispose();
    }
}

/// <summary>A long paste, kept out of the message box as a chip and sent in full with the message.</summary>
public sealed partial class PastedTextViewModel(string text, MainViewModel owner) : ObservableObject
{
    public string Text { get; } = text;
    public int LineCount { get; } = MainViewModel.CountLines(text);
    public string Label => LineCount > 1
        ? $"Pasted text +{LineCount} lines"
        : string.Create(System.Globalization.CultureInfo.CurrentCulture, $"Pasted text +{Text.Length:N0} characters");

    /// <summary>The first lines, for the chip's tooltip.</summary>
    public string Preview
    {
        get
        {
            var lines = Text.Split('\n', 9);
            var head = string.Join("\n", lines.Take(8).Select(l => l.Length > 120 ? l[..120] + "…" : l.TrimEnd('\r')));
            return lines.Length > 8 ? head + "\n…" : head;
        }
    }

    [RelayCommand] private void Remove() => owner.RemovePastedText(this);
}

/// <summary>omp's thinking levels in words, for the thinking pill and its menu.</summary>
public static class ThinkingLevelText
{
    public static string Label(string? level) => level switch
    {
        null => "Thinking",
        "off" => "Off",
        "minimal" => "Minimal",
        "low" => "Low",
        "medium" => "Medium",
        "high" => "High",
        "xhigh" => "Extra high",
        "max" => "Max",
        _ => level.Length > 0 ? char.ToUpperInvariant(level[0]) + level[1..] : level,
    };

    public static string Description(string? level) => level switch
    {
        "off" => "Answers straight away",
        "minimal" => "A moment of thought",
        "low" => "Quick reasoning for simple tasks",
        "medium" => "Balanced speed and depth",
        "high" => "Thinks longer on hard problems",
        "xhigh" => "Deeper reasoning, slower",
        "max" => "The model's longest reasoning",
        _ => "",
    };

    public static readonly Avalonia.Data.Converters.IValueConverter LabelConverter =
        new Avalonia.Data.Converters.FuncValueConverter<string?, string>(Label);

    public static readonly Avalonia.Data.Converters.IValueConverter DescriptionConverter =
        new Avalonia.Data.Converters.FuncValueConverter<string?, string>(Description);
}

/// <summary>The permission mode's mark: a shield to ask, a pencil to accept edits, a warning to bypass.</summary>
public static class ApprovalModeIcons
{
    public static readonly Avalonia.Data.Converters.IValueConverter IconConverter =
        new Avalonia.Data.Converters.FuncValueConverter<string?, object?>(mode =>
            Avalonia.Application.Current is { } app && app.TryGetResource(IconKey(mode), app.ActualThemeVariant, out var icon) ? icon : null);

    /// <summary>The icon resource of a mode (a session card names its icon by key).</summary>
    public static string IconKey(string? mode) => mode switch { "write" => "IconPencil", "yolo" => "IconAlert", _ => "IconShield" };
}

/// <summary>Composer: attachments, long pastes, file references, messages while omp works, the permission mode
/// keys and the shortcut sheet.</summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<AttachmentViewModel> Attachments { get; } = [];
    public ObservableCollection<PastedTextViewModel> PastedTexts { get; } = [];
    public ObservableCollection<QueuedItemViewModel> QueuedMessages { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasComposerMessage))]
    private string _composerMessage = "";

    public bool HasComposerMessage => ComposerMessage.Length > 0;
    public bool HasAttachments => Attachments.Count > 0;
    public bool HasPastedTexts => PastedTexts.Count > 0;
    public bool HasQueued => QueuedMessages.Count > 0;
    public bool CanQueue => IsRunning && Phase == SessionPhase.Running && HasContent;
    private bool HasContent => !string.IsNullOrWhiteSpace(ComposerText) || Attachments.Count > 0 || PastedTexts.Count > 0 || Preview.HasAnnotations || CodeComments.Count > 0;

    /// <summary>A paste longer than this (characters) or than <see cref="LongPasteLines"/> lines becomes a chip.</summary>
    public const int LongPasteChars = 800;
    public const int LongPasteLines = 3;

    public static int CountLines(string text)
    {
        var t = text.TrimEnd('\r', '\n');
        return t.Length == 0 ? 0 : t.Count(c => c == '\n') + 1;
    }

    /// <summary>
    /// A long paste (Claude Code's "[Pasted text +N lines]") goes beside the message as a chip instead of filling the
    /// box; true when it was taken. Sent in full after what was typed.
    /// </summary>
    public bool TryAddPastedText(string? text)
    {
        if (text is null || text.Length <= LongPasteChars && CountLines(text) <= LongPasteLines) return false;
        PastedTexts.Add(new PastedTextViewModel(text, this));
        AttachmentsChanged();
        return true;
    }

    internal void RemovePastedText(PastedTextViewModel p)
    {
        PastedTexts.Remove(p);
        AttachmentsChanged();
        FocusComposerRequested?.Invoke();
    }

    /// <summary>The shortcut sheet (⌘/ or Ctrl+/, and /hotkeys).</summary>
    [ObservableProperty] private bool _isShortcutsOpen;

    /// <summary>The sheet's rows, as the platform writes keys and as the send key is set now.</summary>
    [ObservableProperty] private IReadOnlyList<KeyboardShortcutGroup> _shortcuts = KeyboardShortcuts.For(OperatingSystem.IsMacOS());

    partial void OnIsShortcutsOpenChanged(bool value)
    {
        if (value) Shortcuts = KeyboardShortcuts.For(OperatingSystem.IsMacOS(), SendWithModifier);
    }

    [RelayCommand] private void ToggleShortcuts() => IsShortcutsOpen = !IsShortcutsOpen;

    [RelayCommand]
    private void CloseShortcuts()
    {
        IsShortcutsOpen = false;
        FocusComposerRequested?.Invoke();
    }

    /// <summary>
    /// Shift+Tab in the message box (Claude Code): Ask → Accept edits → Bypass → Ask. Bypass still asks first: the
    /// view opens the permission menu on its confirmation, and nothing changes until the user confirms there. Another
    /// Shift+Tab while that confirmation waits moves on past Bypass: a key press is never taken as the confirmation.
    /// </summary>
    public void CycleApprovalMode()
    {
        var modes = ApprovalModes.Select(m => m.Mode).ToList();
        var from = ConfirmYolo ? "yolo" : ShownApprovalMode ?? "";
        ConfirmYolo = false;
        var next = modes[(modes.IndexOf(from) + 1) % modes.Count];
        _ = SetApprovalModeCommand.ExecuteAsync(next);
    }

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
        OnPropertyChanged(nameof(HasPastedTexts));
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

    /// <summary>Takes the composer content (text, long pastes, code and page comments, images) and clears it: the message is
    /// being sent, so the conversation goes to its latest message (chat apps do, even when the reader had scrolled up).</summary>
    private (string Text, ImageAttachment[] Images) TakeComposer()
    {
        var text = ComposerText.Trim();
        // Long pastes go in full after what was typed (the chips stood for them)
        foreach (var p in PastedTexts) text = text.Length > 0 ? text + "\n\n" + p.Text.TrimEnd() : p.Text.TrimEnd();
        // Comments on the code and on the page follow (MainViewModel.CodeComments.cs)
        text = WithComments(text);
        var images = Attachments.Select(a => a.Model).ToArray();
        ComposerText = "";
        foreach (var a in Attachments) a.Dispose();
        Attachments.Clear();
        PastedTexts.Clear();
        ComposerMessage = "";
        AttachmentsChanged();
        ScrollToLatestRequested?.Invoke();
        return (text, images);
    }

    private bool CanSteer() => Phase == SessionPhase.Running && HasContent;

    [RelayCommand(CanExecute = nameof(CanSteer))]
    private async Task SteerAsync()
    {
        var open = _open;
        (string Text, ImageAttachment[] Images) content = ("", []);
        try
        {
            if (HandleTerminalOnlyCommand(ComposerText)) return;
            content = TakeComposer();
            await open.Controller.QueueAsync(QueueKind.Steer, content.Text, content.Images, _cts.Token);
            ApplyIfShown(open);
        }
        catch (OmpNotRunningException) { if (open == _open) PutBackUnsent(content); }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
    }

    private void ApplyQueue(SessionSnapshot s)
    {
        if (QueuedMessages.Select(q => q.Model).SequenceEqual(s.Queued)) return;
        // Messages still queued keep their rows (and decoded previews); the delivered and withdrawn ones go
        var kept = QueuedMessages.ToDictionary(q => q.Model.Seq);
        QueuedMessages.Clear();
        foreach (var q in s.Queued)
            QueuedMessages.Add(kept.Remove(q.Seq, out var row) ? row : new QueuedItemViewModel(q, this));
        foreach (var gone in kept.Values) gone.Dispose();
        OnPropertyChanged(nameof(HasQueued));
    }

    /// <summary>↑ in an empty message box (Claude Code): the last queued message comes back to be edited.</summary>
    public bool EditLastQueued()
    {
        if (QueuedMessages.Count == 0 || ComposerText.Length > 0 || Attachments.Count > 0 || PastedTexts.Count > 0) return false;
        _ = WithdrawQueuedAsync(QueuedMessages[^1], edit: true);
        return true;
    }

    private readonly HashSet<long> _withdrawing = [];

    /// <summary>
    /// Takes a queued message back from omp before it is delivered; with <paramref name="edit"/> its text and images
    /// return to the message box. omp may have delivered it meanwhile: then it stays in the conversation.
    /// </summary>
    internal async Task WithdrawQueuedAsync(QueuedItemViewModel item, bool edit)
    {
        if (!_withdrawing.Add(item.Model.Seq)) return;
        try
        {
            IReadOnlyList<ImageAttachment>? images;
            try { images = await Session.RemoveQueuedAsync(item.Model, _cts.Token); }
            catch (OmpGui.Rpc.RpcCommandException)
            {
                ComposerMessage = "This omp can't take back queued messages (omp 18.4.4 or later can).";
                return;
            }
            Apply(Session.Snapshot());
            if (images is null)
            {
                ComposerMessage = "omp already has that message.";
                return;
            }
            ComposerMessage = "";
            if (!edit) return;
            ComposerText = ComposerText.Length == 0 ? item.Text : item.Text + "\n" + ComposerText;
            foreach (var image in images) TryAddImage(image, image.Name);
            CaretToEndRequested?.Invoke();
            FocusComposerRequested?.Invoke();
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        finally
        {
            _withdrawing.Remove(item.Model.Seq);
        }
    }
}
