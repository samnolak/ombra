using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.App.Services;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

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
    public string Size => Model.Data.Length >= 1024 ? Loc.F("{0} KB", Model.Data.Length / 1024) : Loc.F("{0} B", Model.Data.Length);

    [RelayCommand] private void Remove() => _owner.RemoveAttachment(this);

    public void Dispose() => Preview.Dispose();
}

/// <summary>A message sent while omp works, until omp delivers it: × withdraws it, ↑ in an empty box edits it, Send now
/// makes omp read a follow-up at its next step.</summary>
public sealed partial class QueuedItemViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _owner;

    public QueuedItemViewModel(QueuedMessage model, MainViewModel owner)
    {
        _owner = owner;
        Model = model;
        Previews = [.. model.Images.Select(i => new ImagePreview(i, () => model.Images))];
    }

    public QueuedMessage Model
    {
        get => _model;
        internal set
        {
            if (!SetProperty(ref _model, value)) return;
            // Promotion turns a follow-up into a steer: label, tooltip and Send now follow.
            OnPropertyChanged(nameof(Kind));
            OnPropertyChanged(nameof(Tooltip));
            OnPropertyChanged(nameof(ThinkingLabel));
            OnPropertyChanged(nameof(HasThinking));
            OnPropertyChanged(nameof(CanSendNow));
            OnPropertyChanged(nameof(CanWithdraw));
            RemoveCommand.NotifyCanExecuteChanged();
            EditCommand.NotifyCanExecuteChanged();
            SendNowCommand.NotifyCanExecuteChanged();
        }
    }
    private QueuedMessage _model = null!;
    public IReadOnlyList<ImagePreview> Previews { get; }
    public bool HasImages => Previews.Count > 0;
    public string Text => Model.Text;
    public string Kind => Model.Kind switch
    {
        QueueKind.Steer => Loc.T("Steer"),
        QueueKind.BuiltinCommand => Loc.T("Command"),
        _ => Loc.T("Queued"),
    };
    /// <summary>The thinking level picked for this message while omp worked (a small tag); null: the session's level.</summary>
    public string? ThinkingLabel => Model.ThinkingLevel is { } level ? ThinkingLevelText.Label(level) : null;
    public bool HasThinking => Model.ThinkingLevel is not null;
    public string Tooltip => Model.LiveSteered ? Loc.T("omp already has that message.")
        : Model.Kind == QueueKind.Steer
            ? Loc.T("omp reads this at its next step. ↑ in the empty message box edits it.")
            : Model.ThinkingLevel is { } level
                ? Loc.F("Sent when omp finishes its current work, thinking effort: {0}. ↑ in the empty message box edits it.", ThinkingLevelText.Label(level))
                : Loc.T("Sent when omp finishes its current work. ↑ in the empty message box edits it.");

    /// <summary>Only omp's follow-up queue can be promoted; a steer is already read at the next step.</summary>
    public bool CanSendNow => Model is { Kind: QueueKind.FollowUp, Recovered: false };
    public bool CanWithdraw => !Model.LiveSteered;
    public string SendNowTooltip => Loc.T("Send now: omp reads this at its next step instead of after its current work.");

    [RelayCommand(CanExecute = nameof(CanSendNow))] private Task SendNow() => _owner.SendQueuedNowAsync(this);
    [RelayCommand(CanExecute = nameof(CanWithdraw))] private Task Remove() => _owner.WithdrawQueuedAsync(this, edit: false);
    [RelayCommand(CanExecute = nameof(CanWithdraw))] private Task Edit() => _owner.WithdrawQueuedAsync(this, edit: true);

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
        ? Loc.F("Pasted text +{0} lines", LineCount)
        : Loc.F("Pasted text +{0:N0} characters", Text.Length);

    /// <summary>The first lines, for the chip's tooltip.</summary>
    public string Preview
    {
        get
        {
            var lines = Text.Split('\n', 9);
            var head = string.Join("\n", lines.Take(8).Select(l => l.Length > 120 ? l[..GraphemeText.PrefixLength(l, 120)] + "…" : l.TrimEnd('\r')));
            return lines.Length > 8 ? head + "\n…" : head;
        }
    }

    [RelayCommand] private void Remove() => owner.RemovePastedText(this);
    [RelayCommand]
    private void Expand()
    {
        if (owner.IsReadOnly) return;
        owner.InsertMessageText(Text);
        owner.RemovePastedText(this);
    }
}

/// <summary>omp's thinking levels in words, for the thinking pill and its menu.</summary>
public static class ThinkingLevelText
{
    public static string Label(string? level) => level switch
    {
        null => Loc.T("Thinking"),
        "off" => Loc.T("Off"),
        "minimal" => Loc.T("Minimal"),
        "low" => Loc.T("Low"),
        "medium" => Loc.T("Medium"),
        "high" => Loc.T("High"),
        "xhigh" => Loc.T("Extra high"),
        "max" => Loc.T("Max"),
        _ => level.Length > 0 ? char.ToUpperInvariant(level[0]) + level[1..] : level,
    };

    public static string Description(string? level) => level switch
    {
        "off" => Loc.T("Answers straight away"),
        "minimal" => Loc.T("A moment of thought"),
        "low" => Loc.T("Quick reasoning for simple tasks"),
        "medium" => Loc.T("Balanced speed and depth"),
        "high" => Loc.T("Thinks longer on hard problems"),
        "xhigh" => Loc.T("Deeper reasoning, slower"),
        "max" => Loc.T("The model’s longest reasoning"),
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
    public bool CanQueue => !IsReadOnly && IsRunning && Phase == SessionPhase.Running && HasContent;
    private bool HasContent => !string.IsNullOrWhiteSpace(ComposerText) || Attachments.Count > 0 || PastedTexts.Count > 0 || Marks.HasOpen;

    /// <summary>A paste longer than this (characters) or than <see cref="LongPasteLines"/> lines becomes a chip.</summary>
    public const int LongPasteChars = 800;
    public const int LongPasteLines = 3;

    public static int CountLines(string text)
    {
        var t = text.TrimEnd('\r', '\n');
        return t.Length == 0 ? 0 : t.Count(c => c == '\n') + 1;
    }

    public string CleanPastedText(string text)
    {
        var cleaned = PromptText.Clean(text);
        if (cleaned.Removed > 0)
            ComposerMessage = Loc.F("Removed invisible characters: {0}. Review the pasted text.", cleaned.Removed);
        return cleaned.Text;
    }

    private bool PrepareComposerForSend()
    {
        var cleaned = PromptText.Clean(ComposerText);
        if (!ReferenceEquals(cleaned.Text, ComposerText)) ComposerText = cleaned.Text;
        var removed = cleaned.Removed;
        var changedPastes = false;
        for (var i = 0; i < PastedTexts.Count; i++)
        {
            var paste = PromptText.Clean(PastedTexts[i].Text);
            removed += paste.Removed;
            if (ReferenceEquals(paste.Text, PastedTexts[i].Text)) continue;
            PastedTexts[i] = new PastedTextViewModel(paste.Text, this);
            changedPastes = true;
        }
        if (changedPastes) AttachmentsChanged();
        if (removed == 0) return true;
        // Cleanup can expose a leading slash command. Show the result before any classification or dispatch.
        ComposerMessage = Loc.F("Removed invisible characters: {0}. Review the text and send again.", removed);
        return false;
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
    }

    /// <summary>
    /// Shift+Tab in the message box (Claude Code): Ask → Accept edits → Plan mode → Bypass → Ask. Plan mode is a mode of
    /// the session over the permission mode, offered when omp has it (MainViewModel.PlanMode.cs); leaving it moves on to
    /// the step after it. Bypass still asks first: the view opens the permission menu on its confirmation, and nothing
    /// changes until the user confirms there. Another Shift+Tab while that confirmation waits moves on past Bypass: a key
    /// press is never taken as the confirmation.
    /// </summary>
    public void CycleApprovalMode()
    {
        if (IsReadOnly) return;
        if (IsPlanMode && !ConfirmYolo)
        {
            _ = SetPlanModeAsync(false);
            // Plan entered from Bypass (the menu) has nothing of Bypass to step to: on to Ask
            _ = SetApprovalModeCommand.ExecuteAsync(ShownApprovalMode == "yolo" ? "always-ask" : "yolo");
            return;
        }
        var modes = ApprovalModes.Select(m => m.Mode).ToList();
        var from = ConfirmYolo ? "yolo" : ShownApprovalMode ?? "";
        ConfirmYolo = false;
        if (from == "write" && IsPlanModeAvailable && !IsPlanActionRunning)
        {
            _ = SetPlanModeAsync(true);
            return;
        }
        var next = modes[(modes.IndexOf(from) + 1) % modes.Count];
        _ = SetApprovalModeCommand.ExecuteAsync(next);
    }

    /// <summary>Raised when the user asked to attach images; the view shows the platform file picker.</summary>
    public event Func<Task<IReadOnlyList<string>>>? PickImagesRequested;

    partial void OnComposerTextChanged(string value)
    {
        ComposerInputChanged();
        UpdateCommandSuggestions();
        OnPropertyChanged(nameof(CanQueue));
        SteerCommand.NotifyCanExecuteChanged();
        InterruptAndSendCommand.NotifyCanExecuteChanged();
        SaveDraft();
    }

    public bool TryAddImage(ImageAttachment? image, string source)
    {
        if (image is null)
        {
            ComposerMessage = Loc.F("{0}: not an image omp can take, or too large even after scaling.", source);
            return false;
        }
        if (Attachments.Count >= ImageAttachments.MaxImages)
        {
            ComposerMessage = Loc.F("At most {0} images per message (omp takes one message of up to 1 MiB).", ImageAttachments.MaxImages);
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
                ComposerMessage = Loc.F("Could not read {0}: {1}", Path.GetFileName(path), e.Message);
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
        ComposerInputChanged();
        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(HasPastedTexts));
        OnPropertyChanged(nameof(CanQueue));
        SendCommand.NotifyCanExecuteChanged();
        SteerCommand.NotifyCanExecuteChanged();
        InterruptAndSendCommand.NotifyCanExecuteChanged();
        SaveDraft();
    }

    [RelayCommand]
    private async Task AttachImagesAsync()
    {
        if (PickImagesRequested is not { } pick) return;
        await AddFilesAsync(await pick());
    }

    /// <summary>Builds a message without mutating any input; validation and durable staging precede clearing.</summary>
    private (string Text, ImageAttachment[] Images) TakeComposer() => BuildComposer(preserveWhitespace: false);

    private (string Text, ImageAttachment[] Images) BuildComposer(bool preserveWhitespace)
    {
        var text = preserveWhitespace ? ComposerText : ComposerText.Trim();
        // Long pastes go in full after what was typed (the chips stood for them).
        foreach (var p in PastedTexts)
        {
            var paste = preserveWhitespace ? p.Text : p.Text.TrimEnd();
            text = text.Length > 0 ? text + "\n\n" + paste : paste;
        }
        // Open local marks follow (MainViewModel.Marks.cs; resolved ones stay out)
        text = WithComments(text);
        var images = Attachments.Select(a => a.Model).ToArray();
        return (text, images);
    }

    private void ClearComposer()
    {
        _changingDraft = true;
        ComposerText = "";
        foreach (var a in Attachments) a.Dispose();
        Attachments.Clear();
        PastedTexts.Clear();
        Marks.RemoveSent();
        Preview.IsAnnotating = false;
        _changingDraft = false;
        ComposerMessage = "";
        AttachmentsChanged();
        ScrollToLatestRequested?.Invoke();
    }

    private bool CanSteer() => !IsReadOnly && Phase == SessionPhase.Running && HasContent;

    [RelayCommand(CanExecute = nameof(CanSteer))]
    private async Task SteerAsync()
    {
        if (IsReadOnly) return;
        using var admission = AdmitWorktreeChat(null);
        if (admission is null) return;
        var open = _open;
        string? submission = null;
        try
        {
            if (!PrepareComposerForSend()) return;
            if (HandleTerminalOnlyCommand(ComposerText)) return;
            var content = TakeComposer();
            submission = StageSubmission(open, content, fromComposer: true);
            var accepted = await open.Controller.QueueAsync(QueueKind.Steer, content.Text, content.Images, _cts.Token, submission);
            FinishSubmission(open, submission, accepted);
            ApplyIfShown(open);
        }
        catch (Exception e) when (e is OmpNotRunningException or ArgumentException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            if (submission is not null) FinishSubmission(open, submission, accepted: false);
            if (open == _open) ComposerMessage = e.Message;
        }
    }

    private void ApplyQueue(SessionSnapshot s)
    {
        var pending = s.Queued.Where(q => !q.Recovered).ToArray();
        if (QueuedMessages.Select(q => q.Model).SequenceEqual(pending)) return;
        // Messages still queued keep their rows (and decoded previews); the delivered and withdrawn ones go
        var kept = QueuedMessages.ToDictionary(q => q.Model.Seq);
        QueuedMessages.Clear();
        foreach (var q in pending)
        {
            if (kept.Remove(q.Seq, out var row))
            {
                // Local follow-up admission changes its route, not its text, images or visible label.
                row.Model = q;
                QueuedMessages.Add(row);
            }
            else QueuedMessages.Add(new QueuedItemViewModel(q, this));
        }
        foreach (var gone in kept.Values) gone.Dispose();
        OnPropertyChanged(nameof(HasQueued));
    }

    /// <summary>↑ in an empty message box (Claude Code): the last queued message comes back to be edited.</summary>
    public bool EditLastQueued()
    {
        if (QueuedMessages.Count == 0 || ComposerText.Length > 0 || Attachments.Count > 0 || PastedTexts.Count > 0) return false;
        var last = QueuedMessages.LastOrDefault(q => q.CanWithdraw);
        if (last is null) return false;
        _ = WithdrawQueuedAsync(last, edit: true);
        return true;
    }

    private readonly HashSet<(SessionController Controller, long Sequence)> _withdrawing = [];

    /// <summary>
    /// Takes a queued message back from omp before it is delivered; with <paramref name="edit"/> its text and images
    /// return to the message box. omp may have delivered it meanwhile: then it stays in the conversation.
    /// </summary>
    internal async Task WithdrawQueuedAsync(QueuedItemViewModel item, bool edit)
    {
        if (!item.CanWithdraw) return;
        var open = _open;
        if (!_withdrawing.Add((open.Controller, item.Model.Seq))) return;
        try
        {
            IReadOnlyList<ImageAttachment>? images;
            try { images = await open.Controller.RemoveQueuedAsync(item.Model, _cts.Token, recover: edit); }
            catch (Ombra.Rpc.RpcCommandException)
            {
                ComposerMessage = Loc.T("This omp can’t take back queued messages (omp 18.4.4 or later can).");
                return;
            }
            ApplyIfShown(open);
            if (images is null)
            {
                ComposerMessage = Loc.T("omp already has that message.");
                return;
            }
            ComposerMessage = "";
            if (!edit && open == _open)
            {
                var restored = false;
                var text = item.Model.Text;
                var level = item.Model.ThinkingLevel;
                var card = new SessionCardViewModel("brief", "IconTrash", Loc.T("Message removed from queue"),
                    Loc.T("Undo restores it to the composer without sending."));
                card.Primary = new SessionCardAction(Loc.T("Undo"), new RelayCommand(() =>
                {
                    if (restored || !_opens.ContainsKey(open.Controller)) return;
                    restored = true;
                    CloseSessionCard();
                    AppendRecovered(open, text, images);
                    // Its thinking level comes back with it, like an edited one (ConversationState.RecoverQueued).
                    if (level is not null && open.Last?.Phase is SessionPhase.Ready or SessionPhase.Running)
                        _ = open.Controller.SetThinkingLevelAsync(level, _cts.Token).ContinueWith(_ => PostToUi(() => ApplyIfShown(open)), TaskScheduler.Default);
                    SaveDraft(open);
                    FlushDrafts();
                    if (open == _open) FocusComposerRequested?.Invoke();
                }));
                ShowTimedCard(card, UndoCardDuration);
            }
            if (!edit || open != _open) return;
            CaretToEndRequested?.Invoke();
            FocusComposerRequested?.Invoke();
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        finally
        {
            _withdrawing.Remove((open.Controller, item.Model.Seq));
        }
    }
}
