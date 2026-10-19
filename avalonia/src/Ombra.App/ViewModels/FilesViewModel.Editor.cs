using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.App.Services;
using Ombra.App.Services.Documents;

namespace Ombra.App.ViewModels;

public sealed record EditorBufferItem(string FullPath, string Label);

public sealed partial class FilesViewModel
{
    private readonly Dictionary<string, string> _documentNavigationPaths = new(DocumentFile.PathComparer);

    internal void RememberDocumentNavigationPath(WorkspaceDocument document, string path)
    {
        _documentNavigationPaths.TryAdd(document.FullPath, path);
        DocumentStateChanged();
    }
    public EditorLanguageService LanguageService => Owner.EditorLanguageService;
    public ObservableCollection<EditorBufferItem> UnsavedDocuments { get; } = [];
    public bool HasUnsavedDocuments => UnsavedDocuments.Count > 0;
    public bool IsEditorTrusted => LanguageService.IsTrusted;
    public string EditorLanguageStatus => LanguageService.Status;
    [ObservableProperty] private bool _isConfirmingEditorTrust;
    private string? _editorTrustRoot;

    internal void DocumentStateChanged()
    {
        if (!Owner.UiDispatcher.CheckAccess())
        {
            Owner.UiDispatcher.Post(DocumentStateChanged);
            return;
        }
        var service = Owner.Documents;
        var dirty = service.Documents.Where(d => d.IsDirty).Select(d => d.FullPath);
        var paths = dirty.Concat(service.RecoverablePaths).Distinct(DocumentFile.PathComparer)
            .Select(path => _documentNavigationPaths.TryGetValue(path, out var navigationPath) ? navigationPath : path).ToArray();
        if (!UnsavedDocuments.Select(d => d.FullPath).SequenceEqual(paths, ProjectFiles.PathComparer))
        {
            UnsavedDocuments.Clear();
            foreach (var path in paths) UnsavedDocuments.Add(new(path, Path.GetFileName(path)));
            OnPropertyChanged(nameof(HasUnsavedDocuments));
        }
    }

    private void LanguageStateChanged()
    {
        if (!Owner.UiDispatcher.CheckAccess())
        {
            Owner.UiDispatcher.Post(LanguageStateChanged);
            return;
        }
        OnPropertyChanged(nameof(IsEditorTrusted));
        OnPropertyChanged(nameof(EditorLanguageStatus));
    }

    [RelayCommand]
    private Task OpenEditorBufferAsync(EditorBufferItem? item) =>
        item is null ? Task.CompletedTask : OpenFileAsync(item.FullPath);

    [RelayCommand]
    private void AskEditorTrust()
    {
        _editorTrustRoot = Owner.ProjectFolder;
        IsConfirmingEditorTrust = true;
    }
    [RelayCommand] private void CancelEditorTrust() => IsConfirmingEditorTrust = false;

    [RelayCommand]
    private async Task EnableEditorLanguageAsync()
    {
        IsConfirmingEditorTrust = false;
        if (_editorTrustRoot is null || !SamePath(_editorTrustRoot, Owner.ProjectFolder))
        {
            Report(Loc.T("The workspace changed. Review language-service trust again for this workspace."));
            return;
        }
        try { await LanguageService.SetTrustedAsync(true); }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        { Report(Loc.F("Could not enable language services: {0}", e.Message)); }
    }

    [RelayCommand]
    private async Task DisableEditorLanguageAsync()
    {
        try { await LanguageService.SetTrustedAsync(false); }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        { Report(Loc.F("Could not disable language services: {0}", e.Message)); }
    }
}
