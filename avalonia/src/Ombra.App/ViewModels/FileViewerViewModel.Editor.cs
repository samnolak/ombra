using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.App.Services.Documents;

namespace Ombra.App.ViewModels;

/// <summary>View state only: the document service owns text, undo, disk revisions and recovery.</summary>
public sealed partial class FileViewerViewModel
{
    private static readonly string[] DocumentProperties =
    [
        nameof(Document), nameof(HasDocument), nameof(Text), nameof(PreviewText), nameof(MarkdownText),
        nameof(ShowEditor), nameof(ShowPreview), nameof(IsDirty), nameof(HasConflict), nameof(CanKeepMine), nameof(CanSaveDocument),
        nameof(DocumentStatus), nameof(DocumentName), nameof(SnapshotNotice), nameof(CanRevertHunks),
    ];
    public WorkspaceDocument? Document { get; private set; }
    public bool HasDocument => Document is not null;
    public string PreviewText => Document is null ? Text : "";
    public bool IsDirty => Document?.IsDirty == true;
    public bool HasConflict => Document?.HasConflict == true;
    public bool CanKeepMine => Document?.CanKeepMine == true;
    public bool CanSaveDocument => Document is { IsDirty: true, IsReadOnly: false, HasConflict: false } && !IsSavingDocument;
    public string DocumentStatus => Document?.Status ?? "";
    public string DocumentName => Name + (IsDirty ? " •" : "");
    public string SnapshotNotice => IsDirty
        ? Loc.T("Unsaved document. Changes, @file references, run and build use the disk snapshot until you save.")
        : "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveDocument))]
    private bool _isSavingDocument;
    [ObservableProperty] private bool _isConfirmingReload;
    [ObservableProperty] private string _documentError = "";
    [ObservableProperty] private string _documentNotice = "";

    internal void AttachDocument(WorkspaceDocument document)
    {
        DetachDocument();
        Document = document;
        Owner.RememberDocumentNavigationPath(document, FullPath);
        document.Changed += DocumentChanged;
        _text = ""; // never keep a second editable text snapshot
        Notice = "";
        State = ViewerState.Text;
        DocumentChanged(document);
    }

    private void DetachDocument()
    {
        if (Document is { } document) document.Changed -= DocumentChanged;
        Document = null;
    }

    private void DocumentChanged(WorkspaceDocument document)
    {
        var buffer = document.Buffer;
        var lineCount = buffer.LineCount - (buffer.GetLineByNumber(buffer.LineCount).Length == 0 ? 1 : 0);
        Info = buffer.TextLength == 0 ? Loc.T("Empty file") :
            (lineCount == 1 ? Loc.T("1 line") : Loc.F("{0:N0} lines", lineCount)) + " · " + document.EncodingName;
        foreach (var property in DocumentProperties)
            OnPropertyChanged(property);
    }

    [RelayCommand]
    private async Task SaveDocumentAsync()
    {
        if (!CanSaveDocument || Document is not { } document) return;
        IsSavingDocument = true;
        DocumentError = "";
        try { await document.SaveAsync(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        { DocumentError = e.Message; }
        finally { IsSavingDocument = false; }
    }

    [RelayCommand] private void AskReloadDocument() => IsConfirmingReload = true;
    [RelayCommand] private void CancelReloadDocument() => IsConfirmingReload = false;

    [RelayCommand]
    private async Task ReloadDocumentAsync()
    {
        if (Document is not { } document) return;
        DocumentError = "";
        try { await document.ReloadAsync(); IsConfirmingReload = false; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        { DocumentError = e.Message; }
    }

    [RelayCommand]
    private void CopyUnsavedDocument()
    {
        if (Document is { } document) Owner.Owner.CopyText(document.Buffer.Text);
    }

    /// <summary>The disk file changed under unsaved text (typically omp edited it): keep the user's text as a copy, then load the disk version.</summary>
    [RelayCommand]
    private async Task ReloadKeepingCopyAsync()
    {
        if (Document is not { } document) return;
        DocumentError = "";
        try
        {
            var copy = await document.ReloadKeepingCopyAsync();
            DocumentNotice = Loc.F("Your unsaved text was saved as {0}.", Path.GetFileName(copy));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        { DocumentError = e.Message; }
    }

    /// <summary>Keeps the user's text; the next save replaces the changed disk file.</summary>
    [RelayCommand]
    private void KeepMineDocument()
    {
        if (Document is not { CanKeepMine: true } document) return;
        DocumentError = "";
        DocumentNotice = "";
        document.KeepMine();
    }
}
