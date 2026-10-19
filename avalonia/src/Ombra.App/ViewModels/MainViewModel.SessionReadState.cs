using CommunityToolkit.Mvvm.Input;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

public sealed partial class MainViewModel
{
    private HashSet<string>? _markedUnread;
    private HashSet<string> MarkedUnread()
    {
        if (_markedUnread is not null) return _markedUnread;
        OmpRuntimeOptions? options = null;
        try { options = _settings?.Load(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { }
        return _markedUnread = new HashSet<string>(options?.MarkedUnreadSessions ?? [], SessionCatalog.PathComparer);
    }

    private void SaveMarkedUnread() => Persist(o => o with
    {
        MarkedUnreadSessions = _markedUnread is { Count: > 0 } paths ? [.. paths.Order(StringComparer.Ordinal)] : null,
    });

    private void ClearMarkedUnread(string? path)
    {
        if (path is not null && MarkedUnread().Remove(path)) SaveMarkedUnread();
    }

    [RelayCommand]
    private void ToggleReadItem(SessionItemViewModel? item)
    {
        if (item is null) return;
        var unread = !item.IsMarkedUnread;
        if (unread) MarkedUnread().Add(item.Model.Path);
        else MarkedUnread().Remove(item.Model.Path);
        foreach (var open in _opens.Values)
            if (SessionCatalog.PathComparer.Equals(open.Controller.SessionFile ?? open.Controller.CurrentLaunch.ResumeSessionFile, item.Model.Path)) open.Unread = unread;
        SaveMarkedUnread();
        ApplySessionFilter();
    }

    [RelayCommand]
    private void MarkAllRead()
    {
        MarkedUnread().Clear();
        foreach (var open in _opens.Values) open.Unread = false;
        SaveMarkedUnread();
        ApplySessionFilter();
    }

    [RelayCommand]
    private void RevealSessionItem(SessionItemViewModel? item)
    {
        if (item is not null) Files.RevealInFileManagerCommand.Execute(item.Model.Path);
    }

    [RelayCommand]
    private void TerminalSessionItem(SessionItemViewModel? item)
    {
        if (item is not null && Directory.Exists(item.Cwd)) NewShellIn(item.Cwd);
    }
}
