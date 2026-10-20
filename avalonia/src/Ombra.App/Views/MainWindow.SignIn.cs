using Avalonia.Controls;
using Ombra.App.Controls;
using Ombra.App.Localization;
using Ombra.App.ViewModels;

namespace Ombra.App.Views;

/// <summary>
/// Ombra's sign-in window: a sign-in started with no chat on screen (Settings › Plugins › MCP) opens its page here
/// (<see cref="MainViewModel.SignInBrowser"/>, <see cref="LinkTarget.SignIn"/>), a browser of Ombra's own (built-in
/// preview or Nightly, as the Browser page) over the main window, where the page's redirect to omp's local callback
/// completes the sign-in. It closes when the sign-in is done (the model's browser goes) or when the user closes it.
/// </summary>
public sealed partial class MainWindow
{
    private Window? _signInWindow;

    private void WireSignInWindow(MainViewModel vm)
    {
        vm.SignInWindowRequested += () => ShowSignInWindow(vm);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SignInBrowser) && vm.SignInBrowser is null) _signInWindow?.Close();
        };
    }

    private void ShowSignInWindow(MainViewModel vm)
    {
        if (vm.SignInBrowser is not { } browser) return;
        if (_signInWindow is { } open)
        {
            open.Activate();
            return;
        }
        var window = new Window
        {
            // The window's model is the app's: links and menus in the pane find it (ConversationMenus.Owner)
            DataContext = vm,
            Title = Loc.T("Sign in"),
            Width = 560,
            Height = 760,
            MinWidth = 380,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new PreviewPanel { DataContext = browser, ShowTabStrip = true },
        };
        window.Bind(BackgroundProperty, window.GetResourceObservable("GuiChrome"));
        window.Closed += (_, _) =>
        {
            if (_signInWindow == window) _signInWindow = null;
            if (ReferenceEquals(vm.SignInBrowser, browser)) vm.SignInBrowser = null;
        };
        _signInWindow = window;
        window.Show(this);
    }
}
