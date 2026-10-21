using CommunityToolkit.Mvvm.ComponentModel;
using Ombra.App.Localization;

namespace Ombra.App.ViewModels;

/// <summary>
/// The sidebar's selection mode (MainViewModel.SessionSelection.cs): on while rows are being picked, with how many are.
/// Each row holds the one instance, so its check box and its menu (a popup, outside the window's tree) can read it.
/// </summary>
public sealed partial class SessionSelectionViewModel : ObservableObject
{
    /// <summary>Rows show a check box and a click toggles one instead of opening it.</summary>
    [ObservableProperty] private bool _isActive;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CountText), nameof(DeleteLabel), nameof(HasAny))] private int _count;

    public bool HasAny => Count > 0;
    /// <summary>"3 selected": the bar over the list.</summary>
    public string CountText => Loc.Plural(Count, "{0} selected", "{0} selected");
    /// <summary>"Delete 3 chats": a selected row's menu and the confirmation's button.</summary>
    public string DeleteLabel => Loc.Plural(Count, "Delete {0} chat", "Delete {0} chats");
}
