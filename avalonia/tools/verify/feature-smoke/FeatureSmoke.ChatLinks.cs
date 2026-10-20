#if OMBRA_FEATURE_SMOKE
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Ombra.App.Localization;
using Ombra.App.ViewModels;

namespace Ombra.App.Smoke;

internal static partial class FeatureSmoke
{
    private sealed partial class Run
    {
        private (string, Func<Task>)[] ChatLinksSections() => [("chat-links", ChatLinks)];

        /// <summary>
        /// A link in a reply, clicked with the real mouse events: the right panel's Browser tool shows it in a tab, the chat
        /// stays on screen, nothing goes to the system's browser (MainViewModel.OpenLink); a second click is a second
        /// tab. ⌘-click opens the link's menu (Open in Browser page, Open in default browser) instead; its "Open in
        /// Browser page" shows the Browser page. "Open in default browser" is not chosen: it would start the Mac's browser.
        /// </summary>
        private async Task ChatLinks()
        {
            const string url = "https://example.com/ombra-chat-link";
            var system = new List<string>();
            void Launched(string u) => system.Add(u);
            vm.OpenUrlRequested += Launched;
            try
            {
                await Send($"see {url} please");
                HyperlinkButton? Link() => window.GetVisualDescendants().OfType<HyperlinkButton>()
                    .LastOrDefault(l => l.NavigateUri?.AbsoluteUri == url && l.IsEffectivelyVisible);
                Check("chat-link-shown", await Until(() => !vm.IsRunning && Link() is not null, 60));
                await Task.Delay(500);
                Shot("50-chat-link");

                Activate();
                await Until(() => window.IsActive, 5);
                await Task.Delay(1500); // the reply's footer and the transcript's scroll settle (the link moves until then)
                await LinkClick(Link()!);
                Check("chat-link-opens-in-panel", await Until(() => vm.ActivePane == SidePane.Browser && vm.Preview.CurrentUrl?.AbsoluteUri == url, 10),
                    $"pane={vm.ActivePane} url={vm.Preview.CurrentUrl}");
                await Task.Delay(1500);
                var tabs = vm.Preview.Tabs.Count;
                Check("chat-link-chat-stays", !vm.ShowBrowserPage && Link() is not null && system.Count == 0,
                    $"browser page={vm.ShowBrowserPage} link shown={Link() is not null} system=[{string.Join(", ", system)}]");
                Shot("51-chat-link-in-panel");

                await LinkClick(Link()!);
                Check("chat-link-second-tab", await Until(() => vm.Preview.Tabs.Count == tabs + 1 && vm.Preview.ActiveTab.IsOpenedByUser, 10)
                    && system.Count == 0, $"tabs {tabs} → {vm.Preview.Tabs.Count} system={system.Count}");

                // A quick second click would be a double click: wait it out
                await Task.Delay(800);
                await LinkClick(Link()!, command: true);
                Flyout? Menu() => Link()?.ContextFlyout is Flyout { IsOpen: true } f ? f : null;
                var opened = await Until(() => Menu() is not null, 5);
                Button? Item(string english) => (Menu()?.Content as Control)?.GetLogicalDescendants().OfType<Button>()
                    .FirstOrDefault(b => AutomationProperties.GetName(b) == Loc.T(english));
                await Task.Delay(400);
                Check("chat-link-cmd-click-menu", Menu() is not null && Item("Open in Browser page") is not null && Item("Open in default browser") is not null
                    && vm.Preview.Tabs.Count == tabs + 1 && system.Count == 0, $"menu={opened} open now={Menu() is not null} tabs={vm.Preview.Tabs.Count}");
                ShotScreenRegion("52-chat-link-cmd-menu");
                Key("\u001b", 0x35);
                Check("chat-link-cmd-menu-closes", await Until(() => Menu() is null, 5));

                // Its right-click menu: the same rows
                RightClick(Link()!);
                await Until(() => Menu() is not null, 5);
                await Task.Delay(400);
                Check("chat-link-right-click-menu", Menu() is not null && Item("Open in Browser page") is not null && Item("Open in default browser") is not null);
                ShotScreenRegion("53-chat-link-right-click-menu");
                Click(Item("Open in Browser page")!);
                // With Nightly the page is its Nightly window (the tab is Nightly's); with the built-in preview, its own tabs
                Check("chat-link-browser-page", await Until(() => vm.ShowBrowserPage && (vm.OpenInBrowserWindow is not null || vm.Browser.CurrentUrl?.AbsoluteUri == url), 10)
                    && system.Count == 0, $"browser page={vm.ShowBrowserPage} nightly window={vm.OpenInBrowserWindow is not null} url={vm.Browser.CurrentUrl}");
                await Task.Delay(1000);
                Shot("54-chat-link-browser-page");
            }
            finally
            {
                vm.OpenUrlRequested -= Launched;
                vm.ShowChatsCommand.Execute(null);
            }
        }

        /// <summary>
        /// A left click on a link at a person's pace, as posted mouse events (⌘ held: the flags ride on the mouse events,
        /// no key event). The pointer arrives first and the press waits a few frames: hovering re-lays the reply's text,
        /// which puts the link back into the tree, and it is hit-tested only once a frame has drawn it again; a press
        /// posted together with the move would land on the text under it.
        /// </summary>
        private async Task LinkClick(Control c, bool command = false)
        {
            var flags = command ? 1UL << 20 : 0; // NSEventModifierFlagCommand
            var at = WindowPoint(c, 0.5, 0.5);
            Post(MouseEvent(5, at, 0, NsWindow, flags));
            await Task.Delay(200);
            Post(MouseEvent(1, at, 1, NsWindow, flags));
            await Task.Delay(80);
            Post(MouseEvent(2, at, 1, NsWindow, flags));
            Log($"{(command ? "⌘-click" : "click")} {c.GetType().Name} at ({at.X:0},{at.Y:0})");
            await Task.Delay(100);
        }
    }
}
#endif
