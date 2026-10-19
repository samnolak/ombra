#if OMBRA_FEATURE_SMOKE
using System.Net;
using System.Text;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Ombra.App.Controls;
using Ombra.App.ViewModels;
using Ombra.ClientCore;
using Ombra.ClientCore.Teams;

namespace Ombra.App.Smoke;

internal static partial class FeatureSmoke
{
    private sealed partial class Run
    {
        private (string, Func<Task>)[] SharedMarksSections() => [("shared-marks", SharedMarksFlow)];

        /// <summary>
        /// K4 shared marks (P5 §4) in the real window over an in-memory team (Alice = this window, Bob and Carol): "Send
        /// to…" everyone from the message box's marks; the recipients' seen/done/dismissed with notes show on the Team
        /// page; Bob's marks arrive as a toast, open the page in the browser pane at the purple pin (scrolled to it) and
        /// the file at the line, become seen, and done with a short note reaches Bob.
        /// </summary>
        private async Task SharedMarksFlow()
        {
            Activate();
            var hub = new SmokeHub();
            var alice = new SmokeTeam(hub, SmokeHub.Alice);
            var bob = new SmokeItems(hub, TeamsHex.Of(SmokeHub.Bob));
            var carol = new SmokeItems(hub, TeamsHex.Of(SmokeHub.Carol));
            vm.TeamMarks.Attach(alice);
            Check("shared-attach", vm.TeamMarks.CanShare && vm.TeamMarks.Recipients.Count == 3, $"recipients={vm.TeamMarks.Recipients.Count}");

            using var site = SmokeSite.Start();
            var root = vm.ProjectRoot ?? Environment.CurrentDirectory;
            File.WriteAllLines(Path.Combine(root, "shared-demo.py"), Enumerable.Range(1, 60).Select(i => $"value_{i} = {i}  # line {i}"));

            // Alice's marks: one on code, one on the page
            vm.Marks.Items.Clear();
            vm.AddCodeMark(new CodeAnchor("shared-demo.py", 12, false, "value_12 = 12  # line 12", CodeMarkOrigin.Editor), "Rename value_12");
            Check("shared-page-mark", vm.Preview.OnPageMessage(PageMessage(site.Url, "smoke-shared-pin", "Make Buy bigger")) == "annotation");
            vm.ActivePane = SidePane.None;
            await Task.Delay(600);
            var sendTo = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "SendMarksToButton" && b.IsEffectivelyVisible);
            Check("shared-send-to-visible", sendTo is not null);
            if (sendTo is null) return;
            Click(sendTo);
            Button? Confirm() => sendTo.Flyout is Flyout { IsOpen: true, Content: Control c }
                ? c.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "SendMarksConfirm" && b.IsEffectivelyVisible) : null;
            Check("shared-send-flyout", await Until(() => Confirm() is not null, 5));
            await Task.Delay(400);
            ShotScreenRegion("60-shared-send-to");
            if (Confirm() is not { } confirm) return;
            Click(confirm);
            Check("shared-sent", await Until(() => hub.Items.Count == 4 && vm.Marks.Items.Count == 0, 5), $"items={hub.Items.Count} marks={vm.Marks.Items.Count}");

            // Bob: seen, then done with a note on the code mark; Carol dismisses the page mark with a note
            foreach (var i in hub.Items.Where(i => i.RecipientMemberId == bob.Me).ToList()) await bob.SetStatusAsync(SmokeHub.Team, i.AuthorMemberId, i.ItemId, TeamItemStatuses.Seen);
            var bobCode = hub.Items.First(i => i.RecipientMemberId == bob.Me && SharedMarks.FromItem(i)!.Mark.Code is not null);
            await bob.SetStatusAsync(SmokeHub.Team, bobCode.AuthorMemberId, bobCode.ItemId, TeamItemStatuses.Done, "Renamed to total");
            var carolPage = hub.Items.First(i => i.RecipientMemberId == carol.Me && SharedMarks.FromItem(i)!.Mark.Page is not null);
            await carol.SetStatusAsync(SmokeHub.Team, carolPage.AuthorMemberId, carolPage.ItemId, TeamItemStatuses.Dismissed, "Already big enough");
            vm.OpenTeamPageCommand.Execute(null);
            await Task.Delay(800);
            var card = window.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "SharedMarksCard" && b.IsEffectivelyVisible);
            Check("shared-card-visible", card is not null);
            card?.BringIntoView();
            await Task.Delay(500);
            var sentRecipients = vm.TeamMarks.Sent.SelectMany(s => s.Recipients).ToList();
            Check("shared-sender-statuses", sentRecipients.Any(r => r.Name == "Bob" && r.StatusText == Localization.Loc.T("Done") && r.Note == "Renamed to total")
                && sentRecipients.Any(r => r.Name == "Carol" && r.StatusText == Localization.Loc.T("Dismissed") && r.Note == "Already big enough"),
                string.Join(" | ", sentRecipients.Select(r => $"{r.Name}:{r.StatusText}:{r.Note}")));
            Shot("61-shared-sender-statuses");

            // Bob leaves Alice two marks on the page and one on the file: two toasts at most, one per object
            vm.IsTeamPageOpen = false;
            await Task.Delay(300);
            var bobPage = new LocalMark("bob-p", "This button overlaps the footer", Page: new PageAnchor(site.Url, "Shop",
                new PageMark(PageMarkKind.Point, 40, 20, 0, 0, 0, 0, 20, 10, 1280, 800), new PageElement("#buy", "button", "<button id=\"buy\">", "Buy", "", 0, 0, 80, 30), []));
            await bob.SendAsync(SmokeHub.Team, SharedMarks.ToDraft(bobPage, "bob1"), [TeamsHex.Of(SmokeHub.Alice)]);
            var toastText = Localization.Loc.F("Comments from {0} in {1}", "Bob", "Shop");
            Check("shared-toast", await Until(() => vm.SessionCard?.Title == toastText, 15), $"card='{vm.SessionCard?.Title}'");
            await Task.Delay(500);
            Shot("62-shared-toast");

            // Open from the toast: the browser pane shows the page and scrolls to Bob's pin; the mark becomes seen
            vm.SessionCard?.Primary?.Command.Execute(null);
            Check("shared-page-opened", await Until(() => vm.Preview.CurrentUrl is { } u && u.AbsoluteUri.StartsWith(site.Url, StringComparison.Ordinal), 15),
                $"url={vm.Preview.CurrentUrl}");
            Check("shared-page-revealed", await Until(() => vm.Preview.PendingReveal is null, 15), $"pending={vm.Preview.PendingReveal}");
            await Task.Delay(1200);
            Shot("63-shared-page-pin");
            Check("shared-page-seen", vm.TeamMarks.Received.Any(r => !r.IsCode && r.IsSeen));

            // Bob's file mark comes from the same repository on another commit: opening warns the line may have moved
            var aliceRepo = await Until(() => vm.TeamMarks.LocalRepo is not null, 10) ? vm.TeamMarks.LocalRepo : null;
            Check("shared-repo-identity", aliceRepo is { Commit.Length: 40 }, $"repo={aliceRepo}");
            var bobRepo = aliceRepo is null ? null : aliceRepo with { Commit = "0123456789abcdef0123456789abcdef01234567" };
            var bobFile = new LocalMark("bob-c", "Use a loop here", Code: new CodeAnchor("shared-demo.py", 30, false, "value_30 = 30  # line 30", Before: ["value_29 = 29  # line 29"]));
            await bob.SendAsync(SmokeHub.Team, SharedMarks.ToDraft(bobFile, "bob2", bobRepo), [TeamsHex.Of(SmokeHub.Alice)]);
            var fileToast = Localization.Loc.F("Comments from {0} in {1}", "Bob", "shared-demo.py");
            Check("shared-file-toast", await Until(() => vm.SessionCard?.Title == fileToast, 15), $"card='{vm.SessionCard?.Title}'");
            vm.SessionCard?.Primary?.Command.Execute(null);
            var editorShown = await Until(() => window.GetVisualDescendants().OfType<DocumentEditor>().Any(e => e.IsEffectivelyVisible && e.WorkspaceDocument is not null), 15);
            var editor = window.GetVisualDescendants().OfType<DocumentEditor>().FirstOrDefault(e => e.IsEffectivelyVisible && e.WorkspaceDocument is not null);
            await Task.Delay(800);
            var caretLine = editor?.Editor.TextArea.Caret.Line ?? 0;
            Check("shared-file-opened", editorShown && caretLine == 30, $"caret={caretLine}");
            var movedTitle = Localization.Loc.F("Left on commit {0}: the line may have moved.", "0123456");
            Check("shared-file-moved-warning", await Until(() => vm.SessionCard?.Title == movedTitle, 10), $"card='{vm.SessionCard?.Title}'");
            await Task.Delay(400);
            Shot("64-shared-file-line");

            // Done with a short note: Bob sees it on his copy
            vm.OpenTeamPageCommand.Execute(null);
            await Task.Delay(600);
            var row = vm.TeamMarks.Received.First(r => r.IsCode);
            row.ReplyNote = "Done in a loop, see line 30";
            await row.DoneCommand.ExecuteAsync(null);
            var bobSees = bob.List(SmokeHub.Team).Select(SharedMarks.FromItem).OfType<SharedMark>().FirstOrDefault(s => s.Mark.Code is not null && s.Item.RecipientMemberId == TeamsHex.Of(SmokeHub.Alice));
            var st = bobSees?.StatusOf(TeamsHex.Of(SmokeHub.Alice));
            Check("shared-done-reaches-author", st is { Status: TeamItemStatuses.Done, Note: "Done in a loop, see line 30" }, $"status={st?.Status} note={st?.Note}");
            await Task.Delay(500);
            window.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "SharedMarksCard")?.BringIntoView();
            await Task.Delay(400);
            Shot("65-shared-recipient-done");

            // «В запрос агенту»: Bob's page mark joins my message box with its anchor, for my agent's next request
            var pageRow = vm.TeamMarks.Received.First(r => !r.IsCode);
            pageRow.ToAgentCommand.Execute(null);
            var chip = await Until(() => vm.Marks.Items.Any(m => m.Note.EndsWith("(left by Bob)", StringComparison.Ordinal) && m.Page?.Element.Selector == "#buy"), 5);
            Check("shared-to-agent", chip && !vm.IsTeamPageOpen, $"marks=[{string.Join(" | ", vm.Marks.Items.Select(m => m.Note))}]");
            Check("shared-to-agent-request", LocalMarks.BuildRequest("", vm.Marks.Models()).Contains("Selector: #buy", StringComparison.Ordinal));
            vm.ActivePane = SidePane.None;
            await Task.Delay(600);
            Shot("66-shared-to-agent");
            vm.Marks.Items.Clear();
            vm.IsTeamPageOpen = false;
        }

        private string PageMessage(string url, string id, string comment) => System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "annotation", ["token"] = vm.Preview.AnnotationToken, ["id"] = id, ["url"] = url, ["title"] = "Shop", ["comment"] = comment,
            ["mark"] = new Dictionary<string, object> { ["kind"] = "point", ["x"] = 400, ["y"] = 300, ["pageX"] = 400, ["pageY"] = 1500, ["offsetX"] = 30, ["offsetY"] = 12, ["viewportWidth"] = 1280, ["viewportHeight"] = 800 },
            ["element"] = new Dictionary<string, object> { ["selector"] = "#buy", ["tag"] = "button", ["html"] = "<button id=\"buy\">", ["text"] = "Buy", ["source"] = "", ["x"] = 380, ["y"] = 290, ["width"] = 80, ["height"] = 30 },
        });
    }

    /// <summary>A local page with the marked button far down (the pin must be scrolled to).</summary>
    private sealed class SmokeSite : IDisposable
    {
        private readonly HttpListener _http = new();
        public string Url { get; private set; } = "";

        public static SmokeSite Start()
        {
            var s = new SmokeSite();
            for (var port = 18500; ; port++)
            {
                try
                {
                    s._http.Prefixes.Clear();
                    s._http.Prefixes.Add($"http://127.0.0.1:{port}/");
                    s._http.Start();
                    s.Url = $"http://127.0.0.1:{port}/shop";
                    break;
                }
                catch (HttpListenerException) when (port < 18600) { }
            }
            _ = Task.Run(async () =>
            {
                while (s._http.IsListening)
                {
                    try
                    {
                        var c = await s._http.GetContextAsync();
                        var html = Encoding.UTF8.GetBytes("<!doctype html><html><head><title>Shop</title></head><body style=\"font:16px system-ui;margin:24px\">" +
                            "<h1>Shop</h1><div style=\"height:1400px;background:linear-gradient(#f4f4f4,#ddd)\">Products…</div>" +
                            "<button id=\"buy\" style=\"padding:8px 24px\">Buy</button><footer style=\"height:600px\">Footer</footer></body></html>");
                        c.Response.ContentType = "text/html; charset=utf-8";
                        await c.Response.OutputStream.WriteAsync(html);
                        c.Response.Close();
                    }
                    catch (Exception) when (!s._http.IsListening) { }
                }
            });
            return s;
        }

        public void Dispose() => _http.Close();
    }

    /// <summary>One replica for Alice, Bob and Carol (delivery is instant).</summary>
    private sealed class SmokeHub
    {
        public const string Team = "5a0e";
        public static readonly Guid Alice = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
        public static readonly Guid Bob = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");
        public static readonly Guid Carol = Guid.Parse("cccccccc-0000-4000-8000-000000000003");
        public List<TeamItem> Items { get; } = [];
        public event Action<string>? Changed;
        public void Raise() => Changed?.Invoke(Team);
    }

    private sealed class SmokeTeam(SmokeHub hub, Guid me) : ITeamContext
    {
        public string? SelectedTeamId => SmokeHub.Team;
        public Guid? MyMemberId => me;
        public IReadOnlyList<TeamMember> Members { get; } =
        [
            new(SmokeHub.Alice, "owner", "Alice", [], me == SmokeHub.Alice, ""),
            new(SmokeHub.Bob, "member", "Bob", [], me == SmokeHub.Bob, ""),
            new(SmokeHub.Carol, "member", "Carol", [], me == SmokeHub.Carol, ""),
        ];
        public ITeamItems? Items { get; } = new SmokeItems(hub, TeamsHex.Of(me));
        public event Action? Changed { add { } remove { } }
    }

    private sealed class SmokeItems : ITeamItems
    {
        private readonly SmokeHub _hub;
        private int _next;
        public string Me { get; }

        public SmokeItems(SmokeHub hub, string me)
        {
            _hub = hub;
            Me = me;
            hub.Changed += t => Avalonia.Threading.Dispatcher.UIThread.Post(() => ItemsChanged?.Invoke(t));
        }

        public event Action<string>? ItemsChanged;

        public Task<IReadOnlyList<TeamItemCopy>> SendAsync(string teamId, TeamItemDraft d, IReadOnlyList<string> to, CancellationToken ct = default)
        {
            var copies = new List<TeamItemCopy>();
            foreach (var r in to)
            {
                var id = $"{Me[..4]}{++_next:x4}";
                _hub.Items.Insert(0, new TeamItem(teamId, Me, id, d.Kind, 1, (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(), null, r, false, d.Anchor?.DeepClone(), d.Context?.DeepClone(), d.Body?.DeepClone(), d.Attachments?.DeepClone(), [], null));
                copies.Add(new TeamItemCopy(r, "g", id, "op"));
            }
            _hub.Raise();
            return Task.FromResult<IReadOnlyList<TeamItemCopy>>(copies);
        }

        public Task SetStatusAsync(string teamId, string authorMemberId, string itemId, string status, string? note = null, CancellationToken ct = default)
        {
            var i = _hub.Items.FindIndex(x => x.AuthorMemberId == authorMemberId && x.ItemId == itemId && x.RecipientMemberId == Me);
            if (i < 0) throw new InvalidOperationException("not addressed to me");
            var old = _hub.Items[i].Statuses.FirstOrDefault(s => s.RecipientMemberId == Me);
            var next = status == TeamItemStatuses.Seen ? new TeamItemStatus(Me, true, old?.Status, old?.Note) : new TeamItemStatus(Me, true, status, note);
            _hub.Items[i] = _hub.Items[i] with { Statuses = [.. _hub.Items[i].Statuses.Where(s => s.RecipientMemberId != Me), next] };
            _hub.Raise();
            return Task.CompletedTask;
        }

        public IReadOnlyList<TeamItem> List(string teamId, int limit = 200, ulong? before = null) =>
            [.. _hub.Items.Where(x => x.AuthorMemberId == Me || x.RecipientMemberId == Me).Take(limit)];
        public TeamItem? Get(string teamId, string authorMemberId, string itemId) => _hub.Items.FirstOrDefault(x => x.AuthorMemberId == authorMemberId && x.ItemId == itemId);
        public Task EditAsync(string teamId, string itemId, ulong baseRev, TeamItemDraft delta, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string teamId, string authorMemberId, string itemId, CancellationToken ct = default) => throw new NotSupportedException();
        public int PendingCount(string? teamId = null) => 0;
        public Task<TeamDeliveryResult> DeliverAsync(CancellationToken ct = default) => Task.FromResult(new TeamDeliveryResult(0, 0, 0, []));

        private static readonly Dictionary<string, byte[]> Blobs = [];

        public async Task<TeamBlob> UploadAsync(string teamId, string recipientMemberId, Stream content, CancellationToken ct = default)
        {
            using var m = new MemoryStream();
            await content.CopyToAsync(m, ct);
            var id = Guid.NewGuid().ToString("D");
            lock (Blobs) Blobs[id] = m.ToArray();
            return new TeamBlob(id, (ulong)m.Length, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(m.ToArray())), "00");
        }

        public Task<byte[]> DownloadAsync(TeamItem item, CancellationToken ct = default)
        {
            lock (Blobs) return Task.FromResult(Blobs[item.Body!["blobRef"]!.GetValue<string>()]);
        }
    }
}
#endif
