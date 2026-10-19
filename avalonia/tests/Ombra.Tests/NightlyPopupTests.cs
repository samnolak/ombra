using System.Buffers.Binary;
using System.Text;
using Ombra.App.Nightly;

namespace Ombra.Tests;

/// <summary>
/// A Nightly page's popups, new windows, composition and pointer shape (desktop <c>zen-embed.js</c> and
/// <c>ZenEmbedHost.mm</c>): the PopupChoice / MarkedText / Popup / PopupClosed / Cursor / TextRect layouts against the
/// native structs, the popup JSON, and, with a real embed-capable Nightly (<c>OMBRA_TEST_NIGHTLY_APP</c>), a page's
/// <c>&lt;select&gt;</c>, datalist autocomplete and context menu answered from Ombra, "Open link in new tab" and
/// <c>window.open</c> adopted as panes, a composition and its commit, the input method's end of a composition Nightly
/// ended itself, and the page's cursor. In the runtime tests' class, so the real runtime tests never run side by side.
/// </summary>
public sealed partial class NightlyRuntimeTests
{
    [Fact]
    public void PopupAndInputMethodMessagesHaveTheLayoutOfTheNativeStructs()
    {
        var choice = NightlyProtocol.PopupChoiceMessage(7, 5, -1);
        Assert.Equal(40, choice.Length);
        Assert.Equal(NightlyProtocol.PopupChoice, BinaryPrimitives.ReadInt32LittleEndian(choice.AsSpan(20)));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(choice.AsSpan(24)));
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(choice.AsSpan(28)));
        Assert.Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(choice.AsSpan(32)));

        var marked = NightlyProtocol.MarkedTextMessage(3, "にほん", 3, 9);
        Assert.Equal(168, marked.Length);
        Assert.Equal(NightlyProtocol.MarkedText, BinaryPrimitives.ReadInt32LittleEndian(marked.AsSpan(20)));
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(marked.AsSpan(28))); // caret at the end
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(marked.AsSpan(32))); // clamped to the text
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(marked.AsSpan(36)));
        Assert.Equal('に', (char)BinaryPrimitives.ReadUInt16LittleEndian(marked.AsSpan(40)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(NightlyProtocol.MarkedTextMessage(3, "", 0, 0).AsSpan(36)));
    }

    [Fact]
    public void ReadsPopupCursorAndTextRectMessages()
    {
        var json = """{"kind":"select","rect":{"x":300,"y":20,"width":160,"height":32},"items":[{"kind":"item","label":"Alpha"}],"selected":0}""";
        var bytes = Encoding.UTF8.GetBytes(json);
        var popup = new byte[64];
        BinaryPrimitives.WriteInt32LittleEndian(popup.AsSpan(20), NightlyProtocol.Popup);
        BinaryPrimitives.WriteUInt32LittleEndian(popup.AsSpan(44), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(popup.AsSpan(48), 9);
        BinaryPrimitives.WriteUInt32LittleEndian(popup.AsSpan(56), (uint)bytes.Length);
        Assert.Equal(new NightlyProtocol.PopupInfo(2, 9, json), NightlyProtocol.ReadPopup(popup, bytes));
        Assert.Null(NightlyProtocol.ReadPopup(popup, bytes[..10])); // a length past the payload is refused

        var closed = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(closed.AsSpan(24), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(closed.AsSpan(28), 9);
        Assert.Equal((2u, 9u), NightlyProtocol.ReadPopupClosed(closed));
        Assert.Equal((2u, 9), NightlyProtocol.ReadCursor(closed));

        var rect = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(rect.AsSpan(24), 2);
        BinaryPrimitives.WriteDoubleLittleEndian(rect.AsSpan(32), 10.5);
        BinaryPrimitives.WriteDoubleLittleEndian(rect.AsSpan(40), 20);
        BinaryPrimitives.WriteDoubleLittleEndian(rect.AsSpan(48), 1);
        BinaryPrimitives.WriteDoubleLittleEndian(rect.AsSpan(56), 18);
        Assert.Equal(new NightlyProtocol.TextRectInfo(2, 10.5, 20, 1, 18), NightlyProtocol.ReadTextRect(rect));
    }

    [Fact]
    public void ParsesThePopupJson()
    {
        var select = NightlyPopup.Parse("""
            {"kind":"select","rect":{"x":300,"y":20,"width":160,"height":32},"selected":2,"items":[
              {"kind":"header","label":"Group"},{"kind":"item","label":"Alpha","indent":true},
              {"kind":"item","label":"Beta","disabled":true},{"kind":"separator"}]}
            """)!;
        Assert.Equal(NightlyPopup.PopupKind.Select, select.Kind);
        Assert.Equal((300, 20, 160, 32), select.Anchor);
        Assert.Equal(2, select.Selected);
        Assert.Equal([NightlyPopup.ItemKind.Header, NightlyPopup.ItemKind.Item, NightlyPopup.ItemKind.Item, NightlyPopup.ItemKind.Separator],
            select.Items.Select(i => i.Kind));
        Assert.True(select.Items[1].Indent);
        Assert.True(select.Items[1].CanChoose);
        Assert.False(select.Items[2].CanChoose);
        Assert.False(select.Items[0].CanChoose);

        var menu = NightlyPopup.Parse("""{"kind":"menu","rect":{"x":5,"y":6,"width":0,"height":0},"selected":-1,"items":[{"kind":"item","command":"copy-link","label":""}]}""")!;
        Assert.Equal("copy-link", menu.Items[0].Command);
        Assert.Equal(-1, menu.Selected);

        Assert.Null(NightlyPopup.Parse("""{"kind":"tooltip","items":[{"label":"x"}]}"""));
        Assert.Null(NightlyPopup.Parse("""{"kind":"menu","items":[]}"""));
        Assert.Null(NightlyPopup.Parse("not json"));
        Assert.Equal(-1, NightlyPopup.Parse("""{"kind":"menu","selected":7,"items":[{"label":"x"}]}""")!.Selected);
    }

    /// <summary>
    /// A select, a datalist field, a field to compose in, a link, a button that opens a pop-up, and two pointer
    /// targets; the title says the state (CSS px from the page's top left, 800×600 window).
    /// </summary>
    private const string PopupFixture = """
        <!doctype html><html><head><meta charset="utf-8"><title>K1</title>
        <style>
          body { margin: 0; font: 15px sans-serif; }
          #link { position: absolute; left: 20px; top: 70px; width: 200px; height: 30px; line-height: 30px; display: block; background: #eef; }
          #sel { position: absolute; left: 300px; top: 20px; width: 160px; height: 32px; }
          #open { position: absolute; left: 300px; top: 70px; width: 160px; height: 30px; }
          #ime { position: absolute; left: 300px; top: 120px; width: 240px; height: 32px; box-sizing: border-box; }
          #pointer { position: absolute; left: 300px; top: 170px; width: 100px; height: 40px; background: #fd8; cursor: pointer; }
          #grab { position: absolute; left: 420px; top: 170px; width: 100px; height: 40px; background: #8df; cursor: grab; }
          #auto { position: absolute; left: 20px; top: 240px; width: 240px; height: 32px; box-sizing: border-box; }
        </style></head><body>
        <a id="link" href="#linked">Link to #linked</a>
        <select id="sel" aria-label="Choice"><option value="alpha">Alpha</option><option value="beta">Beta</option><option value="gamma">Gamma</option></select>
        <button id="open">Open a pop-up</button>
        <input id="ime" aria-label="IME">
        <div id="pointer">pointer</div><div id="grab">grab</div>
        <input id="auto" name="city" list="cities" aria-label="City"><datalist id="cities"><option value="Moscow"><option value="Madrid"><option value="Milan"></datalist>
        <script nonce="k1">
          let comp = "", popup = new URLSearchParams(location.search).get("popup") || "0";
          const sel = document.getElementById("sel"), ime = document.getElementById("ime"), auto = document.getElementById("auto");
          document.getElementById("open").addEventListener("click", () => { window.open(location.pathname + "?popup=1", "_blank"); });
          ime.addEventListener("compositionupdate", e => { comp = e.data; update(); });
          ime.addEventListener("compositionend", e => { comp = "end:" + e.data; update(); });
          for (const el of [sel, ime, auto]) { el.addEventListener("input", update); el.addEventListener("change", update); }
          function update() {
            document.title = `K1 s=${sel.value} p=${popup} ime=${ime.value} comp=${comp} a=${auto.value} op=${window.opener ? 1 : 0}`;
          }
          update();
        </script></body></html>
        """;

    [Fact]
    public async Task PagePopupsNewWindowsAndCompositionComeToOmbra()
    {
        var app = Environment.GetEnvironmentVariable("OMBRA_TEST_NIGHTLY_APP");
        var root = Environment.GetEnvironmentVariable("OMBRA_TEST_NIGHTLY_PROFILE_ROOT");
        Assert.SkipUnless(OperatingSystem.IsMacOS() && app is { Length: > 0 } && root is { Length: > 0 },
            "set OMBRA_TEST_NIGHTLY_APP (an embed-capable Nightly.app) and OMBRA_TEST_NIGHTLY_PROFILE_ROOT");
        Assert.Empty(NightlyProcesses(app!));
        var profile = Path.Combine(root!, "popup-test-" + Guid.NewGuid().ToString("N")[..8]);
        var serial = new Serial();
        var options = new NightlyRuntimeOptions(NightlyMach.RealPath(Path.Combine(app!, "Contents", "MacOS", "nightly")), profile, null, null, null, AllowUnsignedPeer: true);
        var runtime = new NightlyRuntime(options, serial.Post);
        var pane = new AgentPane(runtime);
        var panes = new List<AgentPane> { pane };
        var (server, url) = ServeFixture(PopupFixture);
        try
        {
            var token = serial.Run(() => runtime.Register(pane));
            await Until(() => serial.Run(() => runtime.State == NightlyState.Running && pane.Window != 0 && pane.FrameCount > 0), "a window with frames", 90);
            var window = pane.Window;
            serial.Run(() => runtime.Send(NightlyProtocol.CommandMessage(window, NightlyProtocol.CommandOp.Navigate, url)!));
            string Title(AgentPane p) => serial.Run(() => p.Location?.Title ?? "");
            await Until(() => Title(pane).StartsWith("K1 s=alpha", StringComparison.Ordinal), "the fixture", 30);
            // Input reaches the page once it has been painted (hit testing works on what the compositor has).
            var framesAtLoad = serial.Run(() => pane.FrameCount);
            await Until(() => serial.Run(() => pane.FrameCount > framesAtLoad), "the page painted", 10);
            await Task.Delay(1000);
            serial.Run(() => runtime.SetActive(window, true));
            void Send(byte[] m) => serial.Run(() => runtime.Send(m));
            void Mouse(double x, double y, int button = 0)
            {
                Send(NightlyProtocol.MouseMessage(window, NightlyProtocol.MouseKind.Move, -1, 0, x, y, 0));
                Send(NightlyProtocol.MouseMessage(window, NightlyProtocol.MouseKind.Down, button, 1, x, y, 0));
                Send(NightlyProtocol.MouseMessage(window, NightlyProtocol.MouseKind.Up, button, 1, x, y, 0));
            }
            (uint Id, NightlyPopup Popup)? Popup(NightlyPopup.PopupKind kind) => serial.Run(() =>
            {
                for (var i = pane.Popups.Count - 1; i >= 0; i--)
                    if (pane.Popups[i].Popup.Kind == kind) return pane.Popups[i];
                return ((uint, NightlyPopup)?)null;
            });

            // <select>: its options under it; the choice changes the page's value (input, change).
            Mouse(380, 36);
            await Until(() => Popup(NightlyPopup.PopupKind.Select) is not null, "the select's popup", 10);
            var (selectId, select) = Popup(NightlyPopup.PopupKind.Select)!.Value;
            Assert.Equal(["Alpha", "Beta", "Gamma"], select.Items.Select(i => i.Label));
            Assert.Equal(0, select.Selected);
            Assert.InRange(select.Anchor.X, 298, 302);
            Assert.InRange(select.Anchor.Y, 18, 22);
            Assert.InRange(select.Anchor.Width, 158, 162);
            Send(NightlyProtocol.PopupChoiceMessage(window, selectId, 2));
            await Until(() => Title(pane).Contains("s=gamma ", StringComparison.Ordinal), "the chosen option", 10);
            // Opened again it shows the new choice; the page closing it (its select loses the focus) tells Ombra.
            Mouse(380, 36);
            await Until(() => Popup(NightlyPopup.PopupKind.Select) is { } p && p.Id != selectId, "the select's popup again", 10);
            var (againId, again) = Popup(NightlyPopup.PopupKind.Select)!.Value;
            Assert.Equal(2, again.Selected);
            await runtime.RunScriptAsync(window, NightlyProtocol.ScriptKind.Page, "document.getElementById('sel').blur(), 0", "", CancellationToken.None);
            await Until(() => serial.Run(() => pane.ClosedPopups.Contains(againId)), "the page closes the dropdown (blur)", 10);

            // Datalist autocomplete: typing shows the entries; the chosen one fills the field.
            Mouse(140, 256);
            await Task.Delay(300);
            Send(NightlyProtocol.KeyMessage(window, true, 0x2E, "M", "m", 1UL << 17, false));
            Send(NightlyProtocol.KeyMessage(window, false, 0x2E, "M", "m", 1UL << 17, false));
            await Until(() => Popup(NightlyPopup.PopupKind.Autocomplete) is { Popup.Items.Count: >= 3 }, "the datalist's entries", 10);
            var (autoId, auto) = Popup(NightlyPopup.PopupKind.Autocomplete)!.Value;
            var madrid = auto.Items.ToList().FindIndex(i => i.Label == "Madrid");
            Assert.True(madrid >= 0, string.Join(",", auto.Items.Select(i => i.Label)));
            Send(NightlyProtocol.PopupChoiceMessage(window, autoId, madrid));
            await Until(() => Title(pane).Contains("a=Madrid ", StringComparison.Ordinal), "the chosen entry", 10);

            // A composition (an input method's marked text), where it is for the candidate window, and its commit; then
            // a dead key's: the accent composed, the letter committed over it.
            Mouse(420, 136);
            await Task.Delay(300);
            Send(NightlyProtocol.MarkedTextMessage(window, "にほん", 3, 0));
            await Until(() => Title(pane).Contains("ime=にほん comp=にほん ", StringComparison.Ordinal), "the composition in the page", 10);
            await Until(() => serial.Run(() => pane.TextRect is { } r && r.X >= 300 && r.X <= 540 && r.Y >= 118 && r.Y + r.Height <= 154 && r.Height > 0),
                $"the composition's place inside the field ({serial.Run(() => pane.TextRect)})", 10);
            Send(NightlyProtocol.TextMessage(window, "日本"));
            await Until(() => Title(pane).Contains("ime=日本 comp=end:日本 ", StringComparison.Ordinal), "the commit", 10);
            Send(NightlyProtocol.MarkedTextMessage(window, "´", 1, 0));
            await Until(() => Title(pane).Contains("comp=´ ", StringComparison.Ordinal), "the dead key's accent", 10);
            Send(NightlyProtocol.TextMessage(window, "é"));
            await Until(() => Title(pane).Contains("ime=日本é comp=end:é ", StringComparison.Ordinal), "the accented letter", 10);

            // The pointer shape the page wants comes to Ombra (Nightly leaves the screen's cursor alone).
            Send(NightlyProtocol.MouseMessage(window, NightlyProtocol.MouseKind.Move, -1, 0, 350, 190, 0));
            await Until(() => serial.Run(() => pane.Cursor == 3), "the hand over cursor: pointer (eCursor_hyperlink)", 10);
            Send(NightlyProtocol.MouseMessage(window, NightlyProtocol.MouseKind.Move, -1, 0, 470, 190, 0));
            await Until(() => serial.Run(() => pane.Cursor == 19), "cursor: grab (eCursor_grab)", 10);

            // The context menu on a link: Ombra labels its commands; "Open link in new tab" opens a window for a pane of its own.
            Mouse(80, 85, button: 1);
            await Until(() => Popup(NightlyPopup.PopupKind.Menu) is not null, "the link's context menu", 10);
            var (menuId, menu) = Popup(NightlyPopup.PopupKind.Menu)!.Value;
            Assert.InRange(menu.Anchor.X, 78, 82); // at the pointer
            Assert.InRange(menu.Anchor.Y, 83, 87);
            var commands = menu.Items.Select(i => i.Command).ToList();
            Assert.Contains("copy-link", commands);
            Send(NightlyProtocol.PopupChoiceMessage(window, menuId, commands.IndexOf("open-link")));
            await Until(() => serial.Run(() => pane.PopupWindows.Count == 1), "the link's new window", 15);
            var linked = new AgentPane(runtime);
            panes.Add(linked);
            var linkedToken = serial.Run(() => runtime.Adopt(linked, pane.PopupWindows[0]));
            await Until(() => serial.Run(() => linked.Window == pane.PopupWindows[0] && linked.FrameCount > 0
                && linked.Location is { Url: var u } && u.EndsWith("#linked", StringComparison.Ordinal)), "the link in the adopted pane", 30);
            // Its tab closed: the window goes, and what Nightly still sends for it does not offer it again.
            serial.Run(() => runtime.Unregister(linkedToken));
            await Task.Delay(1500);
            Assert.Single(serial.Run(() => pane.PopupWindows.ToList()));

            // window.open from a click: the pop-up's window comes the same way, its page has an opener.
            Mouse(380, 85);
            await Until(() => serial.Run(() => pane.PopupWindows.Count == 2), "window.open's window", 15);
            var opened = new AgentPane(runtime);
            panes.Add(opened);
            var openedToken = serial.Run(() => runtime.Adopt(opened, pane.PopupWindows[1]));
            await Until(() => serial.Run(() => opened.FrameCount > 0) && Title(opened).Contains("p=1 ", StringComparison.Ordinal)
                && Title(opened).EndsWith("op=1", StringComparison.Ordinal), $"the pop-up's page ('{Title(opened)}')", 30);

            // The pop-up closes itself: its pane hears it.
            var closing = await runtime.RunScriptAsync(opened.Window, NightlyProtocol.ScriptKind.Page, "window.close()", "", CancellationToken.None);
            Assert.NotEqual(NightlyProtocol.ScriptStatus.Error, closing.Status);
            await Until(() => serial.Run(() => opened.WindowClosed && opened.Window == 0), "the pop-up's window closed", 10);

            serial.Run(() => runtime.Unregister(openedToken));
            serial.Run(() => runtime.Unregister(token));
            await Until(() => NightlyProcesses(app!).Count == 0, "no Nightly process left", 15);
        }
        finally
        {
            server.Stop();
            serial.Run(runtime.Dispose);
            foreach (var p in panes) p.Frames.Dispose();
            try { Directory.Delete(profile, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Compositions Nightly ends itself while the input method still composes in Ombra (a click elsewhere in the page takes
    /// the focus from the field: Nightly commits it), and one where no editor has the keyboard: the input method's empty end
    /// afterwards, and an empty one with no composition at all, are nothing for Nightly (they crashed it in
    /// <c>IMEInputHandler::DispatchCompositionCommitEvent</c>, MOZ_RELEASE_ASSERT(isSome())); the page keeps its text and
    /// the field composes again, in the same runtime.
    /// </summary>
    [Fact]
    public async Task AnEmptyCompositionWithoutOneInNightlyIsNothing()
    {
        var app = Environment.GetEnvironmentVariable("OMBRA_TEST_NIGHTLY_APP");
        var root = Environment.GetEnvironmentVariable("OMBRA_TEST_NIGHTLY_PROFILE_ROOT");
        Assert.SkipUnless(OperatingSystem.IsMacOS() && app is { Length: > 0 } && root is { Length: > 0 },
            "set OMBRA_TEST_NIGHTLY_APP (an embed-capable Nightly.app) and OMBRA_TEST_NIGHTLY_PROFILE_ROOT");
        Assert.Empty(NightlyProcesses(app!));
        var profile = Path.Combine(root!, "ime-test-" + Guid.NewGuid().ToString("N")[..8]);
        var serial = new Serial();
        var options = new NightlyRuntimeOptions(NightlyMach.RealPath(Path.Combine(app!, "Contents", "MacOS", "nightly")), profile, null, null, null, AllowUnsignedPeer: true);
        var runtime = new NightlyRuntime(options, serial.Post);
        var pane = new AgentPane(runtime);
        var (server, url) = ServeFixture(PopupFixture);
        try
        {
            var token = serial.Run(() => runtime.Register(pane));
            await Until(() => serial.Run(() => runtime.State == NightlyState.Running && pane.Window != 0 && pane.FrameCount > 0), "a window with frames", 90);
            var window = pane.Window;
            serial.Run(() => runtime.Send(NightlyProtocol.CommandMessage(window, NightlyProtocol.CommandOp.Navigate, url)!));
            string Title() => serial.Run(() => pane.Location?.Title ?? "");
            await Until(() => Title().StartsWith("K1 s=alpha", StringComparison.Ordinal), "the fixture", 30);
            var framesAtLoad = serial.Run(() => pane.FrameCount);
            await Until(() => serial.Run(() => pane.FrameCount > framesAtLoad), "the page painted", 10);
            await Task.Delay(1000);
            serial.Run(() => runtime.SetActive(window, true));
            var pid = serial.Run(() => runtime.Pid);
            void Send(byte[] m) => serial.Run(() => runtime.Send(m));
            void Click(double x, double y)
            {
                Send(NightlyProtocol.MouseMessage(window, NightlyProtocol.MouseKind.Move, -1, 0, x, y, 0));
                Send(NightlyProtocol.MouseMessage(window, NightlyProtocol.MouseKind.Down, 0, 1, x, y, 0));
                Send(NightlyProtocol.MouseMessage(window, NightlyProtocol.MouseKind.Up, 0, 1, x, y, 0));
            }
            async Task EmptyEnd(string where)
            {
                Send(NightlyProtocol.MarkedTextMessage(window, "", 0, 0));
                await Task.Delay(500);
                Assert.True(serial.Run(() => runtime.Pid == pid && runtime.Starts == 1 && NightlyMach.IsAlive(pid)),
                    $"Nightly crashed on the empty composition {where} (pid {pid}→{serial.Run(() => runtime.Pid)})");
            }

            Click(420, 136);
            await Task.Delay(300);
            await EmptyEnd("with none at all");
            Send(NightlyProtocol.MarkedTextMessage(window, "かな", 2, 0));
            await Until(() => Title().Contains(" ime=かな comp=かな ", StringComparison.Ordinal), "a composition", 10);
            Click(100, 400); // the page's empty part: the field loses the focus
            await Until(() => Title().Contains(" ime=かな comp=end:かな ", StringComparison.Ordinal), "the blur's commit", 10);
            await EmptyEnd("after the blur's commit");
            Send(NightlyProtocol.MarkedTextMessage(window, "の", 1, 0));
            await EmptyEnd("where no editor has the keyboard");
            Assert.Contains(" ime=かな comp=end:かな ", Title(), StringComparison.Ordinal);
            Click(520, 136);
            await Task.Delay(300);
            Send(NightlyProtocol.MarkedTextMessage(window, "じ", 1, 0));
            Send(NightlyProtocol.TextMessage(window, "字"));
            await Until(() => Title().Contains(" ime=かな字 comp=end:字 ", StringComparison.Ordinal), "a composition after them", 10);
            Assert.Equal(pid, serial.Run(() => runtime.Pid));

            serial.Run(() => runtime.Unregister(token));
            await Until(() => NightlyProcesses(app!).Count == 0, "no Nightly process left", 15);
        }
        finally
        {
            server.Stop();
            serial.Run(runtime.Dispose);
            pane.Frames.Dispose();
            try { Directory.Delete(profile, recursive: true); } catch (IOException) { }
        }
    }
}
