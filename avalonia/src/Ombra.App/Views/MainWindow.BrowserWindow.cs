using System.ComponentModel;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App.Controls;
using Ombra.App.ViewModels;

namespace Ombra.App.Views;

/// <summary>
/// The rail's Browser page with Nightly (macOS): one whole Nightly browser window of the shared runtime
/// (<see cref="NightlyPane.IsBrowserWindow"/>; its sidebar, spaces, URL bar, library and menus are Nightly's own) in
/// BrowserWindowHost. It is made the first time the page shows with Nightly in use and lives while Nightly stays in use
/// (hidden, not closed, while another page shows, so its tabs and state stay); it goes when the built-in preview takes
/// over (Nightly not chosen, or the runtime cannot run: the page's fallback strip then says why). Showing the page gives
/// the window the keyboard; leaving it takes the keyboard away, which closes the window's open menus and panels.
/// Links opened in the Browser page (shared with the team) go to the window as new tabs.
/// </summary>
public sealed partial class MainWindow
{
    private NightlyPane? _browserWindow;

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private void WireBrowserWindow(MainViewModel vm)
    {
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ShowBrowserPage)) SyncBrowserWindow(vm, pageChanged: true);
        };
        vm.Browser.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PreviewViewModel.UseNightly)) SyncBrowserWindow(vm, pageChanged: false);
        };
        vm.OpenInBrowserWindow = vm.Browser.UseNightly ? OpenInBrowserWindow : null;
        SyncBrowserWindow(vm, pageChanged: false);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private void SyncBrowserWindow(MainViewModel vm, bool pageChanged)
    {
        vm.OpenInBrowserWindow = vm.Browser.UseNightly ? OpenInBrowserWindow : null;
        if (!vm.Browser.UseNightly)
        {
            CloseBrowserWindow();
            return;
        }
        if (vm.ShowBrowserPage) EnsureBrowserWindow(vm);
        if (!pageChanged || _browserWindow is not { } pane) return;
        if (vm.ShowBrowserPage) Dispatcher.UIThread.Post(() => pane.FocusPage(), DispatcherPriority.Loaded);
        else if (pane.IsKeyboardFocusWithin) Dispatcher.UIThread.Post(() => ReturnFocus(null), DispatcherPriority.Background);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private NightlyPane EnsureBrowserWindow(MainViewModel vm)
    {
        if (_browserWindow is { } existing) return existing;
        // The window is the sheet's rect: BrowserWindowHost (its clip) lies 1 DIP inside the sheet (MainWindow.axaml).
        // Its shortcuts are its own also while the rail (or nothing) has Ombra's focus: the page is the window's then,
        // and ⌘T must not go to the hidden built-in preview's tabs (OnFocusShortcut) and leave no new tab on screen.
        var pane = new NightlyPane
        {
            IsBrowserWindow = true,
            Margin = new Avalonia.Thickness(-1),
            TakesShortcutsUnfocused = () => vm is { ShowBrowserPage: true, IsSettingsOpen: false, IsPaletteOpen: false, IsShortcutsOpen: false, IsImageViewerOpen: false }
                && !vm.Share.IsDialogOpen
                && (FocusManager?.GetFocusedElement() is not Avalonia.Visual focused || IconRail.IsVisualAncestorOf(focused)),
        };
        // Nightly cannot show the window (it cannot start, or gave up restarting): the built-in preview takes the page,
        // with the reason on its fallback strip (Try again brings the window back).
        pane.RuntimeUnusable += reason => Dispatcher.UIThread.Post(() => vm.Browser.ReportNightlyUnusable(reason));
        // Its last tab closed with the window (or Nightly closed it): a new one, as the page always shows a browser.
        pane.PageClosed += () => Dispatcher.UIThread.Post(() =>
        {
            if (_browserWindow != pane) return;
            CloseBrowserWindow();
            if (vm.Browser.UseNightly && vm.ShowBrowserPage) EnsureBrowserWindow(vm);
        });
        _browserWindow = pane;
        BrowserWindowHost.Child = pane;
        return pane;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private void CloseBrowserWindow()
    {
        if (_browserWindow is not { } pane) return;
        _browserWindow = null;
        BrowserWindowHost.Child = null;
        pane.Close();
    }

    /// <summary>A link for the Browser page: a new tab of its Nightly window (made now if the page never showed).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private void OpenInBrowserWindow(Uri uri)
    {
        if (Vm is not { } vm) return;
        EnsureBrowserWindow(vm).Navigate(uri);
    }
}
