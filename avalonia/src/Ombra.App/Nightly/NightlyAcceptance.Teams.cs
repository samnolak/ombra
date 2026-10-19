namespace Ombra.App.Nightly;

internal static partial class NightlyAcceptance
{
    private sealed partial class Run
    {
        /// <summary>
        /// The Browser page's runtime shares through this Ombra (B1/B2): its team requests (the inbox poll starts with
        /// the window) arrive on this process's own link, <see cref="NightlyTeamLink.EmbedServiceName"/>, and are
        /// accepted, even while another Ombra on this Mac holds the shared name (the page's share items then said
        /// "open Ombra on this Mac").
        /// </summary>
        private async Task TeamLinkCheck()
        {
            var runtime = BrowserWindow()?.RuntimePid ?? 0;
            var served = await Until(() => runtime != 0 && NightlyTeamLink.Embedded?.LastPeer == runtime, 20);
            Check("browser-window-team-link", served,
                $"link={NightlyTeamLink.Embedded?.Name ?? "not registered"} last peer={NightlyTeamLink.Embedded?.LastPeer} runtime={runtime}");
        }
    }
}
