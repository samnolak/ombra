using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using OmpGui.App.ViewModels;

namespace OmpGui.App.Views.Settings;

/// <summary>Settings page "Pets" (DataContext = <see cref="MainViewModel"/>; its content binds <see cref="MainViewModel.Pets"/>).</summary>
public partial class PetsSettings : UserControl
{
    private PetsViewModel? _pets;

    public PetsSettings()
    {
        InitializeComponent();
        // Enter in the name saves, Esc cancels (as in a dialog)
        PetNameBox.KeyDown += (_, e) =>
        {
            if (_pets is null) return;
            if (e.Key == Key.Enter) { _pets.SaveEditorCommand.Execute(null); e.Handled = true; }
            else if (e.Key == Key.Escape) { _pets.CancelEditorCommand.Execute(null); e.Handled = true; }
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_pets is not null) _pets.PropertyChanged -= OnPetsChanged;
        _pets = (DataContext as MainViewModel)?.Pets;
        if (_pets is not null) _pets.PropertyChanged += OnPetsChanged;
    }

    /// <summary>The editor opens below the gallery: bring it into view, the name field ready for typing.</summary>
    private void OnPetsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PetsViewModel.IsEditorOpen) || _pets?.IsEditorOpen != true) return;
        Dispatcher.UIThread.Post(() =>
        {
            PetEditor.BringIntoView();
            PetNameBox.Focus();
        }, DispatcherPriority.Background);
    }
}
