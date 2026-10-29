using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App;
using Ombra.App.Controls;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.ClientCore;
using Ombra.ClientCore.Browser;

namespace Ombra.Tests;

/// <summary>Annotations: comments left on points and areas of the previewed page, sent to the agent with the next message.</summary>
public sealed class PreviewAnnotationTests
{
    private static PreviewViewModel Opened(string url = "http://localhost:5173/")
    {
        var vm = new PreviewViewModel();
        vm.OnNavigationCompleted(new Uri(url), true, false, false);
        return vm;
    }

    private static string Message(PreviewViewModel vm, string comment = "Make it blue", string? token = null, string id = "a1",
        string selector = "#login > button.btn", string url = "http://localhost:5173/", string source = "src/Login.tsx:42",
        string kind = "point", int width = 0, int height = 0, object[]? inside = null, object? strokes = null)
    {
        var mark = new Dictionary<string, object>
        {
            ["kind"] = kind, ["x"] = 452.4, ["y"] = 400, ["width"] = width, ["height"] = height, ["pageX"] = 452, ["pageY"] = 1200,
            ["offsetX"] = 40, ["offsetY"] = 20, ["viewportWidth"] = 1280, ["viewportHeight"] = 800,
        };
        if (strokes is not null) mark["strokes"] = strokes;
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "annotation", ["token"] = token ?? vm.AnnotationToken, ["id"] = id, ["comment"] = comment, ["url"] = url, ["title"] = "Shop",
            ["mark"] = mark,
            ["element"] = new Dictionary<string, object>
            {
                ["selector"] = selector, ["tag"] = "button", ["html"] = "<button class=\"btn\" type=\"submit\">", ["text"] = "Sign in",
                ["source"] = source, ["x"] = 412.4, ["y"] = 380, ["width"] = 120, ["height"] = 40,
            },
            ["inside"] = inside ?? [],
        });
    }

    private static readonly int[][] TwoStrokes = [[10, 20, 30, 40, 50, 25], [60, 70]];

    [Fact]
    public void Sketches_come_with_their_strokes_and_are_drawn_from_them()
    {
        var vm = Opened();
        Assert.Null(vm.OnPageMessage(Message(vm, kind: "sketch", width: 120, height: 60))); // no strokes
        Assert.Null(vm.OnPageMessage(Message(vm, kind: "sketch", width: 120, height: 60, strokes: new[] { new[] { 1, 2, 3 } }))); // odd
        Assert.Null(vm.OnPageMessage(Message(vm, kind: "sketch", width: 120, height: 60, strokes: new[] { Enumerable.Range(0, 2402).ToArray() }))); // too many points
        Assert.Null(vm.OnPageMessage(Message(vm, kind: "sketch", strokes: TwoStrokes))); // no box
        Assert.Empty(vm.Marks.Items);

        Assert.Equal("annotation", vm.OnPageMessage(Message(vm, comment: "Login form here", kind: "sketch", width: 120, height: 60, strokes: TwoStrokes)));
        var sketch = Assert.Single(vm.Marks.Items);
        Assert.Equal(PageMarkKind.Sketch, sketch.Page!.Mark.Kind);
        Assert.Equal(TwoStrokes, sketch.Page.Mark.Strokes);
        Assert.Equal(Loc.F("Sketch {0}×{1} over {2}", 120, 60, "button — Sign in"), sketch.TargetLabel);
        Assert.Null(sketch.Image); // no snapshot on a tab without a page view
        var pins = vm.PinsJson(new Uri("http://localhost:5173/"));
        Assert.Contains("\"kind\":\"sketch\"", pins);
        Assert.Contains("\"strokes\":[[10,20,30,40,50,25],[60,70]]", pins);
        // Points and areas carry none
        Assert.Equal("annotation", vm.OnPageMessage(Message(vm, id: "a2")));
        Assert.Contains("\"kind\":\"point\"", vm.PinsJson(new Uri("http://localhost:5173/")));
        Assert.Contains("\"strokes\":null", vm.PinsJson(new Uri("http://localhost:5173/")));
    }

    [Fact]
    public async Task A_sketch_gets_its_picture_from_the_tab_snapshot_cropped_and_bounded()
    {
        var vm = Opened();
        PreviewViewModel.SketchPictureDelay = TimeSpan.Zero;
        // The viewport (1280×800 CSS px) at 2×: the picture is the sketch's box and some page around it, ≤ 1600 px
        using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(2560, 1600));
        surface.Canvas.Clear(SkiaSharp.SKColors.White);
        using var shot = surface.Snapshot().Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        var shots = 0;
        vm.ActiveTab.CaptureScreenshot = _ => { shots++; return Task.FromResult(new AgentScreenshot(shot.ToArray(), 2560, 1600)); };
        Assert.Equal("annotation", vm.OnPageMessage(Message(vm, kind: "sketch", width: 300, height: 200, strokes: TwoStrokes)));
        var mark = vm.Marks.Items.Single();
        for (var i = 0; i < 100 && mark.Image is null; i++) await Task.Delay(50);
        var image = Assert.IsType<ImageAttachment>(mark.Image);
        Assert.Equal(1, shots);
        Assert.Equal("image/png", image.MimeType);
        Assert.True(image.Data.Length <= Ombra.App.Services.ImageAttachments.MaxSketchBytes);
        using var picture = SkiaSharp.SKBitmap.Decode(image.Data);
        // (452 - 150, 400 - 120) … (452 + 300 + 150, 400 + 200 + 120) CSS px, ×2, then ≤ 1600 on the long side
        Assert.True(picture.Width <= 1600 && picture.Height <= 1600, $"{picture.Width}×{picture.Height}");
        Assert.Equal(1200.0 / 880, (double)picture.Width / picture.Height, 2);
        Assert.Same(image, mark.Model.Image); // the draft keeps it
    }

    [AvaloniaFact]
    public void Switching_tabs_keeps_annotate_mode_and_the_tool()
    {
        var vm = Opened();
        vm.ToggleAnnotateCommand.Execute(null);
        Assert.Equal("annotate-tool", vm.OnPageMessage(JsonSerializer.Serialize(new { type = "annotate-tool", tool = "brush", token = vm.AnnotationToken })));
        Assert.Equal(AnnotateTool.Brush, vm.AnnotateTool);
        var first = vm.ActiveTab;
        vm.NewTabCommand.Execute(null);
        vm.NavigateCommand.Execute("localhost:4000");
        Assert.NotSame(first, vm.ActiveTab);
        Assert.True(vm.IsAnnotating);
        Assert.Equal(AnnotateTool.Brush, vm.AnnotateTool);
        vm.SelectTabCommand.Execute(first);
        Assert.Same(first, vm.ActiveTab);
        Assert.True(vm.IsAnnotating);
        Assert.Equal(AnnotateTool.Brush, vm.AnnotateTool);
        // Esc on the page ends the mode; the tool stays for the next time
        vm.OnPageMessage(JsonSerializer.Serialize(new { type = "annotate-exit", token = vm.AnnotationToken }));
        Assert.Equal((false, AnnotateTool.Brush), (vm.IsAnnotating, vm.AnnotateTool));
        Assert.Equal("annotate-tool", vm.OnPageMessage(JsonSerializer.Serialize(new { type = "annotate-tool", tool = "mark", token = vm.AnnotationToken })));
        Assert.Equal(AnnotateTool.Mark, vm.AnnotateTool);
    }

    [Fact]
    public void Only_messages_from_our_script_become_comments()
    {
        var vm = Opened();
        Assert.Null(vm.OnPageMessage(Message(vm, token: "forged")));
        Assert.Null(vm.OnPageMessage("{not json"));
        Assert.Null(vm.OnPageMessage(""));
        Assert.Null(vm.OnPageMessage(Message(vm, comment: "   ")));
        Assert.Null(vm.OnPageMessage(Message(vm, selector: "")));
        Assert.Null(vm.OnPageMessage(Message(vm, kind: "element")));
        Assert.Null(vm.OnPageMessage(Message(vm, kind: "area"))); // an area needs a size
        Assert.Null(vm.OnPageMessage(new string('x', 70_000)));
        Assert.Empty(vm.Marks.Items);

        Assert.Equal("annotation", vm.OnPageMessage(Message(vm)));
        Assert.Null(vm.OnPageMessage(Message(vm))); // the same id twice
        Assert.Equal("annotation", vm.OnPageMessage(Message(vm, comment: "Less padding", id: "a2", selector: "main > p:nth-of-type(2)", source: "")));
        Assert.Equal([1, 2], vm.Marks.Items.Select(a => a.Number));
        var first = vm.Marks.Items[0];
        Assert.Equal(("Make it blue", 452, 1280, 412), (first.Note, first.Page!.Mark.X, first.Page.Mark.ViewportWidth, first.Page.Element.X));
        Assert.Equal(Loc.F("Point on {0}", "button — Sign in"), first.TargetLabel);
        Assert.Equal(Loc.Plural(2, "1 comment", "{0} comments"), vm.Marks.Title);

        first.RemoveCommand.Execute(null);
        Assert.Equal(1, vm.Marks.Items.Single().Number); // renumbered like the pins
        Assert.Equal("ready", vm.OnPageMessage(JsonSerializer.Serialize(new { type = "ready", token = vm.AnnotationToken })));
    }

    [Fact]
    public void Annotate_mode_needs_a_page_and_the_page_can_end_it()
    {
        var empty = new PreviewViewModel();
        Assert.False(empty.ToggleAnnotateCommand.CanExecute(null));
        var vm = Opened();
        vm.ToggleAnnotateCommand.Execute(null);
        Assert.True(vm.IsAnnotating);
        Assert.Equal("annotate-exit", vm.OnPageMessage(JsonSerializer.Serialize(new { type = "annotate-exit", token = vm.AnnotationToken })));
        Assert.False(vm.IsAnnotating);
    }

    [Fact]
    public void Comments_are_limited_in_number_and_length()
    {
        var vm = Opened();
        for (var i = 0; i < LocalMarks.MaxMarks; i++) Assert.Equal("annotation", vm.OnPageMessage(Message(vm, id: "a" + i)));
        Assert.Null(vm.OnPageMessage(Message(vm, id: "over")));
        Assert.Equal(Loc.F("At most {0} comments at a time: send or clear some first.", LocalMarks.MaxMarks), vm.Message);
        vm.ClearAnnotationsCommand.Execute(null);
        vm.OnPageMessage(Message(vm, comment: new string('c', 5000), selector: new string('s', 900)));
        var a = vm.Marks.Items.Single();
        Assert.True(a.Note.Length <= 2001 && a.Page!.Element.Selector.Length <= 401);
    }

    [Theory]
    [InlineData("😀")]
    [InlineData("e\u0301")]
    [InlineData("👩‍💻")]
    public void Annotation_comments_and_element_fields_keep_whole_graphemes(string glyph)
    {
        for (var cut = 1; cut < glyph.Length; cut++)
        {
            var vm = Opened();
            var commentHead = new string('c', 2000 - cut);
            var sourceHead = new string('s', 400 - cut);
            Assert.Equal("annotation", vm.OnPageMessage(Message(vm,
                comment: commentHead + glyph + " rest", source: sourceHead + glyph + " rest")));
            var annotation = Assert.Single(vm.Marks.Items);
            Assert.Equal(commentHead + "…", annotation.Note);
            Assert.Equal(sourceHead + "…", annotation.Page!.Element.Source);
            annotation.BeginEditCommand.Execute(null);
            annotation.EditText = sourceHead + new string('d', 1600) + glyph + " rest";
            annotation.SaveEditCommand.Execute(null);
            Assert.Equal(sourceHead + new string('d', 1600) + "…", annotation.Note);
        }
    }

    [Fact]
    public void A_comment_can_be_changed_or_removed_from_its_pin_or_its_chip()
    {
        var vm = Opened();
        vm.OnPageMessage(Message(vm));
        vm.OnPageMessage(Message(vm, comment: "Less padding", id: "a2", url: "http://localhost:5173/about", selector: "main > p"));
        var edits = 0;
        vm.Marks.Edited += () => edits++;

        // From the pin on the page
        string Page(string type, string id, string? comment = null) =>
            JsonSerializer.Serialize(new { type, token = vm.AnnotationToken, id, comment });
        Assert.Equal("annotation-update", vm.OnPageMessage(Page("annotation-update", "a1", "Make it red")));
        Assert.Equal("Make it red", vm.Marks.Items[0].Note);
        Assert.Null(vm.OnPageMessage(Page("annotation-update", "nope", "x"))); // an unknown comment
        Assert.Null(vm.OnPageMessage(JsonSerializer.Serialize(new { type = "annotation-update", token = "forged", id = "a1", comment = "x" })));
        Assert.Equal("annotation-remove", vm.OnPageMessage(Page("annotation-remove", "a1")));
        Assert.Equal(1, vm.Marks.Items.Single().Number); // renumbered

        // From the chip: an empty text changes nothing, Cancel keeps it, Save keeps the new text and tells the pins
        var chip = vm.Marks.Items[0];
        chip.BeginEditCommand.Execute(null);
        Assert.Equal(("Less padding", true), (chip.EditText, chip.IsEditing));
        chip.EditText = "   ";
        chip.SaveEditCommand.Execute(null);
        Assert.Equal("Less padding", chip.Note);
        chip.BeginEditCommand.Execute(null);
        chip.EditText = "No padding";
        chip.CancelEditCommand.Execute(null);
        Assert.Equal("Less padding", chip.Note);
        chip.BeginEditCommand.Execute(null);
        chip.EditText = " No padding ";
        chip.SaveEditCommand.Execute(null);
        Assert.Equal(("No padding", false), (chip.Note, chip.IsEditing));
        Assert.Equal(2, edits);
        Assert.Contains("1. No padding", LocalMarks.PageBlock(vm.Marks.Models())); // the agent gets the new text

        // Resolved: it stays listed (its pin too) but is not sent; reopened, it is sent again
        chip.ToggleResolvedCommand.Execute(null);
        Assert.Equal((MarkStatus.Resolved, 3), (chip.Status, edits));
        Assert.Equal("", LocalMarks.PageBlock(vm.Marks.Models()));
        Assert.Single(JsonDocument.Parse(vm.PinsJson(new Uri("http://localhost:5173/"))).RootElement.EnumerateArray());
        chip.ToggleResolvedCommand.Execute(null);
        Assert.Contains("1. No padding", LocalMarks.PageBlock(vm.Marks.Models()));

        // The chip names its page and opens it
        Assert.Equal("localhost:5173/about", chip.Where);
        Assert.StartsWith("Shop — localhost:5173/about\n" + Loc.F("Point on {0}", "button — Sign in"), chip.Tooltip);
        var shown = 0;
        vm.ShowRequested += () => shown++;
        vm.Marks.ShowRequested += m => vm.ShowMark(m);
        chip.ShowCommand.Execute(null);
        Assert.Equal(1, shown);
        Assert.Equal("http://localhost:5173/about", vm.Address);
    }

    [Fact]
    public void Pins_and_the_prompt_say_where_each_mark_is_and_what_holds_it()
    {
        var vm = Opened();
        vm.OnPageMessage(Message(vm));
        vm.OnPageMessage(Message(vm, comment: "Remove this", id: "a2", url: "http://localhost:5173/about", selector: "footer", source: ""));
        vm.OnPageMessage(Message(vm, comment: "Tighter cards", id: "a3", selector: "section.cards", kind: "area", width: 240, height: 120,
            inside: [new { selector = "#buy", tag = "button", text = "Buy" }, new { selector = "main > h2", tag = "h2", text = "" }]));
        Assert.Equal(Loc.F("Area {0}×{1} in {2}", 240, 120, "button — Sign in"), vm.Marks.Items[2].TargetLabel);
        vm.OnPageMessage(Message(vm, comment: "Elsewhere", id: "a4", url: "http://localhost:3000/"));
        var pins = JsonDocument.Parse(vm.PinsJson(new Uri("http://localhost:5173/#top"))).RootElement;
        // The site's pins, each with its page (the script draws a pin on its own page only); another site's never
        Assert.Equal(["http://localhost:5173/", "http://localhost:5173/about", "http://localhost:5173/"],
            pins.EnumerateArray().Select(p => p.GetProperty("url").GetString()));
        Assert.Equal(("point", "#login > button.btn", 40, 20, 1200), (pins[0].GetProperty("kind").GetString(), pins[0].GetProperty("selector").GetString(),
            pins[0].GetProperty("offsetX").GetInt32(), pins[0].GetProperty("offsetY").GetInt32(), pins[0].GetProperty("pageY").GetInt32()));
        Assert.Equal(("area", 240, 120), (pins[2].GetProperty("kind").GetString(), pins[2].GetProperty("width").GetInt32(), pins[2].GetProperty("height").GetInt32()));
        vm.Marks.Remove(vm.Marks.Items.Single(a => a.Id == "a4"));

        var prompt = LocalMarks.PageBlock(vm.Marks.Models());
        Assert.Contains("Comments on the page http://localhost:5173/ (\"Shop\")", prompt);
        Assert.Contains("1. Make it blue\n   Point: 452,400 in a 1280×800 viewport (452,1200 on the page)\n", prompt);
        Assert.Contains("   On: <button class=\"btn\" type=\"submit\"> with the text \"Sign in\"", prompt);
        Assert.Contains("Selector: #login > button.btn", prompt);
        Assert.Contains("Source: src/Login.tsx:42", prompt);
        Assert.Contains("Element box: 120×40 at 412,380", prompt);
        Assert.Contains("3. Tighter cards\n   Area: 240×120 at 452,400 in a 1280×800 viewport (452,1200 on the page)\n   Within: ", prompt);
        Assert.Contains("Inside the area: button \"Buy\" (#buy); main > h2", prompt);
        Assert.Contains("Comments on the page http://localhost:5173/about", prompt);
        Assert.Contains("2. Remove this", prompt);
        Assert.EndsWith("make the changes the comments ask for.", prompt);
    }

    [Theory]
    [InlineData("http://localhost:5173/a", "http://localhost:5173/a#top", true)]      // an anchor is the same page
    [InlineData("http://localhost:5173/a", "http://localhost:5173/b", false)]
    [InlineData("http://localhost:5173/a?tab=1", "http://localhost:5173/a?tab=2", false)]
    [InlineData("http://localhost:5173/#/cart", "http://localhost:5173/#/orders", false)] // hash routes are pages
    [InlineData("http://localhost:5173/#!/cart", "http://localhost:5173/#!/cart", true)]
    [InlineData("http://localhost:5173/#/cart", "http://localhost:5173/", false)]
    public void A_comment_belongs_to_its_page_route_included_anchor_not(string a, string b, bool same) =>
        Assert.Equal(same, PreviewViewModel.SamePage(new Uri(a), new Uri(b)));

    [Fact]
    public void A_route_change_inside_the_page_moves_the_address_on_the_same_site_only()
    {
        var vm = Opened();
        string Location(string url, string? token = null) => JsonSerializer.Serialize(new { type = "location", token = token ?? vm.AnnotationToken, url });
        Assert.Equal("location", vm.OnPageMessage(Location("http://localhost:5173/orders")));
        Assert.Equal("http://localhost:5173/orders", vm.CurrentUrl?.AbsoluteUri);
        Assert.Null(vm.OnPageMessage(Location("https://evil.example/")));
        Assert.Null(vm.OnPageMessage(Location("http://localhost:5173/x", token: "forged")));
        Assert.Equal("http://localhost:5173/orders", vm.CurrentUrl?.AbsoluteUri);
    }

    [Fact]
    public void The_page_script_carries_the_token_and_parses()
    {
        var vm = new PreviewViewModel();
        var script = PreviewPanel.AnnotateScriptFor(vm.AnnotationToken);
        Assert.EndsWith("})(\"" + vm.AnnotationToken + "\", false);", script);
        Assert.EndsWith("})", PreviewPanel.AnnotateFunction); // Nightly's isolated world calls it with its arguments
        Assert.DoesNotContain(vm.AnnotationToken, PreviewPanel.AnnotateFunction);
        Assert.NotEqual(vm.AnnotationToken, new PreviewViewModel().AnnotationToken);
        // A syntax check where Node is at hand (the real engines run it in the E2E check): the page's form, and the
        // function as Nightly's isolated call wraps it (ZenEmbedAgentChild's ISOLATED_HEAD)
        if (Node() is not { } node) return;
        var dir = TestProcesses.TempDir("annotate");
        foreach (var (name, source) in new[] { ("page.js", script), ("isolated.js", "(async (__ompArgs) => {\nconst __ompFn = (\n" + PreviewPanel.AnnotateFunction + "\n);\n})") })
        {
            var file = Path.Combine(dir, name);
            File.WriteAllText(file, source);
            using var p = Process.Start(new ProcessStartInfo(node, ["--check", file]) { RedirectStandardError = true })!;
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, name + ": " + err);
        }
    }

    private static string? Node() =>
        new[] { "/opt/node22/bin/node", "/usr/bin/node", "/usr/local/bin/node", "/opt/homebrew/bin/node" }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(d => Path.Combine(d, "node")))
            .FirstOrDefault(File.Exists);

    /// <summary>
    /// The script's two hosts, run in Node over a stand-in DOM: queued (Nightly's isolated world, no host bridge) its
    /// messages wait for one reader — <c>next()</c> answers what is there, a newer reader ends the earlier one's wait with
    /// null, a message reaches the waiting reader (Esc in annotate mode: "annotate-exit"), a second start on the document
    /// changes nothing; in the page's world (the built-in preview) they go to <c>invokeCSharpAction</c> and there is no reader.
    /// </summary>
    [Fact]
    public void Queued_the_script_keeps_its_messages_for_one_reader_and_in_the_page_world_posts_them()
    {
        if (Node() is not { } node) return;
        var dir = TestProcesses.TempDir("annotate-queue");
        var script = Path.Combine(dir, "annotate.js");
        File.WriteAllText(script, PreviewPanel.AnnotateFunction);
        var harness = Path.Combine(dir, "harness.js");
        File.WriteAllText(harness, """
            const fs = require("fs");
            const listeners = {};
            function node() {
              return { style: {}, classList: { add() {}, remove() {} }, addEventListener() {}, appendChild() {}, setAttribute() {},
                querySelector() { return node(); }, attachShadow() { return node(); },
                getBoundingClientRect() { return { left: 0, top: 0, right: 0, bottom: 0, width: 0, height: 0 }; } };
            }
            globalThis.window = globalThis;
            globalThis.document = { createElement: node, documentElement: node(), body: node(), querySelectorAll: () => [], elementFromPoint: () => null,
              addEventListener: (t, f) => { listeners[t] = f; }, removeEventListener: (t) => { delete listeners[t]; } };
            globalThis.location = { href: "http://localhost:5173/" };
            globalThis.requestAnimationFrame = () => 0;
            globalThis.setInterval = () => 0;
            window.addEventListener = () => {};
            window.removeEventListener = () => {};
            const start = eval(fs.readFileSync(process.argv[2], "utf8"));
            (async () => {
              const out = {};
              start("tok", true);
              const a = window.__ompAnnotate;
              out.first = await a.next();
              const replaced = a.next();
              const reader = a.next();
              out.replaced = await replaced;
              a.setMode(true);
              listeners.keydown({ key: "Escape", preventDefault() {}, stopPropagation() {} });
              out.exit = await reader;
              start("other", true);
              out.same = window.__ompAnnotate === a;
              delete window.__ompAnnotate;
              const posted = [];
              window.invokeCSharpAction = m => posted.push(m);
              start("page", false);
              out.posted = posted;
              out.pageNext = window.__ompAnnotate.next();
              console.log(JSON.stringify(out));
            })();
            """);
        using var p = Process.Start(new ProcessStartInfo(node, [harness, script]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, err);
        using var doc = JsonDocument.Parse(output);
        var o = doc.RootElement;
        static string[] Strings(JsonElement e) => [.. e.EnumerateArray().Select(x => x.GetString()!)];
        Assert.Equal(["{\"type\":\"ready\",\"token\":\"tok\"}"], Strings(o.GetProperty("first")));
        Assert.Equal(JsonValueKind.Null, o.GetProperty("replaced").ValueKind);
        Assert.Equal(["{\"type\":\"annotate-exit\",\"token\":\"tok\"}"], Strings(o.GetProperty("exit")));
        Assert.True(o.GetProperty("same").GetBoolean());
        Assert.Equal(["{\"type\":\"ready\",\"token\":\"page\"}"], Strings(o.GetProperty("posted")));
        Assert.Equal(JsonValueKind.Null, o.GetProperty("pageNext").ValueKind);
    }

    /// <summary>Nightly's isolated world as the annotations see it: every call recorded, the reader's left waiting for the
    /// test to answer, the others answered at once (<see cref="Answer"/>).</summary>
    private sealed class FakeIsolatedWorld : IIsolatedScripts
    {
        public readonly List<(string Source, string Args, TaskCompletionSource<string?> Answer)> Calls = [];
        public string? Answer;
        public string? Unsupported => null;

        public Task<string?> CallAsync(string functionSource, string argsJson, CancellationToken ct)
        {
            var answer = new TaskCompletionSource<string?>();
            Calls.Add((functionSource, argsJson, answer));
            if (functionSource != IsolatedPageAnnotations.NextSource) answer.SetResult(Answer);
            return answer.Task;
        }

        public int Count(string source) => Calls.Count(c => c.Source == source);

        public string LastArgs(string source) => Calls.Last(c => c.Source == source).Args;

        /// <summary>The reader's call waiting now, once there is one other than <paramref name="after"/>.</summary>
        public async Task<TaskCompletionSource<string?>> Read(TaskCompletionSource<string?>? after = null)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (true)
            {
                Dispatcher.UIThread.RunJobs();
                var open = Calls.LastOrDefault(c => c.Source == IsolatedPageAnnotations.NextSource && !c.Answer.Task.IsCompleted).Answer;
                if (open is not null && open != after) return open;
                Assert.True(DateTime.UtcNow < deadline, "no read waiting");
                await Task.Delay(10);
            }
        }
    }

    private static async Task Settle()
    {
        for (var i = 0; i < 10; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
    }

    private static string Batch(params string[] messages) => JsonSerializer.Serialize(messages);

    /// <summary>
    /// A page Nightly shows: the script starts in its isolated world with the panel's token once a document has loaded,
    /// mode and pins go to it as arguments, and its messages come back through one waiting read — a comment becomes a
    /// mark, Esc ends the mode. A title change starts nothing; another document gets the script and a new reader, the
    /// earlier one stops when the script ends its wait; another tab's and forged messages are nothing; a read whose
    /// document went stops; a closed tab calls nothing.
    /// </summary>
    [AvaloniaFact]
    public async Task A_Nightly_page_runs_the_script_in_its_isolated_world_and_one_reader_takes_its_messages()
    {
        var vm = Opened();
        var world = new FakeIsolatedWorld();
        var shown = true;
        var page = new IsolatedPageAnnotations(world, vm, () => shown);
        await page.OnLoadedAsync("http://localhost:5173/");
        Assert.Equal("[\"" + vm.AnnotationToken + "\",true]", Assert.Single(world.Calls, c => c.Source == PreviewPanel.AnnotateFunction).Args);
        Assert.Equal("[\"mark\",false,[],null]", world.LastArgs(IsolatedPageAnnotations.SyncSource));
        var first = await world.Read();

        var ready = JsonSerializer.Serialize(new { type = "ready", token = vm.AnnotationToken });
        first.SetResult(Batch(ready, Message(vm)));
        var second = await world.Read(after: first);
        Assert.Equal("Make it blue", Assert.Single(vm.Marks.Items).Note);
        Assert.Contains("\"id\":\"a1\",\"number\":1", world.LastArgs(IsolatedPageAnnotations.SyncSource)); // its pin, numbered

        await page.OnLoadedAsync("http://localhost:5173/"); // the title changed
        Assert.Equal(1, world.Count(PreviewPanel.AnnotateFunction));
        vm.IsAnnotating = true;
        await page.SyncAsync();
        Assert.StartsWith("[\"mark\",true,[{", world.LastArgs(IsolatedPageAnnotations.SyncSource));
        // A team member's mark asked for: done once the page says it scrolled to the pin
        vm.PendingReveal = "team-m1-i1";
        world.Answer = "true";
        await page.SyncAsync();
        Assert.EndsWith(",\"team-m1-i1\"]", world.LastArgs(IsolatedPageAnnotations.SyncSource));
        Assert.Null(vm.PendingReveal);
        world.Answer = null;

        page.OnLoading();
        await page.OnLoadedAsync("http://localhost:5173/cart");
        Assert.Equal(2, world.Count(PreviewPanel.AnnotateFunction));
        var third = await world.Read(after: second);
        second.SetResult("null"); // the script ended the earlier reader's wait
        await Settle();
        third.SetResult(Batch(JsonSerializer.Serialize(new { type = "annotate-exit", token = vm.AnnotationToken })));
        var fourth = await world.Read(after: third);
        Assert.False(vm.IsAnnotating);
        Assert.Equal(4, world.Count(IsolatedPageAnnotations.NextSource)); // the stopped reader asked no more

        shown = false;
        fourth.SetResult(Batch(Message(vm, id: "a2")));
        var fifth = await world.Read(after: fourth);
        shown = true;
        fifth.SetResult(Batch(Message(vm, id: "a3", token: "forged")));
        var sixth = await world.Read(after: fifth);
        Assert.Single(vm.Marks.Items);

        sixth.SetException(new IsolatedScriptException("The page is gone."));
        await Settle();
        Assert.Equal(6, world.Count(IsolatedPageAnnotations.NextSource));
        page.Close();
        page.OnLoading();
        await page.OnLoadedAsync("http://localhost:5173/other");
        await page.SyncAsync();
        Assert.Equal(6, world.Count(IsolatedPageAnnotations.NextSource));
        Assert.Equal(2, world.Count(PreviewPanel.AnnotateFunction));
    }

    /// <summary>
    /// The annotations of a tab Nightly shows are Ombra's own calls, not the agent acting on the page: the script's start,
    /// the reader waiting on the page (for as long as it lives) and the sync leave the pane as hidden as its tab is, so
    /// Nightly hides an agent tab behind another one or in a hidden panel. The agent's own call makes the page seen
    /// (<see cref="NightlyPane.AgentSeen"/> after it). Release gate 0.1.1: every right-panel tab counted as acting forever.
    /// </summary>
    [AvaloniaFact]
    public async Task A_Nightly_tabs_annotations_are_not_the_agent_acting_on_it()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var vm = Opened();
        var pane = new NightlyPane(); // no window, no runtime: every call fails at once, after it was counted or not
        try
        {
            var page = PreviewPanel.NightlyAnnotations(pane.Agent, vm, () => true);
            await page.OnLoadedAsync("http://localhost:5173/");
            await page.SyncAsync();
            await Settle();
            Assert.False(pane.AgentActing);

            try
            {
                await pane.Agent.CallAsync("() => 1", "[]", CancellationToken.None);
                Assert.Fail("a pane without a window answered");
            }
            catch (IsolatedScriptException) { }
            Assert.True(pane.AgentActing);
        }
        finally
        {
            pane.Close();
        }
    }

    [Fact]
    public void Short_messages_are_not_folded()
    {
        var row = new UserRowViewModel(new UserItem(1, "short", true));
        Assert.False(row.IsLong);
        Assert.Equal("short", row.DisplayText);
    }

    [AvaloniaFact]
    public async Task Comments_go_with_the_next_message_and_are_cleared()
    {
        var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs());
        var w = new Ombra.App.Views.MainWindow { DataContext = vm, Width = 1180, Height = 760 };
        w.Show();
        vm.OnWindowOpened();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (vm.Phase != SessionPhase.Ready && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); await Task.Delay(15); }
        Assert.False(vm.SendCommand.CanExecute(null));

        var preview = vm.Preview;
        preview.OnNavigationCompleted(new Uri("http://localhost:5173/"), true, false, false);
        preview.OnPageMessage(Message(preview));
        preview.OnPageMessage(Message(preview, comment: "Less padding", id: "a2", selector: "main > p"));
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.SendCommand.CanExecute(null)); // comments alone are a message
        Assert.True(w.FindControl<ItemsControl>("MarksList")!.IsEffectivelyVisible); // one chip per comment, like files

        vm.ComposerText = "Please fix these";
        vm.SendCommand.Execute(null); // sent with the message, like attachments (Codex)
        while (!vm.Rows.OfType<UserRowViewModel>().Any() && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); await Task.Delay(15); }
        var sent = vm.Rows.OfType<UserRowViewModel>().First().Text;
        Assert.StartsWith("Please fix these\n\nComments on the page http://localhost:5173/", sent);
        Assert.Contains("Selector: #login > button.btn", sent);
        Assert.Empty(vm.Marks.Items);
        var row = vm.Rows.OfType<UserRowViewModel>().First();
        // The bubble: the user's words and a chip; the machine-written block only on request (design review T1)
        Assert.True(row.HasComments);
        Assert.Equal("Please fix these", row.DisplayText);
        Assert.Equal(Loc.F("{0} on {1}", Loc.F("{0} comments", 2), "localhost:5173"), row.CommentsLabel);
        row.ToggleFoldCommand.Execute(null);
        Assert.Equal(row.Text, row.DisplayText);
        row.ToggleFoldCommand.Execute(null);

        // The chip lists the comments one by one; a comment opens its details, one at a time
        Assert.Equal(["Make it blue", "Less padding"], row.Comments.Select(c => c.Note));
        Assert.False(row.IsCommentsOpen);
        row.ToggleCommentsCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var list = w.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("sent-comments") && b.DataContext == row);
        Assert.True(list.IsEffectivelyVisible);
        row.ToggleCommentCommand.Execute(row.Comments[1]);
        Assert.Equal([false, true], row.Comments.Select(c => c.IsExpanded));
        Assert.Contains(row.Comments[1].Fields, f => f.Key == Loc.T("Selector") && f.Value == "main > p");
        Assert.DoesNotContain(row.Comments[1].Fields, f => f.Value.StartsWith("<button", StringComparison.Ordinal)); // the caption names it
        row.ToggleCommentCommand.Execute(row.Comments[0]);
        Assert.Equal([true, false], row.Comments.Select(c => c.IsExpanded));
        row.ToggleCommentCommand.Execute(row.Comments[0]);
        Assert.All(row.Comments, c => Assert.False(c.IsExpanded));
        row.ToggleCommentsCommand.Execute(null);

        // Comments alone: a first line says what to do (it also names a new session)
        while (vm.Phase != SessionPhase.Ready && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); await Task.Delay(15); }
        preview.OnPageMessage(Message(preview, id: "a3"));
        Dispatcher.UIThread.RunJobs();
        vm.SendCommand.Execute(null);
        while (vm.Rows.OfType<UserRowViewModel>().Count() < 2 && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); await Task.Delay(15); }
        var alone = vm.Rows.OfType<UserRowViewModel>().Last();
        Assert.StartsWith("Please make the changes my comments on the page ask for.\n\nComments on the page", alone.Text);
        Assert.False(alone.HasOwnText); // just the chip
        Assert.Equal(Loc.F("{0} on {1}", Loc.T("1 comment"), "localhost:5173"), alone.CommentsLabel);
        Assert.False(w.FindControl<ItemsControl>("MarksList")!.IsEffectivelyVisible);
        await vm.DisposeAsync();
        w.Close();
    }

    /// <summary>Marks from every place — a line in Changes, the editor, a diff in the conversation, a page — are one
    /// numbered list and one request: code first, then pages, resolved marks left out and kept.</summary>
    [AvaloniaFact]
    public async Task Marks_from_every_place_flow_into_one_request()
    {
        var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs());
        var w = new Ombra.App.Views.MainWindow { DataContext = vm, Width = 1180, Height = 760 };
        w.Show();
        vm.OnWindowOpened();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (vm.Phase != SessionPhase.Ready && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); await Task.Delay(15); }

        Assert.True(vm.AddCodeMark(new CodeAnchor("src/app.py", 41, false, "print('x')", CodeMarkOrigin.Review), "log it"));
        Assert.True(vm.AddCodeMark(new CodeAnchor("src/app.py", 3, false, "a = 1\nb = 2", CodeMarkOrigin.Editor, EndLine: 4, Before: ["import os"], After: ["c = 3"]), "merge these"));
        vm.CommentOnChangeCommand.Execute(new DiffLineComment("src/util.py", 7, true, "old()", ["before()"], ["after()"], "why removed?"));
        var preview = vm.Preview;
        preview.OnNavigationCompleted(new Uri("http://localhost:5173/"), true, false, false);
        preview.OnPageMessage(Message(preview));
        vm.AddCodeMark(new CodeAnchor("src/done.py", 1, false, "x"), "already done");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([1, 2, 3, 4, 5], vm.Marks.Items.Select(m => m.Number));
        Assert.Equal(CodeMarkOrigin.Conversation, vm.Marks.Items[2].Code!.Origin);
        vm.Marks.Items[4].ToggleResolvedCommand.Execute(null);
        Assert.True(w.FindControl<ItemsControl>("MarksList")!.IsEffectivelyVisible);
        Assert.Equal(5, w.FindControl<ItemsControl>("MarksList")!.ItemCount);

        vm.SendCommand.Execute(null);
        while (!vm.Rows.OfType<UserRowViewModel>().Any() && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); await Task.Delay(15); }
        var sent = vm.Rows.OfType<UserRowViewModel>().First().Text;
        Assert.StartsWith(LocalMarks.AllOnlyLine + "\n\n" + LocalMarks.CodeMarker, sent);
        Assert.Contains("1. src/app.py, line 41: log it\n   > print('x')\n", sent);
        Assert.Contains("2. src/app.py, lines 3-4 (in the editor): merge these\n   > a = 1\n   > b = 2\n   Lines before it:\n     import os\n   Lines after it:\n     c = 3\n", sent);
        Assert.Contains("3. src/util.py, line 7 of the last commit (a removed line) (on a change in the conversation): why removed?\n   > old()\n", sent);
        Assert.Contains("\n\n" + LocalMarks.PageMarker + "http://localhost:5173/", sent);
        Assert.Contains("\n4. Make it blue\n", sent);
        Assert.DoesNotContain("already done", sent);
        Assert.True(sent.IndexOf(LocalMarks.CodeMarker, StringComparison.Ordinal) < sent.IndexOf(LocalMarks.PageMarker, StringComparison.Ordinal));
        // The resolved mark stays; the sent ones are gone
        var left = Assert.Single(vm.Marks.Items);
        Assert.Equal(("already done", MarkStatus.Resolved, 1), (left.Note, left.Status, left.Number));
        Assert.False(vm.SendCommand.CanExecute(null)); // a resolved mark alone is not a message
        await vm.DisposeAsync();
        w.Close();
    }

    private static LocalMark SketchMark(string id, string note, ImageAttachment? image) =>
        new(id, note, Page: new PageAnchor("http://localhost:5173/login", "Login", new PageMark(PageMarkKind.Sketch, 100, 200, 300, 150, 100, 900, 4, 8, 1280, 800, TwoStrokes),
            new PageElement("main", "main", "<main>", "", "", 96, 192, 800, 600), [new PageInsideElement("form", "form", "Sign in")]), Image: image);

    [Fact]
    public void A_sketch_round_trips_through_the_draft_and_old_drafts_still_load()
    {
        var picture = new ImageAttachment("sketch-s1.png", "image/png", [0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);
        var json = JsonSerializer.Serialize(SketchMark("s1", "Login form here", picture));
        var back = JsonSerializer.Deserialize<LocalMark>(json)!;
        Assert.True(back.IsValid);
        Assert.Equal(PageMarkKind.Sketch, back.Page!.Mark.Kind);
        Assert.Equal(TwoStrokes, back.Page.Mark.Strokes);
        Assert.Equal(picture.Data, back.Image!.Data);
        Assert.Equal(("sketch-s1.png", "image/png"), (back.Image.Name, back.Image.MimeType));

        // A draft saved before sketches: its marks have neither strokes nor a picture
        var old = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new LocalMark("p1", "Make it blue", Page: new PageAnchor("http://localhost:5173/", "Shop",
            new PageMark(PageMarkKind.Area, 10, 20, 100, 40, 10, 1220, -6, -4, 1280, 800), new PageElement("#buy", "button", "<button>", "Buy", "", 0, 0, 80, 30), []))))!.AsObject();
        old.Remove("Image");
        old["Page"]!["Mark"]!.AsObject().Remove("Strokes");
        var loaded = old.Deserialize<LocalMark>()!;
        Assert.True(loaded.IsValid);
        Assert.Null(loaded.Page!.Mark.Strokes);
        Assert.Null(loaded.Image);
        Assert.Contains("Area: 100×40 at 10,20", LocalMarks.BuildRequest("", [loaded]));

        // Strokes that are not pairs of numbers make the mark invalid (the draft is refused, as other broken marks)
        Assert.False((back with { Page = back.Page with { Mark = back.Page.Mark with { Strokes = [[1, 2, 3]] } } }).IsValid);
        Assert.False((back with { Image = picture with { Data = [] } }).IsValid);
    }

    [Fact]
    public void The_request_names_each_sketch_s_image_and_gives_the_strokes_when_it_has_none()
    {
        var picture = new ImageAttachment("sketch-s1.png", "image/png", new byte[1000]);
        LocalMark[] marks = [
            new("c1", "rename", Code: new CodeAnchor("src/a.cs", 3, false, "x")),
            SketchMark("s1", "Login form here", picture),
            SketchMark("s2", "A footer like this", null),
            SketchMark("s3", "Resolved one", picture) with { Status = MarkStatus.Resolved },
        ];
        // One image of the user's own goes first: the sketch's is image 2
        var (images, numbers) = LocalMarks.SketchImages(marks, 2, 10_000);
        Assert.Equal([picture], images);
        Assert.Equal(new Dictionary<string, int> { ["s1"] = 2 }, numbers);
        var request = LocalMarks.BuildRequest("Build these", marks, numbers);
        Assert.Contains("Each marks a point, an area or a sketch (a rough drawing of what I want there) of the page", request);
        Assert.Contains("\n2. Sketch — see image 2: Login form here\n   Sketch: 2 strokes over 300×150 at 100,200 in a 1280×800 viewport (100,900 on the page)\n   Over: <main>\n", request);
        Assert.Contains("   Under the sketch: form \"Sign in\" (form)\n", request);
        Assert.Contains("\n3. Sketch: A footer like this\n", request);
        Assert.Contains("   Strokes (SVG path, CSS px on the page): M10 20 L30 40 L50 25 M60 70\n", request);
        Assert.DoesNotContain("Resolved one", request);
        Assert.Contains("build what each sketch roughly draws, where it is drawn.", request);
        // No room left in the message: no picture, the strokes instead
        var full = LocalMarks.SketchImages(marks, 1, 999);
        Assert.Empty(full.Images);
        Assert.Contains("\n2. Sketch: Login form here\n", LocalMarks.BuildRequest("", marks, full.Numbers));
    }

    /// <summary>The message box sends the user's images, then the sketches' pictures, and the text names them in that order.</summary>
    [AvaloniaFact]
    public async Task Sketch_pictures_go_with_the_message_after_its_own_images()
    {
        var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs());
        var w = new Ombra.App.Views.MainWindow { DataContext = vm, Width = 1180, Height = 760 };
        w.Show();
        vm.OnWindowOpened();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (vm.Phase != SessionPhase.Ready && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); await Task.Delay(15); }

        var own = new ImageAttachment("own.png", "image/png", [0x89, 0x50, 0x4E, 0x47, 9]);
        var picture = new ImageAttachment("sketch-s1.png", "image/png", [0x89, 0x50, 0x4E, 0x47, 7]);
        Assert.True(vm.TryAddImage(own, "test"));
        var preview = vm.Preview;
        preview.OnNavigationCompleted(new Uri("http://localhost:5173/"), true, false, false);
        Assert.Equal("annotation", preview.OnPageMessage(Message(preview, comment: "Login form here", kind: "sketch", width: 120, height: 60, strokes: TwoStrokes)));
        vm.Marks.SetImage(vm.Marks.Items.Single(), picture);
        vm.ComposerText = "Please";
        vm.SendCommand.Execute(null);
        while (!vm.Rows.OfType<UserRowViewModel>().Any() && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); await Task.Delay(15); }
        var row = vm.Rows.OfType<UserRowViewModel>().First();
        Assert.Contains("\n1. Sketch — see image 2: Login form here\n", row.Text);
        Assert.Equal([own.Data, picture.Data], row.Previews.Select(p => p.Model.Data));
        Assert.Empty(vm.Marks.Items);
        await vm.DisposeAsync();
        w.Close();
    }

    /// <summary>
    /// The brush in the page script, run in Node over a stand-in DOM: B (or the bar) switches to it and tells the client;
    /// drags draw strokes; after a pause the comment box opens for the sketch; its comment sends a "sketch" mark with
    /// the strokes in page px (the page scrolled by 100), simplified; a straight line keeps only its ends.
    /// </summary>
    [Fact]
    public void The_brush_draws_a_sketch_that_goes_out_with_its_strokes()
    {
        if (Node() is not { } node) return;
        var dir = TestProcesses.TempDir("annotate-brush");
        var script = Path.Combine(dir, "annotate.js");
        File.WriteAllText(script, PreviewPanel.AnnotateFunction);
        var harness = Path.Combine(dir, "harness.js");
        File.WriteAllText(harness, """
            const fs = require("fs");
            const listeners = {};
            function node(tag) {
              const n = { tagName: (tag || "div").toUpperCase(), style: {}, classList: { add() {}, remove() {} }, l: {}, q: {}, children: [],
                addEventListener(t, f) { n.l[t] = f; }, appendChild(c) { n.children.push(c); }, setAttribute() {}, getAttribute() { return null; },
                focus() {}, setSelectionRange() {}, value: "",
                querySelector(s) { return n.q[s] || (n.q[s] = node(s === "textarea" ? "textarea" : "div")); },
                attachShadow() { return globalThis.shadow = node(); },
                getBoundingClientRect() { return { left: 0, top: 0, right: 1280, bottom: 800, width: 1280, height: 800 }; } };
              Object.defineProperty(n, "textContent", { set() { n.children = []; }, get() { return ""; } });
              n.isConnected = true; n.nodeType = 1;
              return n;
            }
            globalThis.window = globalThis;
            const body = node("body");
            globalThis.document = { createElement: node, createElementNS: (ns, t) => node(t), documentElement: node("html"), body, querySelectorAll: () => [],
              elementFromPoint: () => null, title: "Login",
              addEventListener: (t, f) => { listeners[t] = f; }, removeEventListener: (t) => { delete listeners[t]; } };
            globalThis.location = { href: "http://localhost:5173/login" };
            globalThis.requestAnimationFrame = () => 0;
            globalThis.setInterval = () => 0;
            window.innerWidth = 1280; window.innerHeight = 800; window.scrollX = 0; window.scrollY = 100;
            window.addEventListener = () => {};
            window.removeEventListener = () => {};
            const posted = [];
            window.invokeCSharpAction = m => posted.push(JSON.parse(m));
            const ev = (x, y, more) => Object.assign({ button: 0, clientX: x, clientY: y, preventDefault() {}, stopPropagation() {}, stopImmediatePropagation() {} }, more);
            eval(fs.readFileSync(process.argv[2], "utf8"))("tok", false);
            const a = window.__ompAnnotate;
            a.setMode(true);
            listeners.keydown(ev(0, 0, { key: "b", target: body }));
            // A straight line (10 points) and a dot
            listeners.mousedown(ev(100, 100));
            for (let i = 1; i <= 10; i++) listeners.mousemove(ev(100 + i * 10, 100));
            listeners.mouseup(ev(200, 100));
            listeners.mousedown(ev(300, 150));
            listeners.mouseup(ev(300, 150));
            const out = { tool: posted.find(m => m.type === "annotate-tool"), doneShown: shadow.q[".done"].style.display };
            setTimeout(() => {
              out.box = shadow.q[".pop"].style.display;
              out.what = shadow.q[".what"].textContent;
              shadow.q["textarea"].value = "Login form here";
              shadow.q[".go"].l.click();
              out.mark = posted.find(m => m.type === "annotation");
              console.log(JSON.stringify(out));
            }, 1000);
            """);
        using var p = Process.Start(new ProcessStartInfo(node, [harness, script]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, err);
        using var doc = JsonDocument.Parse(output);
        var o = doc.RootElement;
        Assert.Equal("brush", o.GetProperty("tool").GetProperty("tool").GetString());
        Assert.Equal("block", o.GetProperty("doneShown").GetString());
        Assert.Equal("block", o.GetProperty("box").GetString());
        var message = o.GetProperty("mark");
        Assert.Equal("Login form here", message.GetProperty("comment").GetString());
        var mark = message.GetProperty("mark");
        Assert.Equal("sketch", mark.GetProperty("kind").GetString());
        Assert.Equal("[[100,200,200,200],[300,250]]", mark.GetProperty("strokes").GetRawText());
        Assert.Equal((100, 200, 200, 50), (mark.GetProperty("pageX").GetInt32(), mark.GetProperty("pageY").GetInt32(), mark.GetProperty("width").GetInt32(), mark.GetProperty("height").GetInt32()));
        // Its id and the client's message parse as a sketch mark
        var vm = Opened("http://localhost:5173/login");
        var withToken = System.Text.Json.Nodes.JsonNode.Parse(message.GetRawText())!.AsObject();
        withToken["token"] = vm.AnnotationToken;
        Assert.Equal("annotation", vm.OnPageMessage(withToken.ToJsonString()));
        Assert.Equal([[100, 200, 200, 200], [300, 250]], vm.Marks.Items.Single().Page!.Mark.Strokes);
    }
}
