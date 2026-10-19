#if OMBRA_FEATURE_SMOKE
using Avalonia.Controls;
using Avalonia.VisualTree;
using Ombra.App.ViewModels;
using Ombra.ClientCore.Teams;

namespace Ombra.App.Smoke;

internal static partial class FeatureSmoke
{
    private sealed partial class Run
    {
        private (string, Func<Task>)[] TeamSections() => [("team", TeamPage)];

        /// <summary>Another member's device, driven in-process (its keys in memory, its profile under the smoke's folder).</summary>
        private sealed class MemoryKeys : ITeamsKeyStore
        {
            private readonly Dictionary<string, byte[]> _items = [];
            public byte[]? Read(string account) => _items.TryGetValue(account, out var v) ? v.ToArray() : null;
            public void Write(string account, ReadOnlySpan<byte> secret) => _items[account] = secret.ToArray();
            public void Delete(string account) => _items.Remove(account);
        }

        private sealed class Machine(string id) : IMachineIdentity
        {
            public byte[] MachineId() => System.Text.Encoding.UTF8.GetBytes(id);
        }

        private async Task RefreshTeam() => vm.Team.Show(await Task.Run(() => vm.Team.Session!.RefreshAsync()));

        private async Task Themed(string name)
        {
            vm.SettingsTheme = "dark";
            await Task.Delay(400);
            Shot(name + "-dark");
            vm.SettingsTheme = "light";
            await Task.Delay(400);
            Shot(name + "-light");
            vm.SettingsTheme = "dark";
        }

        /// <summary>
        /// The rail's Team against a real team server (team-server.sh; OMBRA_FEATURE_SMOKE_TEAM = address|pin|code): create
        /// a team (recovery key once), name, invite; a second member joins and is approved; members with names and roles,
        /// safety number; pairing a device of mine (QR, six words) and revoking it; a changed key; the equivocation screen
        /// (its state injected: a lying server is not part of this run); Settings › Account; ownership handed over and the
        /// new owner removing me (the removed banner); sign out.
        /// </summary>
        private async Task TeamPage()
        {
            if (Environment.GetEnvironmentVariable("OMBRA_FEATURE_SMOKE_TEAM") is not { } spec || spec.Split('|') is not [var address, var pin, var code])
            {
                Check("team-server", false, "OMBRA_FEATURE_SMOKE_TEAM not set (team-server.sh up)");
                return;
            }
            var others = Path.Combine(dir, "team-others");
            var t = vm.Team;
            vm.OpenTeamPageCommand.Execute(null);
            Check("team-rail", vm.ShowTeamPage && vm.IsTeamRailOn && t.IsSignedOut, $"signedIn={t.IsSignedIn}");
            await Themed("50-team-signed-out");

            t.ServerAddress = address;
            t.ServerPin = pin;
            t.BootstrapCode = code;
            await t.CreateTeamCommand.ExecuteAsync(null);
            Check("team-created", t.HasRecoveryKey && t.HasTeam && t.IsOwner, $"error={t.Error}");
            await Themed("51-team-recovery-key");
            t.DismissRecoveryKeyCommand.Execute(null);
            Check("recovery-key-gone", !t.HasRecoveryKey);

            t.DisplayName = "Olga";
            await t.SaveNameCommand.ExecuteAsync(null);
            await t.CreateInviteCommand.ExecuteAsync(null);
            Check("invite-link", t.Invite?.Contains("#k=") == true, $"error={t.Error}");

            using var ivy = await Task.Run(() => TeamSession.JoinAsync(Path.Combine(others, "ivy"), null, t.Invite!, new MemoryKeys(), new Machine("ivy")));
            await Task.Run(() => ivy.SetNameAsync("Ivy"));
            await RefreshTeam();
            Check("join-request", t.JoinRows.Count == 1, $"joins={t.JoinRows.Count} error={t.Error}");
            await Themed("52-team-join-request");
            await t.JoinRows[0].ApproveCommand.ExecuteAsync(null);
            await Task.Run(async () => { await ivy.RefreshAsync(); await ivy.RefreshAsync(); });
            await RefreshTeam();
            var ivyRow = t.MemberRows.FirstOrDefault(r => !r.IsMe);
            Check("members", t.MemberRows.Count == 2 && ivyRow?.Name == "Ivy" && t.MemberRows.Single(r => r.IsMe).Name == "Olga",
                $"rows=[{string.Join(", ", t.MemberRows.Select(r => r.Name + ":" + r.Member.Role))}]");
            ivyRow?.ShowSafetyNumberCommand.Execute(null);
            Check("safety-number", ivyRow?.SafetyNumber?.Count(char.IsDigit) == 60, ivyRow?.SafetyNumber ?? "");
            await Themed("53-team-members");
            // Remove… from the row's "⋯" asks first, under the row
            ivyRow?.AskRemoveCommand.Execute(null);
            Check("remove-asks-first", ivyRow is { IsConfirmingRemove: true } && t.MemberRows.Count == 2);
            await Themed("53b-team-member-remove-confirm");
            ivyRow?.CancelRemoveCommand.Execute(null);
            ivyRow?.ShowSafetyNumberCommand.Execute(null);

            // A device of mine: link + QR here, the new device answers, both show the same six words.
            await t.StartPairingCommand.ExecuteAsync(null);
            Check("pairing-link", t.IsPairing && t.PairingQr is not null, $"error={t.Error}");
            var (laptop, newSide) = await Task.Run(() => TeamSession.PairNewDeviceAsync(Path.Combine(others, "laptop"), t.PairingLink!, new MemoryKeys(), new Machine("laptop")));
            using var _ = laptop;
            var words = await Task.Run(() => newSide.WaitForWordsAsync(TimeSpan.FromMilliseconds(300)));
            Check("pairing-words", await Until(() => t.PairingWords == string.Join(" ", words), 20), $"here={t.PairingWords} there={string.Join(" ", words)}");
            await Themed("54-team-pairing");
            await t.ConfirmPairingCommand.ExecuteAsync(null);
            await Task.Run(() => laptop.FinishPairingAsync(newSide));
            await RefreshTeam();
            Check("devices", t.DeviceRows.Count == 2, $"devices={t.DeviceRows.Count} error={t.Error}");
            await Themed("55-team-devices");
            await t.DeviceRows.First(d => !d.IsThis).RevokeCommand.ExecuteAsync(null);
            Check("device-revoked", t.DeviceRows.Any(d => d.Device.Revoked), $"error={t.Error}");

            // A member key that differs from the one accepted before (as after a root key change).
            t.Session!.Account.Fingerprints[t.SelectedTeamId!][TeamsHex.Of(ivyRow!.Member.MemberId)] = "00";
            await RefreshTeam();
            Check("key-changed", t.KeyChanged && t.MemberRows.Any(r => r.KeyChanged), t.KeyChangedText);
            await Themed("56-team-key-changed");
            t.MemberRows.First(r => r.KeyChanged).ConfirmKeyCommand.Execute(null);
            await RefreshTeam();
            Check("key-confirmed", !t.KeyChanged);

            // The equivocation screen (state injected on top of the real snapshot).
            var real = t.Session.Last!;
            var view = real.Teams[0];
            var ivyId = TeamsHex.Of(view.Roster.Members.First(m => !m.IsMe).MemberId);
            var review = new EquivocationReview(view.TeamId, view.Roster.Version, [1], [2], [TeamsHex.Of(view.Roster.Owner)], ["77aa0c3d11"],
                [new System.Text.Json.Nodes.JsonObject { ["kind"] = "removeMember", ["memberId"] = ivyId, ["role"] = "member" }],
                [new System.Text.Json.Nodes.JsonObject { ["kind"] = "demote", ["memberId"] = ivyId, ["from"] = "admin", ["to"] = "member", ["restored"] = "admin" }]);
            t.Show(real with { Teams = [view with { Equivocation = review, Confirmation = view.Confirmation! with { Confirmed = false } }] });
            t.OpenEquivocationCommand.Execute(null);
            Check("equivocation-screen", t.IsEquivocationOpen && t.Undone is [var undone] && undone.Contains("Ivy") && !undone.Contains("kind") && t.NotConfirmed,
                string.Join(" | ", t.Undone));
            await Task.Delay(200);
            window.GetVisualDescendants().OfType<Avalonia.Controls.Border>().FirstOrDefault(b => b.Name == "NotConfirmedBanner")?.BringIntoView();
            await Themed("57-team-equivocation");
            t.CloseEquivocationCommand.Execute(null);
            await RefreshTeam();

            await vm.OpenSettingsAtAsync("account");
            Check("account", vm.IsSettingsOpen && t.MyId.Length == 32 && t.Server.Length > 0, $"id={t.MyId}");
            await Themed("58-account");
            vm.IsSettingsOpen = false;
            vm.OpenTeamPageCommand.Execute(null);

            // Ownership to Ivy; Ivy, now owner, removes me: the removed banner and the wipe.
            await t.MemberRows.First(r => !r.IsMe).MakeOwnerCommand.ExecuteAsync(null);
            await Task.Run(async () => { await ivy.RefreshAsync(); await ivy.AcceptOwnershipAsync(ivy.Account.Teams[0]); });
            await RefreshTeam();
            Check("ownership-transferred", !t.IsOwner && t.IsAdmin && t.MemberRows.First(r => !r.IsMe).Member.IsOwner, $"error={t.Error}");
            await Task.Run(() => ivy.RemoveMemberAsync(ivy.Account.Teams[0], t.MyMemberId!.Value));
            await RefreshTeam();
            Check("removed-banner", t.WasRemoved && !t.HasTeam, $"error={t.Error}");
            await Themed("59-team-removed");

            t.SignOutCommand.Execute(null);
            Check("signed-out", t.IsSignedOut);

            // The pet lives with the conversation only: not over Settings › Pets (its preview is in the page).
            await vm.OpenSettingsAtAsync("pets");
            Check("pet-not-on-settings", !vm.ShowPet && Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.Control>(window, "PetPerch")?.IsEffectivelyVisible == false);
            var preview = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Ombra.App.Controls.OniView>().FirstOrDefault(v => v.Name == "PetPreview");
            Check("pet-preview-drawn", await Until(() => preview is { IsEffectivelyVisible: true, Atlases.IsReady: true, LastScene: not null }, 10),
                $"visible={preview?.IsEffectivelyVisible} ready={preview?.Atlases?.IsReady} scene={preview?.LastScene is not null}");
            await Themed("60-settings-pets");
            vm.IsSettingsOpen = false;
            vm.ShowChatsCommand.Execute(null);
            Check("pet-in-chats", vm.ShowPet);
        }
    }
}
#endif
