using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Ombra.App.ViewModels;

namespace Ombra.App.Views;

/// <summary>The rail's Editor page (EditorPageViewModel): the tree's and the hits' clicks and keys, the editor's events.</summary>
public partial class EditorPage : UserControl
{
    private EditorPageViewModel? _subscribed;

    public EditorPage()
    {
        InitializeComponent();
        EditorTree.Tapped += (_, _) => OpenSelected();
        EditorTree.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; OpenSelected(); } };
        EditorSearchHits.Tapped += (_, _) => OpenHit();
        EditorSearchHits.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; OpenHit(); } };
        EditorSearchBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || Vm is not { } vm) return;
            e.Handled = true;
            vm.SearchCommand.Execute(null);
        };
        PageEditor.NavigateRequested += async (path, line) => { if (Vm is { } vm) await vm.OpenAsync(path, line); };
        PageEditor.MarkRequested += request => Vm?.ActiveTab?.AddEditorMark(request);
        DataContextChanged += (_, _) =>
        {
            if (_subscribed is not null) _subscribed.PropertyChanged -= OnVmChanged;
            _subscribed = Vm;
            if (_subscribed is not null) _subscribed.PropertyChanged += OnVmChanged;
        };
    }

    private EditorPageViewModel? Vm => DataContext as EditorPageViewModel;

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorPageViewModel.IsSearchShown) && Vm is { IsSearchShown: true })
            Dispatcher.UIThread.Post(() => EditorSearchBox.Focus(), DispatcherPriority.Loaded);
    }

    private void OpenSelected()
    {
        if (EditorTree.SelectedItem is FileNodeViewModel node) Vm?.OpenNodeCommand.Execute(node);
    }

    private void OpenHit()
    {
        if (EditorSearchHits.SelectedItem is EditorSearchHit hit) Vm?.OpenHitCommand.Execute(hit);
    }

    private void OnTabTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is EditorTabViewModel tab) tab.ActivateCommand.Execute(null);
    }
}
