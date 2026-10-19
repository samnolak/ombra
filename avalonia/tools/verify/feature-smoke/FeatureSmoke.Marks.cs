#if OMBRA_FEATURE_SMOKE
using Avalonia;
using Avalonia.Input;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Ombra.App.Controls;
using Ombra.App.ViewModels;
using Ombra.ClientCore;

namespace Ombra.App.Smoke;

internal static partial class FeatureSmoke
{
    private sealed partial class Run
    {
        private (string, Func<Task>)[] MarksSections() => [("marks", Marks)];

        /// <summary>A row of a control's open context menu (ContextMenus.Menu: a flyout whose content holds the rows).</summary>
        private static Button? MenuRow(Control target, string label) =>
            target.ContextFlyout is Flyout { IsOpen: true, Content: Control content }
                ? content.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => Label(b) == label && b.IsEffectivelyEnabled)
                : null;

        private Button? LabeledPopupButton(string label) =>
            AllPopupButtons().FirstOrDefault(b => Label(b) == label && b.IsEffectivelyVisible && b.IsEffectivelyEnabled);
        /// <summary>Local marks (K4): a comment on a line of a change in the conversation and one on lines in the editor,
        /// both through right-click menus and their comment boxes, a page mark, one resolved from its chip; the chips
        /// are one numbered list, and Send carries the open ones as one request while the resolved one stays.</summary>
        private async Task Marks()
        {
            Activate();
            await Send("WRITECALL please create marks-demo.py");
            if (await Until(() => vm.CurrentDialog is not null, 8)) await ApproveThroughCard("30-marks-write-approval");
            Check("marks-write-done", await Until(() => !vm.IsRunning && AssistantRowsContain("WRITECALL done"), 90), $"notices=[{Notices()}]");
            await Task.Delay(800);

            // In the conversation: open the finished turn and the write's card, right-click a line of its diff →
            // "Comment on this line…" → note → Enter
            var tool = vm.Rows.OfType<ToolRowViewModel>().LastOrDefault(t => t.HasDiff);
            Check("marks-diff-tool", tool is not null);
            if (tool is null) return;
            if (vm.Rows.OfType<TurnFoldRowViewModel>().LastOrDefault() is { IsExpanded: false } fold) fold.ToggleCommand.Execute(null);
            if (tool.Group is { IsExpanded: false } group) group.IsExpanded = true;
            tool.IsExpanded = true;
            await Until(() => window.GetVisualDescendants().OfType<DiffView>().Any(d => d.IsEffectivelyVisible), 5);
            await Task.Delay(600);
            var rows = window.GetVisualDescendants().OfType<DiffView>().LastOrDefault(d => d.IsEffectivelyVisible)?.GetVisualDescendants().OfType<Grid>()
                .Where(g => g.Classes.Contains("diff-row") && !g.Classes.Contains("gap")).ToList() ?? [];
            Check("marks-diff-rows", rows.Count >= 5, $"rows={rows.Count}");
            if (rows.Count < 5) return;
            rows[4].BringIntoView();
            await Task.Delay(600);
            RightClick(rows[4]);
            var commentLine = Localization.Loc.T("Comment on this line…");
            var menu = await Until(() => MenuRow(rows[4], commentLine) is not null, 5);
            await Task.Delay(300);
            ShotScreenRegion("31-marks-diff-menu-dark");
            Check("marks-diff-menu", menu);
            if (!menu) return;
            Click(MenuRow(rows[4], commentLine)!);
            // The comment box opens under the line with the keyboard in it
            await Task.Delay(700);
            Type("start at one");
            await Task.Delay(300);
            ShotScreenRegion("32-marks-diff-comment-box-dark");
            Key("\r", 0x24);
            var conv = await Until(() => vm.Marks.Items.Any(m => m.Code?.Origin == CodeMarkOrigin.Conversation), 5);
            var cm = vm.Marks.Items.FirstOrDefault(m => m.Code?.Origin == CodeMarkOrigin.Conversation);
            Check("marks-diff-mark", conv && cm!.Code!.Path == "marks-demo.py" && cm.Code.Line == 5 && cm.Note == "start at one"
                && cm.Code.Before is { Length: 2 } && cm.Code.After is { Length: 2 },
                $"mark={cm?.Code?.Path}:{cm?.Code?.Line} '{cm?.Note}' before={cm?.Code?.Before?.Length} after={cm?.Code?.After?.Length}");

            // In the editor: select lines, right-click → "Comment…" → note → Enter
            vm.Files.OpenLinkCommand.Execute(new FileLink("marks-demo.py", 6));
            var editorShown = await Until(() => window.GetVisualDescendants().OfType<DocumentEditor>().Any(e => e.IsEffectivelyVisible && e.WorkspaceDocument is not null), 15);
            Check("marks-editor-open", editorShown);
            if (!editorShown) return;
            await Task.Delay(800);
            var editor = window.GetVisualDescendants().OfType<DocumentEditor>().First(e => e.IsEffectivelyVisible && e.WorkspaceDocument is not null);
            var doc = editor.WorkspaceDocument!.Buffer;
            editor.Editor.Select(doc.GetLineByNumber(6).Offset, doc.GetLineByNumber(7).EndOffset - doc.GetLineByNumber(6).Offset);
            editor.Editor.TextArea.Focus();
            await Task.Delay(300);
            RightClick(editor.Editor.TextArea);
            var commentLines = Localization.Loc.T("Comment…");
            var editorMenu = await Until(() => MenuRow(editor.Editor, commentLines) is not null, 5);
            await Task.Delay(300);
            ShotScreenRegion("33-marks-editor-menu-dark");
            Check("marks-editor-menu", editorMenu);
            if (!editorMenu) return;
            Click(MenuRow(editor.Editor, commentLines)!);
            await Task.Delay(700);
            Type("use sum here");
            await Task.Delay(300);
            ShotScreenRegion("34-marks-editor-comment-box-dark");
            Key("\r", 0x24);
            var ed = await Until(() => vm.Marks.Items.Any(m => m.Code?.Origin == CodeMarkOrigin.Editor), 5);
            var em = vm.Marks.Items.FirstOrDefault(m => m.Code?.Origin == CodeMarkOrigin.Editor);
            Check("marks-editor-mark", ed && em!.Code!.Line == 6 && em.Code.EndLine == 7 && em.Code.Snapshot is { BufferUnsaved: false } && em.Note == "use sum here",
                $"mark={em?.Code?.Path}:{em?.Code?.Line}-{em?.Code?.EndLine} '{em?.Note}'");

            // A page mark (the page script's message, as annotate.js sends it) joins the same list
            vm.Preview.OnNavigationCompleted(new Uri("http://localhost:5173/"), true, false, false);
            var page = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["type"] = "annotation", ["token"] = vm.Preview.AnnotationToken, ["id"] = "smoke-pin", ["url"] = "http://localhost:5173/", ["title"] = "Shop",
                ["comment"] = "Make the button bigger",
                ["mark"] = new Dictionary<string, object> { ["kind"] = "point", ["x"] = 400, ["y"] = 300, ["pageX"] = 400, ["pageY"] = 300, ["viewportWidth"] = 1280, ["viewportHeight"] = 800 },
                ["element"] = new Dictionary<string, object> { ["selector"] = "#buy", ["tag"] = "button", ["html"] = "<button id=\"buy\">", ["text"] = "Buy", ["source"] = "", ["x"] = 380, ["y"] = 290, ["width"] = 80, ["height"] = 30 },
            });
            Check("marks-page-mark", vm.Preview.OnPageMessage(page) == "annotation");
            vm.AddCodeMark(new CodeAnchor("README.md", 1, false, "# work"), "already handled");
            vm.ActivePane = SidePane.None;
            await Task.Delay(600);
            Check("marks-one-list", vm.Marks.Items.Select(m => m.Number).SequenceEqual([1, 2, 3, 4])
                && window.FindControl<ItemsControl>("MarksList") is { IsEffectivelyVisible: true, ItemCount: 4 },
                string.Join(" | ", vm.Marks.Items.Select(m => $"{m.Number}:{m.Where}")));

            // Resolve the last from its chip's editor
            var last = vm.Marks.Items[3];
            var chipText = window.FindControl<ItemsControl>("MarksList")!.GetVisualDescendants().OfType<Button>()
                .Where(b => b.Classes.Contains("comment-chip-text")).ElementAtOrDefault(3);
            if (Check("marks-chip-text", chipText is not null)) Click(chipText!);
            var edit = await Until(() => last.IsEditing, 5);
            Check("marks-chip-editor", edit);
            await Task.Delay(400);
            ShotScreenRegion("35-marks-chip-editor-dark");
            var resolve = LabeledPopupButton(Localization.Loc.T("Resolve"));
            if (Check("marks-resolve-button", resolve is not null)) Click(resolve!);
            Check("marks-resolved", await Until(() => last.IsResolved, 5));
            last.IsEditing = false;
            await Task.Delay(500);
            Shot("36-marks-chips-dark");
            vm.SettingsTheme = "light";
            await Task.Delay(600);
            Shot("36-marks-chips-light");
            vm.SettingsTheme = "dark";

            // Send: one request, code first then the page, the resolved mark left out and kept
            FocusComposer();
            await Task.Delay(200);
            Key("\r", 0x24);
            var sent = await Until(() => UserRowsContain("start at one"), 20);
            var text = vm.Rows.OfType<UserRowViewModel>().LastOrDefault()?.Text ?? "";
            Check("marks-request", sent && text.StartsWith(LocalMarks.AllOnlyLine + "\n\n" + LocalMarks.CodeMarker, StringComparison.Ordinal)
                && text.Contains("1. marks-demo.py, line 5 (on a change in the conversation): start at one", StringComparison.Ordinal)
                && text.Contains("2. marks-demo.py, lines 6-7 (in the editor): use sum here", StringComparison.Ordinal)
                && text.Contains("\n3. Make the button bigger\n", StringComparison.Ordinal)
                && !text.Contains("already handled", StringComparison.Ordinal), text.Replace("\n", "⏎"));
            Check("marks-resolved-kept", vm.Marks.Items.Count == 1 && vm.Marks.Items[0].IsResolved);
            await Until(() => !vm.IsRunning, 60);
            await Task.Delay(500);
            Shot("37-marks-sent-dark");
            vm.Marks.Items.Clear();
        }
    }
}
#endif
