using CommunityToolkit.Mvvm.Input;
using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

/// <summary>
/// Settings → Advanced (omp command, arguments before omp's own, OMP_PROFILE) apply only with "Save and restart omp",
/// unlike every other setting, which saves as it changes. Edits not saved yet are flagged on the page and kept when the
/// page closes and opens again; Discard puts back what is saved.
/// </summary>
public sealed partial class MainViewModel
{
    private (string Command, string PrefixArgs, string Profile) _savedRuntime = ("", "", "");

    /// <summary>The Advanced fields differ from the saved settings.</summary>
    public bool HasUnsavedRuntime => (SettingsCommand, SettingsPrefixArgs, SettingsProfile) != _savedRuntime;

    /// <summary>The fields show <paramref name="o"/>, which is what is saved.</summary>
    private void ShowSavedRuntime(OmpRuntimeOptions o)
    {
        _savedRuntime = (o.Command ?? "", string.Join("\n", o.PrefixArgs), o.Profile ?? "");
        (SettingsCommand, SettingsPrefixArgs, SettingsProfile) = _savedRuntime;
        OnPropertyChanged(nameof(HasUnsavedRuntime));
    }

    [RelayCommand]
    private void DiscardRuntimeEdits() => ShowSavedRuntime(LoadSettingsOrDefault());
}
