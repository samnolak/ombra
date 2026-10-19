using Avalonia;
using Avalonia.Controls.Primitives;
using Ombra.App.Localization;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore.Teams;

namespace Ombra.Tests;

/// <summary>
/// The rail's Team page and Settings › Account in the real window: signed out (forms), then a team snapshot as the
/// session delivers it — members with names and roles, a join request, devices, the banners (state not confirmed,
/// key changed, removed, equivocation) and the owner's equivocation screen with what each choice undoes.
/// The server flows themselves are covered by Ombra.Teams.Tests/TeamSessionTests against the real server.
/// </summary>
public sealed class TeamUiTests
{
    private static readonly string ShotDir = Environment.GetEnvironmentVariable("OMBRA_SHOT_DIR") ?? Path.Combine(AppContext.BaseDirectory, "screenshots");

    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name) ?? throw new InvalidOperationException($"no {typeof(T).Name} named {name}");

    private static void Settle(Window w)
    {
        for (var i = 0; i < 3; i++) { Dispatcher.UIThread.RunJobs(); w.UpdateLayout(); }
    }

    private static void Shot(Window w, string name)
    {
        Settle(w);
        Directory.CreateDirectory(ShotDir);
        using var frame = w.CaptureRenderedFrame();
        using var file = File.Create(Path.Combine(ShotDir, name + ".png"));
        frame?.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private static async Task<(MainWindow W, MainViewModel Vm)> OpenAsync()
    {
        Environment.SetEnvironmentVariable("OMBRA_TEAMS_DIR", TestProcesses.TempDir("teams"));
        return await PanesUiTests.OpenAsync("normal", 1180, 900);
    }

    private static readonly Guid Me = Guid.Parse("0190a000-0000-7000-8000-000000000001");
    private static readonly Guid Ivy = Guid.Parse("0190a000-0000-7000-8000-000000000002");
    private static readonly Guid Max = Guid.Parse("0190a000-0000-7000-8000-000000000003");
    private static readonly Guid MyLaptop = Guid.Parse("0190a000-0000-7000-8000-0000000000a1");

    internal static TeamSnapshot Snapshot(bool confirmed = true, bool keyChanged = false, bool equivocation = false, bool removed = false)
    {
        var members = new List<TeamMember>
        {
            new(Me, "owner", "Olga", [new(Guid.Parse("0190a000-0000-7000-8000-0000000000a0"), "olga-mac", false, true), new(MyLaptop, "olga-air", false, false)], true, "aa"),
            new(Ivy, "admin", "Ivy", [], false, "bb"),
            new(Max, "member", null, [], false, "cc"),
        };
        var roster = new TeamRoster("4f1c9a2b00000000000000000000000000000000000000000000000000000000", 7, Me, members, true);
        var join = new JoinReview(Guid.NewGuid(), Guid.NewGuid(), roster.TeamId, Guid.Parse("0190a000-0000-7000-8000-000000000004"), [], true, true, "member", null);
        var eq = equivocation
            ? new EquivocationReview(roster.TeamId, 6, [1], [2], ["4f1c9a2b00"], ["77aa0c3d00"],
                [new JsonObject { ["kind"] = "removeMember", ["memberId"] = TeamsHex.Of(Max), ["role"] = "member" }],
                [new JsonObject { ["kind"] = "demote", ["memberId"] = TeamsHex.Of(Ivy), ["from"] = "admin", ["to"] = "member", ["restored"] = "admin" }])
            : null;
        var view = new TeamView(roster.TeamId, roster, true, true, new TeamConfirmation(confirmed, false, 0, 2), keyChanged ? [Ivy] : [], eq, null, [join]);
        return new TeamSnapshot("https://203.0.113.7:8443", Me, members[0].Devices[0].DeviceId, removed ? [] : [view], [], removed ? [roster.TeamId] : []);
    }

    [AvaloniaFact]
    public async Task Rail_team_page_signed_out_shows_create_join_and_pair_and_account_says_not_signed_in()
    {
        var (w, vm) = await OpenAsync();
        try
        {
            Named<Button>(w, "RailTeamButton").Command!.Execute(null);
            Settle(w);
            Assert.True(vm.ShowTeamPage);
            Assert.True(vm.IsTeamRailOn);
            Assert.False(vm.IsChatsRailOn);
            Assert.True(Named<Control>(w, "TeamPageHost").IsVisible);
            Assert.True(Named<Control>(w, "SignedOutForms").IsVisible);
            Assert.False(Named<Control>(w, "TeamSections").IsVisible);
            Assert.True(Named<TextBox>(w, "InviteLinkBox").IsEffectivelyVisible);
            Assert.True(Named<TextBox>(w, "ServerAddressBox").IsEffectivelyVisible);
            Assert.True(Named<TextBox>(w, "PairLinkBox").IsEffectivelyVisible);
            Shot(w, "team-signed-out-dark");
            vm.SettingsTheme = "light";
            Shot(w, "team-signed-out-light");

            vm.ShowChatsCommand.Execute(null);
            Assert.False(vm.ShowTeamPage);
            Assert.True(vm.IsChatsRailOn);

            await vm.OpenSettingsAtAsync("account");
            Settle(w);
            Assert.True(Named<Control>(w, "AccountSignedOut").IsEffectivelyVisible);
            Assert.False(Named<Control>(w, "AccountCard").IsVisible);
        }
        finally { await vm.DisposeAsync(); w.Close(); }
    }

    /// <summary>
    /// The Team page is a page of its own, as the Browser page: no chats sidebar beside it (New chat, the chats, the
    /// project folders), its sheet right after the rail, and ⌘B (with its menu item and palette entry) off there, so it
    /// cannot change the sidebar behind the page. Back on Chats the sidebar is as it was.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ru")]
    public async Task Team_and_browser_pages_take_the_sidebars_place_and_cmd_b_is_off_there(string language)
    {
        var settingsPath = Path.Combine(TestProcesses.TempDir("team-no-sidebar-" + language), "settings.json");
        var previous = Loc.Current;
        Loc.Load(language, settingsPath);
        var (w, vm) = await OpenAsync();
        try
        {
            var sidebar = Named<Border>(w, "Sidebar");
            var rail = Named<Border>(w, "IconRail");
            var mod = (Avalonia.Input.RawInputModifiers)w.GetPlatformSettings()!.HotkeyConfiguration.CommandModifiers;
            Settle(w);
            Assert.True(vm.ShowSidebar);
            Assert.True(sidebar.IsEffectivelyVisible);

            foreach (var (open, host) in new (Action, string)[]
            {
                (() => Named<Button>(w, "RailTeamButton").Command!.Execute(null), "TeamPageHost"),
                (() => Named<Button>(w, "RailBrowserButton").Command!.Execute(null), "BrowserPage"),
            })
            {
                open();
                Settle(w);
                var page = Named<Control>(w, host);
                Assert.True(page.IsVisible, host);
                Assert.False(vm.ShowSidebar, host);
                Assert.False(sidebar.IsVisible, host);
                // The page has the sidebar's width: its sheet starts where the rail ends
                var pageLeft = page.TranslatePoint(default, w)!.Value.X;
                var railRight = rail.TranslatePoint(new Avalonia.Point(rail.Bounds.Width, 0), w)!.Value.X;
                Assert.Equal(railRight, pageLeft, 0.5);
                // ⌘B does nothing here, nor behind the page
                Assert.False(vm.ToggleSidebarCommand.CanExecute(null), host);
                w.KeyPress(Avalonia.Input.Key.B, mod, Avalonia.Input.PhysicalKey.B, null);
                Settle(w);
                Assert.True(vm.IsSidebarVisible, host);
                Assert.False(sidebar.IsVisible, host);
            }
            Shot(w, $"browser-page-no-sidebar-{language}");
            Named<Button>(w, "RailTeamButton").Command!.Execute(null);
            Settle(w);
            vm.SettingsTheme = "dark";
            Shot(w, $"team-page-no-sidebar-dark-{language}");
            vm.SettingsTheme = "light";
            Shot(w, $"team-page-no-sidebar-light-{language}");
            Assert.Contains(Loc.T("Team"), w.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text));
            if (language == "ru") Assert.NotEqual("Available in Chats", Loc.T("Available in Chats"));

            vm.ShowChatsCommand.Execute(null);
            Settle(w);
            Assert.True(vm.ShowSidebar);
            Assert.True(sidebar.IsEffectivelyVisible);
            Assert.True(vm.ToggleSidebarCommand.CanExecute(null));
            w.KeyPress(Avalonia.Input.Key.B, mod, Avalonia.Input.PhysicalKey.B, null);
            Settle(w);
            Assert.False(vm.ShowSidebar);
        }
        finally { await vm.DisposeAsync(); w.Close(); Loc.Load(previous, settingsPath); }
    }

    [AvaloniaFact]
    public async Task Team_snapshot_lists_members_roles_joins_devices_and_banners()
    {
        var (w, vm) = await OpenAsync();
        try
        {
            vm.OpenTeamPageCommand.Execute(null);
            vm.Team.Show(Snapshot(confirmed: false, keyChanged: true));
            Settle(w);
            Assert.True(Named<Control>(w, "TeamSections").IsVisible);
            Assert.False(Named<Control>(w, "SignedOutForms").IsVisible);
            Assert.Equal(["Olga", "Ivy", Ombra.App.Localization.Loc.T("(no name yet)")], vm.Team.MemberRows.Select(r => r.Name));
            Assert.Equal(["owner", "admin", "member"], vm.Team.MemberRows.Select(r => r.Member.Role));
            Assert.False(vm.Team.MemberRows[0].CanRemove); // me
            Assert.True(vm.Team.MemberRows[1].CanRemove);
            Assert.True(vm.Team.MemberRows[1].CanMakeOwner);
            Assert.True(vm.Team.MemberRows[1].KeyChanged);
            Assert.Equal(2, vm.Team.DeviceRows.Count);
            Assert.False(vm.Team.DeviceRows.Single(d => d.IsThis).CanRevoke);
            Assert.True(vm.Team.DeviceRows.Single(d => !d.IsThis).CanRevoke);
            Assert.Single(vm.Team.JoinRows);
            Assert.True(Named<Control>(w, "JoinRequests").IsEffectivelyVisible);
            Assert.True(Named<Control>(w, "NotConfirmedBanner").IsEffectivelyVisible);
            Assert.Equal(TeamViewModel.ConfirmationText(2, 0), vm.Team.NotConfirmedText);
            Assert.DoesNotContain("(", vm.Team.NotConfirmedText);
            Assert.Contains(Loc.Plural(1, "So far {0} admin has confirmed it.", "So far {0} admins have confirmed it."), TeamViewModel.ConfirmationText(2, 1));
            Assert.DoesNotContain("{0}", TeamViewModel.ConfirmationText(1, 0));
            // IDs stay out of the page: in tooltips and under the folded Technical details.
            var texts = w.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();
            Assert.DoesNotContain(texts, t => t.Contains(TeamsHex.Of(Ivy)[^8..]) || t.Contains("4f1c9a2b"));
            Assert.False(Named<ToggleButton>(w, "TeamTechToggle").IsChecked == true);
            Assert.True(Named<Control>(w, "KeyChangedBanner").IsEffectivelyVisible);
            Assert.Contains("Ivy", vm.Team.KeyChangedText);
            Assert.False(Named<Control>(w, "EquivocationBanner").IsVisible);
            Assert.True(Named<Control>(w, "InviteCard").IsEffectivelyVisible);
            // Links: a ready status and Copy; the secret shows only on "Show link".
            vm.Team.Invite = "ombra://join?s=https%3A%2F%2F203.0.113.7%3A8443&pin=" + new string('e', 64) + "&t=" + new string('a', 64) + "#k=" + new string('k', 43);
            Settle(w);
            Assert.True(Named<Control>(w, "InviteStatus").IsEffectivelyVisible);
            var invite = Named<SelectableTextBlock>(w, "InviteText");
            Assert.False(invite.IsEffectivelyVisible);
            Assert.Null(ToolTip.GetTip(invite));
            Named<ToggleButton>(w, "ShowInviteToggle").IsChecked = true;
            Settle(w);
            Assert.True(invite.IsEffectivelyVisible);
            Named<ToggleButton>(w, "ShowInviteToggle").IsChecked = false;
            Settle(w);
            // pet-covers: on a full page the pet covers nothing (it stays with the conversation's message box)
            Assert.Empty(PetsTests.PetCovers(w));
            Assert.False(Named<Control>(w, "PetPerch").IsEffectivelyVisible);
            Shot(w, "team-members-banners-dark");
            vm.SettingsTheme = "light";
            Shot(w, "team-members-banners-light");
            vm.SettingsTheme = "dark";
            foreach (var language in (string[])["en", "ru"]) LaidOutIn(language, vm);
        }
        finally { await vm.DisposeAsync(); w.Close(); }
    }

    /// <summary>
    /// The Team page built in <paramref name="language"/> (its own window): one list card per section, rows ending on one
    /// edge, the invite's picker as high as its pill; Remove… asks first under the row. Synchronous on purpose: the
    /// language is global (Loc), and an await while it is switched would let other tests on the UI thread read it.
    /// </summary>
    private static void LaidOutIn(string language, MainViewModel vm)
    {
        var settingsPath = Path.Combine(TestProcesses.TempDir("team-lang-" + language), "settings.json");
        var previous = Loc.Current;
        Loc.Load(language, settingsPath);
        var w = new Window { Width = 900, Height = 1500 };
        try
        {
            vm.Team.Show(Snapshot(confirmed: false, keyChanged: true));
            vm.Team.Invite = "ombra://join?s=https%3A%2F%2F203.0.113.7%3A8443&pin=" + new string('e', 64) + "&t=" + new string('a', 64) + "#k=" + new string('k', 43);
            w.Content = new Ombra.App.Views.Team.TeamPage { DataContext = vm };
            w.Show();
            Settle(w);
            Assert.True(Named<Control>(w, "InviteStatus").IsEffectivelyVisible);
            var found = LayoutAudit.Run(w);
            Assert.True(found.Count == 0, LayoutAudit.Report("team-" + language, found));
            Shot(w, $"team-page-{language}-dark");
            vm.SettingsTheme = "light";
            Shot(w, $"team-page-{language}-light");
            // Remove… in the row's menu asks first, under the row; the safety number opens there too
            var ivy = vm.Team.MemberRows[1];
            ivy.SafetyNumber = "28954 32512 14192 76535 12607 33173 78235 64224 25706 29617 16484 86666"; // the session computes it (FeatureSmoke.Team)
            ivy.AskRemoveCommand.Execute(null);
            Settle(w);
            Assert.True(ivy.IsConfirmingRemove);
            Assert.Equal(Loc.F("Remove “{0}”?", "Ivy"), ivy.RemoveQuestion);
            if (language == "ru") Assert.NotEqual("Remove “Ivy”?", ivy.RemoveQuestion);
            ivy.CancelRemoveCommand.Execute(null);
            Assert.False(ivy.IsConfirmingRemove);
            ivy.AskRemoveCommand.Execute(null);
            Settle(w);
            found = LayoutAudit.Run(w);
            Assert.True(found.Count == 0, LayoutAudit.Report("team-confirm-" + language, found));
            Shot(w, $"team-member-remove-{language}-light");
            vm.SettingsTheme = "dark";
            Shot(w, $"team-member-remove-{language}-dark");
        }
        finally { w.Close(); Loc.Load(previous, settingsPath); }
    }

    [AvaloniaFact]
    public async Task Equivocation_screen_lists_what_each_choice_undoes()
    {
        var (w, vm) = await OpenAsync();
        try
        {
            vm.OpenTeamPageCommand.Execute(null);
            vm.Team.Show(Snapshot(equivocation: true));
            Settle(w);
            Assert.True(Named<Control>(w, "EquivocationBanner").IsEffectivelyVisible);
            Assert.False(Named<Control>(w, "EquivocationCard").IsVisible);
            vm.Team.OpenEquivocationCommand.Execute(null);
            Settle(w);
            Assert.True(Named<Control>(w, "EquivocationCard").IsEffectivelyVisible);
            // keep the known version: undoes the other branch's demotion, said with the name and the role
            Assert.Equal(Loc.F("The demotion of {0} is undone: they are an admin again.", "Ivy"), Assert.Single(vm.Team.Undone));
            Named<RadioButton>(w, "SwitchRadio").IsChecked = true;
            Settle(w);
            Assert.False(vm.Team.KeepKnown);
            Assert.Equal(Loc.F("The removal of {0} is undone: they return to the team.", Loc.T("(no name yet)")), Assert.Single(vm.Team.Undone));
            Assert.DoesNotContain(vm.Team.Undone, u => u.Contains("kind") || u.Contains(TeamsHex.Of(Max)));
            Assert.Contains("77aa0c3d", vm.Team.SwitchText);
            Shot(w, "team-equivocation-dark");
            vm.SettingsTheme = "light";
            Shot(w, "team-equivocation-light");
        }
        finally { await vm.DisposeAsync(); w.Close(); }
    }

    [AvaloniaFact]
    public async Task Removed_banner_explains_the_wipe()
    {
        var (w, vm) = await OpenAsync();
        try
        {
            vm.OpenTeamPageCommand.Execute(null);
            vm.Team.Show(Snapshot(removed: true));
            Settle(w);
            Assert.True(Named<Control>(w, "RemovedBanner").IsEffectivelyVisible);
            Assert.False(Named<Control>(w, "TeamSections").IsVisible);
            Shot(w, "team-removed-dark");
        }
        finally { await vm.DisposeAsync(); w.Close(); }
    }
}
