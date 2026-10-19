using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

/// <summary>
/// Settings → Permissions → Trusted folders (<see cref="WorkspaceTrust"/>): the project folders whose own tools omp
/// may run, each with Revoke. A revoked folder's chats restart restricted at once when idle.
/// </summary>
public sealed partial class PermissionRulesViewModel
{
    public ObservableCollection<TrustedFolderRowViewModel> TrustedFolders { get; } = [];

    [ObservableProperty] private bool _hasTrustedFolders;

    private void RefreshTrustedFolders()
    {
        TrustedFolders.Clear();
        foreach (var folder in Main.WorkspaceTrust?.TrustedFolders ?? []) TrustedFolders.Add(new TrustedFolderRowViewModel(folder, this));
        HasTrustedFolders = TrustedFolders.Count > 0;
    }

    internal async Task RevokeAsync(TrustedFolderRowViewModel row)
    {
        if (Main.WorkspaceTrust is not { } trust) return;
        try { trust.Revoke(row.Folder.Path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Say(Loc.T("Not revoked: ") + e.Message, error: true);
            return;
        }
        RefreshTrustedFolders();
        var busy = await Main.RestrictFolderChatsAsync(row.Folder.Path); // MainViewModel.WorkspaceTrust.cs
        Say(busy > 0
            ? Loc.F("Revoked. {0} chat(s) in this folder are working and restart without its tools when their run ends.", busy)
            : Loc.F("Revoked. omp runs in {0} without the folder's own tools.", row.Name));
    }
}

public sealed partial class TrustedFolderRowViewModel(TrustedFolder folder, PermissionRulesViewModel owner) : ObservableObject
{
    public TrustedFolder Folder { get; } = folder;

    public string Name => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Folder.Path)) is { Length: > 0 } n ? n : Folder.Path;

    /// <summary>The folder's path as the sidebar shows it (<c>~/…</c>).</summary>
    public string Detail => GitProbe.ShownPath(Folder.Path);

    public string TrustedLabel => Loc.F("Trusted {0}", Folder.TrustedAt.ToLocalTime().ToString("d", CultureInfo.CurrentCulture));

    [RelayCommand] private Task Revoke() => owner.RevokeAsync(this);
}
