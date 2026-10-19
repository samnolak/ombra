using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

public sealed partial class MainViewModel
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendAsFileCommand))]
    [NotifyPropertyChangedFor(nameof(InputFileNotice))]
    private bool _hasOversizedInput;

    private string? _offeredInputFile;
    private long _composerRevision;

    public string InputFileNotice => Loc.F("Save the full text, pasted text and comments as a local UTF-8 file, then send its @file reference with your images. File previews may be truncated; the complete file remains available. Kept until you delete it manually: {0}", _offeredInputFile ?? "");

    private void ComposerInputChanged()
    {
        _composerRevision++;
        HasOversizedInput = false;
        _offeredInputFile = null;
    }

    private void OfferInputFile(string text)
    {
        // Wrapping a slash command in prose would bypass its normal dispatch. Never reinterpret it as a file prompt.
        if (text.Length == 0 || text.TrimStart().StartsWith('/')) return;
        var journal = _draftStore.Path ?? DraftStore.PathFor(_settings?.Path ?? OmpRuntimeOptions.DefaultConfigPath);
        _offeredInputFile = Path.Combine(journal + ".input-files", Guid.NewGuid().ToString("N") + ".txt");
        HasOversizedInput = true;
        OnPropertyChanged(nameof(InputFileNotice));
    }

    private bool CanSendAsFile() => HasOversizedInput && CanSend() && _disposed == 0;

    [RelayCommand(CanExecute = nameof(CanSendAsFile))]
    private async Task SendAsFileAsync()
    {
        if (!CanSendAsFile()) return;
        if (!PrepareComposerForSend()) return;
        if (HandleTerminalOnlyCommand(ComposerText)) return;
        var content = BuildComposer(preserveWhitespace: true);
        if (content.Text.TrimStart().StartsWith('/')) return;
        var open = _open;
        var identity = open.DraftId;
        var revision = _composerRevision;
        var original = CaptureDraft(open);
        var path = _offeredInputFile!;
        try
        {
            var mention = FilesViewModel.Mention(path);
            // The runtime's quoted @file grammar has no escape syntax. Unix backslashes are also
            // changed by the shared mention formatter, so those paths cannot round-trip either.
            if ((mention[1] is '\'' or '"' && mention.AsSpan(2, mention.Length - 3).Contains(mention[1]))
                || (!OperatingSystem.IsWindows() && path.Contains('\\')))
                throw new ArgumentException(Loc.T("This input-file path cannot be represented as an @file reference. Use a settings path without conflicting quotes or backslashes."));
            var reference = Loc.F("Read the complete message in this UTF-8 file before responding (its automatic preview may be truncated): {0}", mention);
            // Images still travel normally. Reject an oversized image envelope before writing or clearing anything.
            SessionController.ValidateMessage(reference, content.Images);
            await Task.Run(() => WriteInputFile(path, content.Text), _cts.Token);
            // A write completing after navigation, edits, close, or a new session cannot submit or clear that draft.
            if (_disposed != 0 || _cts.IsCancellationRequested || open != _open || open.DraftId != identity
                || revision != _composerRevision || !CanSendAsFile()) return;
            await SubmitAsync(() => (reference, content.Images), fromComposer: true, recovery: original);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or OperationCanceledException)
        {
            if (_disposed == 0 && open == _open && open.DraftId == identity && revision == _composerRevision)
                ComposerMessage = e.Message;
        }
        finally
        {
            // A failed/abandoned attempt may already have created its immutable file. A retry gets a new name.
            if (_disposed == 0 && open == _open && open.DraftId == identity && revision == _composerRevision && HasOversizedInput)
                OfferInputFile(content.Text);
        }
    }

    private static void WriteInputFile(string path, string text)
    {
        var directory = Path.GetDirectoryName(path)!;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.Read };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var file = new FileStream(path, options);
        using (var writer = new StreamWriter(file, new UTF8Encoding(false, true), leaveOpen: true))
        {
            writer.Write(text);
            writer.Flush();
        }
        file.Flush(flushToDisk: true);
        // Deliberately outside chat/temp cleanup: queues, recovered drafts and saved conversations can retain
        // this reference after the originating chat closes. Only the user removes these durable input files.
    }
}
