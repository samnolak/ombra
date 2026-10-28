using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Ombra.App.ViewModels;

namespace Ombra.App.Views.Session;

/// <summary>
/// The BTW panel; opening it puts the keyboard in its question box, Enter asks, Esc closes the panel (never stops omp's
/// run). Topics read top to bottom like the conversation: the list follows a new or growing answer at its bottom unless
/// the reader scrolled up.
/// </summary>
public partial class BtwPanel : UserControl
{
    private MainViewModel? _vm;
    private bool _atBottom = true;

    public BtwPanel()
    {
        InitializeComponent();
        BtwQuestionBox.AddHandler(KeyDownEvent, OnQuestionKey, RoutingStrategies.Tunnel);
        BtwScroller.ScrollChanged += OnScrollChanged;
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // Grown content (a new topic, a streaming answer) keeps the bottom in view if it was; a scroll by the reader decides
        if (e.ExtentDelta.Y != 0 && e.OffsetDelta.Y == 0)
        {
            if (_atBottom) BtwScroller.ScrollToEnd();
            return;
        }
        _atBottom = BtwScroller.Offset.Y + BtwScroller.Viewport.Height >= BtwScroller.Extent.Height - 4;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = DataContext as MainViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null) return;
        if (e.PropertyName is nameof(MainViewModel.IsBtwOpen) or nameof(MainViewModel.BtwFollowUp) && _vm.IsBtwOpen)
        {
            if (e.PropertyName == nameof(MainViewModel.IsBtwOpen)) ScrollToNewest();
            Dispatcher.UIThread.Post(() => BtwQuestionBox.Focus(), DispatcherPriority.Background);
        }
        else if (e.PropertyName == nameof(MainViewModel.IsBtwRunning) && _vm.IsBtwRunning)
            ScrollToNewest();
    }

    private void ScrollToNewest()
    {
        _atBottom = true;
        Dispatcher.UIThread.Post(() =>
        {
            BtwScroller.ScrollToEnd();
            // A low window scrolls the cards above the message box: the newest answer and the question box stay in view
            BtwQuestionBox.BringIntoView();
        }, DispatcherPriority.Background);
    }

    private void OnQuestionKey(object? sender, KeyEventArgs e)
    {
        if (_vm is not { } vm) return;
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            if (vm.AskBtwCommand.CanExecute(null)) vm.AskBtwCommand.Execute(null);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            vm.IsBtwOpen = false;
        }
    }
}
