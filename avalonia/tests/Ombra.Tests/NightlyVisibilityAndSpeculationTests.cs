using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using Ombra.App.Nightly;
using Ombra.ClientCore.Browser;

namespace Ombra.Tests;

/// <summary>
/// Embed protocol 5 on Ombra's side: Visibility (46) laid out as ZenEmbedHost.mm reads it and sent only when a pane's
/// state changes, each with a generation above every earlier one; a pane off screen holds none of Nightly's frames; and
/// Augur's Speculate command (Command op 9), whose JSON keeps to the bounds Nightly enforces.
/// </summary>
public sealed class NightlyVisibilityAndSpeculationTests
{
    private sealed class Wire
    {
        public readonly List<(uint Window, bool Visible, uint Generation)> Sent = [];
        public readonly List<(uint Window, ulong Seq)> Released = [];
        // Everything in the order it happened: "hide", "visible"/"hidden" (sent), "release N".
        public readonly List<string> Log = [];
        // Retries the pane scheduled (run by the test).
        public readonly List<(TimeSpan Delay, Action Run)> Later = [];
        public bool Fails;

        public bool Send(byte[] m)
        {
            Assert.Equal(NightlyProtocol.Visibility, NightlyProtocol.Id(m));
            if (Fails) return false;
            Sent.Add((R32(m, 24), R32(m, 28) != 0, R32(m, 32)));
            Log.Add(R32(m, 28) != 0 ? "visible" : "hidden");
            return true;
        }

        public void Release(uint window, ulong seq)
        {
            Released.Add((window, seq));
            Log.Add($"release {seq}");
        }

        public void Hide() => Log.Add("hide");

        public void RunLater()
        {
            var due = Later.ToList();
            Later.Clear();
            foreach (var (_, run) in due) run();
        }
    }

    private static uint R32(byte[] m, int o) => BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(o));

    private static NightlyProtocol.FrameInfo Frame(uint window, ulong seq) => new(window, 1, seq, 0, 800, 600);

    private static (NightlyVisibility Visibility, Wire Wire) Pane()
    {
        var wire = new Wire();
        return (new NightlyVisibility(wire.Send, wire.Release, wire.Hide, (delay, run) => wire.Later.Add((delay, run))), wire);
    }

    // ── layouts ──

    [Fact]
    public void VisibilityHasTheLayoutOfTheNativeStruct()
    {
        Assert.Equal(5u, NightlyProtocol.Version);
        var m = NightlyProtocol.VisibilityMessage(7, true, 0xDEADBEEF);
        Assert.Equal(40, m.Length); // 36, padded to 40
        Assert.Equal(19u, R32(m, 0)); // MACH_MSG_TYPE_COPY_SEND, not complex
        Assert.Equal(40u, R32(m, 4));
        Assert.Equal(46, BinaryPrimitives.ReadInt32LittleEndian(m.AsSpan(20)));
        Assert.Equal(7u, R32(m, 24));
        Assert.Equal(1u, R32(m, 28));
        Assert.Equal(0xDEADBEEFu, R32(m, 32));
        Assert.Equal(0u, R32(NightlyProtocol.VisibilityMessage(7, false, 1), 28));
    }

    [Fact]
    public void SpeculateIsCommandOpNineWithItsJson()
    {
        var json = NightlyProtocol.CancelSpeculationJson("0123456789abcdef")!;
        var m = NightlyProtocol.SpeculateMessage(3, json)!;
        Assert.Equal(NightlyProtocol.Command, NightlyProtocol.Id(m));
        Assert.Equal(0, m.Length % 4);
        Assert.Equal(3u, R32(m, 24));
        Assert.Equal(9u, R32(m, 28));
        Assert.Equal((uint)Encoding.UTF8.GetByteCount(json), R32(m, 32));
        Assert.Equal(json, Encoding.UTF8.GetString(m, 40, (int)R32(m, 32)));
        // A pane whose window is still being opened (agent_open): window 0.
        Assert.Equal(0u, R32(NightlyProtocol.SpeculateMessage(0, json)!, 24));
        Assert.Null(NightlyProtocol.SpeculateMessage(3, new string('a', NightlyProtocol.MaxText + 1)));
    }

    // ── visibility ──

    [Fact]
    public void OnlyChangesAreSentEachWithAHigherGeneration()
    {
        var (v, wire) = Pane();
        v.Attach(5, visible: true); // the window starts with the pane's state
        v.Update(true);
        v.Update(true);
        v.Update(false);
        v.Update(false);
        v.Update(false);
        v.Update(true);
        Assert.Equal([(5u, true, 1u), (5u, false, 2u), (5u, true, 3u)], wire.Sent);
    }

    [Fact]
    public void AGenerationIsNeverSentTwiceNorBelowAnEarlierOne()
    {
        var (v, wire) = Pane();
        v.Attach(5, visible: true);
        wire.Fails = true; // the queue is full: the hidden state does not get there
        v.Update(false);
        wire.Fails = false;
        v.Update(false); // sent now, under a generation of its own
        v.Detach(); // the runtime restarted: nothing more for window 5
        v.Update(true);
        v.Update(false);
        v.Attach(9, visible: false);
        v.Update(true);
        Assert.Equal([(5u, true, 1u), (5u, false, 3u), (9u, false, 4u), (9u, true, 5u)], wire.Sent);
        Assert.Equal(5u, v.Generation);
        Assert.DoesNotContain(wire.Sent, s => s.Window == 5 && s.Generation > 3);
    }

    [Fact]
    public void HidingGivesBackEveryFrameAndInFlightOnesAtOnce()
    {
        var (v, wire) = Pane();
        v.Attach(5, visible: true);
        Assert.True(v.Arrived(Frame(5, 1), canShow: true));
        v.Showing(Frame(5, 1));
        Assert.False(v.Arrived(Frame(5, 2), canShow: false)); // no layer yet: held
        Assert.Equal(Frame(5, 2), v.Held);
        Assert.Empty(wire.Released);

        Assert.True(v.Update(false));
        Assert.Equal([(5u, 1ul), (5u, 2ul)], wire.Released);
        Assert.Null(v.Shown);
        Assert.Null(v.Held);
        Assert.Equal((5u, false, 2u), wire.Sent[^1]);

        // Still in flight when Nightly got the hidden state: back as it comes, not held, no second message.
        Assert.False(v.Arrived(Frame(5, 3), canShow: true));
        Assert.Equal((5u, 3ul), wire.Released[^1]);
        Assert.Null(v.Held);
        Assert.Null(v.TakeHeld());
        Assert.Equal(2, wire.Sent.Count);

        // Shown again: Nightly is told, and the pane waits for the fresh frame it paints.
        Assert.True(v.Update(true));
        Assert.Equal((5u, true, 3u), wire.Sent[^1]);
        Assert.Null(v.Shown);
        Assert.True(v.Arrived(Frame(5, 4), canShow: true));
        v.Showing(Frame(5, 4));
        Assert.Equal(Frame(5, 4), v.Shown);
        Assert.Equal(3, wire.Released.Count);
    }

    [Fact]
    public void TheFrameLeavesTheScreenBeforeNightlyIsToldOrGetsItBack()
    {
        var (v, wire) = Pane();
        v.Attach(5, visible: true);
        Assert.True(v.Arrived(Frame(5, 1), canShow: true));
        v.Showing(Frame(5, 1));
        Assert.False(v.Arrived(Frame(5, 2), canShow: false));
        wire.Log.Clear();
        v.Update(false);
        Assert.Equal(["hide", "hidden", "release 1", "release 2"], wire.Log);
        v.Update(false); // no change: nothing taken off again
        Assert.Equal(4, wire.Log.Count);
    }

    [Fact]
    public void AVisibleStateThatCouldNotBeSentIsSentAgainLater()
    {
        var (v, wire) = Pane();
        v.Attach(5, visible: false);
        wire.Fails = true;
        v.Update(true); // the queue is full: Nightly still has the page hidden
        var retry = Assert.Single(wire.Later);
        Assert.Equal(NightlyVisibility.RetryDelay, retry.Delay);
        v.Update(true); // tried again at once (and failed): still one retry, none piled up
        Assert.Single(wire.Later);
        wire.RunLater(); // still full: once more
        Assert.Single(wire.Later);
        wire.Fails = false;
        wire.RunLater();
        Assert.Equal([(5u, false, 1u), (5u, true, 5u)], wire.Sent);
        Assert.Empty(wire.Later); // Nightly has it: no more retries
    }

    [Fact]
    public void ARetrySendsTheLatestStateAndNothingOnceTheWindowIsGone()
    {
        var (v, wire) = Pane();
        v.Attach(5, visible: false);
        wire.Fails = true;
        v.Update(true);
        wire.Fails = false;
        v.Update(false); // hidden again before the retry: sent at once (the visible one never arrived)
        wire.RunLater(); // Nightly has the latest state: nothing to send
        Assert.Equal([(5u, false, 1u), (5u, false, 3u)], wire.Sent);

        wire.Fails = true;
        v.Update(true);
        v.Detach(); // the runtime restarted before the retry
        wire.Fails = false;
        wire.RunLater();
        Assert.Equal([(5u, false, 1u), (5u, false, 3u)], wire.Sent);
        Assert.Empty(wire.Later);
    }

    [Fact]
    public void LeavingTheWindowAlwaysSendsHiddenAndGivesTheFramesBack()
    {
        // Attached while the queue was full: Nightly never got the visible state, and still gets the hidden one.
        var (v, wire) = Pane();
        wire.Fails = true;
        v.Attach(5, visible: true);
        wire.Fails = false;
        Assert.True(v.Arrived(Frame(5, 1), canShow: true));
        v.Showing(Frame(5, 1));
        Assert.True(v.Arrived(Frame(5, 2), canShow: true));
        v.Showing(Frame(5, 2)); // the one it replaces goes back
        Assert.Equal([(5u, 1ul)], wire.Released);

        v.Update(false); // out of the visual tree
        Assert.Equal([(5u, false, 2u)], wire.Sent);
        Assert.Equal([(5u, 1ul), (5u, 2ul)], wire.Released);
        Assert.Null(v.Shown);
    }

    [Fact]
    public void AGoneWindowsFramesAreVoidAndOtherWindowsFramesGoBack()
    {
        var (v, wire) = Pane();
        v.Attach(5, visible: true);
        Assert.False(v.Arrived(Frame(8, 1), canShow: true)); // not this pane's window
        Assert.Equal([(8u, 1ul)], wire.Released);
        Assert.True(v.Arrived(Frame(5, 2), canShow: true));
        v.Showing(Frame(5, 2));
        v.Detach(); // Nightly restarted: nothing of the old process goes back
        Assert.Null(v.Shown);
        Assert.Equal(0u, v.WindowId);
        v.Update(false);
        Assert.Single(wire.Released);
        Assert.Single(wire.Sent);
    }

    // ── Speculate (Augur v1) ──

    private static JsonObject Parse(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Fact]
    public void AHintRoundTripsItsFields()
    {
        var hint = SpeculationHint.For(SpeculationHint.AgentClick, new Uri("https://example.com/a b?q=1&r=ü#top"), leadMs: 140)!;
        Assert.True(SpeculationHint.IsId(hint.Id));
        var json = Parse(NightlyProtocol.SpeculateJson(hint)!);
        Assert.Equal(["v", "id", "source", "action", "url", "p", "leadMs", "ttlMs"], json.Select(p => p.Key));
        Assert.Equal(1, json["v"]!.GetValue<int>());
        Assert.Equal(hint.Id, json["id"]!.GetValue<string>());
        Assert.Equal("agent_click", json["source"]!.GetValue<string>());
        Assert.Equal("hint", json["action"]!.GetValue<string>());
        Assert.Equal(hint.Url.AbsoluteUri, json["url"]!.GetValue<string>());
        Assert.Equal(new Uri(json["url"]!.GetValue<string>()), hint.Url);
        Assert.Equal(0.90, json["p"]!.GetValue<double>());
        Assert.Equal(140, json["leadMs"]!.GetValue<int>());
        Assert.Equal(SpeculationHint.DefaultTtlMs, json["ttlMs"]!.GetValue<int>());

        // The cancel names the same intent, and nothing else.
        var cancel = Parse(NightlyProtocol.CancelSpeculationJson(hint.Id)!);
        Assert.Equal(["v", "id", "action"], cancel.Select(p => p.Key));
        Assert.Equal(hint.Id, cancel["id"]!.GetValue<string>());
        Assert.Equal("cancel", cancel["action"]!.GetValue<string>());
    }

    [Fact]
    public void EachSourceHasItsPriorAndLead()
    {
        var url = new Uri("http://localhost:3000/");
        foreach (var (source, p, lead) in new[]
                 {
                     (SpeculationHint.AgentOpen, 0.98, 250), (SpeculationHint.AgentClick, 0.90, 120),
                     (SpeculationHint.AgentGoto, 0.98, 10), (SpeculationHint.AddressCommit, 0.995, 20),
                 })
        {
            var hint = SpeculationHint.For(source, url)!;
            Assert.Equal((source, p, lead, url), (hint.Source, hint.P, hint.LeadMs, hint.Url));
            Assert.NotNull(NightlyProtocol.SpeculateJson(hint));
        }
        Assert.Equal(SpeculationHint.MaxLeadMs, SpeculationHint.For(SpeculationHint.AgentClick, url, 99_999)!.LeadMs);
        Assert.Equal(0, SpeculationHint.For(SpeculationHint.AgentClick, url, -5)!.LeadMs);
        Assert.NotEqual(SpeculationHint.For(SpeculationHint.AgentOpen, url)!.Id, SpeculationHint.For(SpeculationHint.AgentOpen, url)!.Id);
        Assert.Throws<ArgumentOutOfRangeException>(() => SpeculationHint.For("hover", url));
    }

    [Theory]
    [InlineData("ftp://example.com/file")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,hi")]
    [InlineData("about:blank")]
    [InlineData("wss://example.com/socket")]
    [InlineData("https://user:secret@example.com/")]
    [InlineData("http://user@example.com/")]
    public void NoHintForANonHttpAddressOrOneWithCredentials(string address)
    {
        var url = new Uri(address);
        Assert.Null(SpeculationHint.For(SpeculationHint.AgentOpen, url));
        Assert.Null(NightlyProtocol.SpeculateJson(new SpeculationHint("0123456789abcdef", url, SpeculationHint.AgentOpen, 0.5, 10, 1000)));
    }

    [Fact]
    public void NoHintForARelativeAddress()
    {
        Assert.Null(SpeculationHint.For(SpeculationHint.AgentGoto, new Uri("/next", UriKind.Relative)));
    }

    [Fact]
    public void OutOfBoundsHintsAreRefusedAndTheBoundsThemselvesAccepted()
    {
        var ok = new SpeculationHint("0123456789abcdef", new Uri("https://example.com/"), SpeculationHint.AddressCommit, 0.5, 100, 1000);
        Assert.NotNull(NightlyProtocol.SpeculateJson(ok));
        foreach (var bad in new[]
                 {
                     ok with { P = -0.01 }, ok with { P = 1.01 }, ok with { P = double.NaN }, ok with { P = double.PositiveInfinity },
                     ok with { LeadMs = -1 }, ok with { LeadMs = 5001 }, ok with { TtlMs = 99 }, ok with { TtlMs = 30_001 },
                     ok with { Id = "0123456789ABCDEF" }, ok with { Id = "0123456789abcde" }, ok with { Id = "0123456789abcdeg" },
                     ok with { Id = "0123456789abcdef0" }, ok with { Source = "hover" }, ok with { Source = "" },
                 })
            Assert.Null(NightlyProtocol.SpeculateJson(bad));
        foreach (var edge in new[]
                 {
                     ok with { P = 0 }, ok with { P = 1 }, ok with { LeadMs = 0 }, ok with { LeadMs = 5000 },
                     ok with { TtlMs = 100 }, ok with { TtlMs = 30_000 },
                 })
            Assert.NotNull(NightlyProtocol.SpeculateJson(edge));
        Assert.Null(NightlyProtocol.CancelSpeculationJson("not-an-id"));
        // A URL too long for the command is no message at all.
        var huge = ok with { Url = new Uri("https://example.com/" + new string('a', NightlyProtocol.MaxText)) };
        Assert.Null(NightlyProtocol.SpeculateMessage(1, NightlyProtocol.SpeculateJson(huge)!));
    }
}
