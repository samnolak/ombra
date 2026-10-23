#if OMBRA_FEATURE_SMOKE
using Avalonia.Controls;
using Avalonia.VisualTree;
using Ombra.App.Controls;

namespace Ombra.App.Smoke;

internal static partial class FeatureSmoke
{
    private sealed partial class Run
    {
        private (string, Func<Task>)[] MessageSelectionSections() => [("message-selection", SelectMessageText)];

        /// <summary>
        /// A reply's selection through its blocks (Controls/MessageSelection) with the Mac's own keys: ⌘A with the keyboard
        /// in the reply selects all of it, ⌘C copies it as it reads (taken from the app's clipboard writer, not the Mac's
        /// clipboard, the owner's), a click elsewhere clears it. A drag is not driven here: Avalonia.Native reads the
        /// button of a mouseDragged from the hardware (<c>[NSEvent pressedMouseButtons]</c>), which posted events do not
        /// press; the drags are MessageTextSelectionTests' (headless).
        /// </summary>
        private async Task SelectMessageText()
        {
            await Prompt("MDREPLY please");
            await Task.Delay(1500); // the reply's footer and the transcript's scroll settle
            Activate();
            await Until(() => window.IsActive, 5);
            var view = window.FindControl<ListBox>("Transcript")!.GetVisualDescendants().OfType<MarkdownView>()
                .LastOrDefault(v => v.IsEffectivelyVisible && v.GetVisualDescendants().OfType<SelectableTextBlock>().Any(b => Words(b) == "Last paragraph."));
            var second = view?.GetVisualDescendants().OfType<SelectableTextBlock>().FirstOrDefault(b => Words(b).StartsWith("Second paragraph", StringComparison.Ordinal));
            Check("selection-reply-shown", second is not null);
            if (view is null || second is null) return;
            SelectableTextBlock[] Selected() => view.GetVisualDescendants().OfType<SelectableTextBlock>().Where(b => b.SelectionStart != b.SelectionEnd).ToArray();

            Click(second, 0.1, 0.5);
            await Until(() => second.IsFocused, 3);
            Key("a", 0x00, Cmd);
            Check("selection-reply-cmd-a", await Until(() => Selected().Length == 6, 3), $"blocks selected={Selected().Length} focus={Focused()}");
            var copied = await CopiedWithCommandC();
            const string whole = "First paragraph of the reply, long enough to wrap onto a second line in the conversation column, so a drag goes down "
                + "through it.\n\nSecond paragraph with inline code and more words.\n\nitem one\nitem two\n\ndef f():\n    return 1\n\nLast paragraph.";
            Check("selection-reply-cmd-c", copied == whole, $"[{copied}]");
            Shot("60-selection-reply-all");

            Click(window.FindControl<TextBox>("Composer")!);
            Check("selection-cleared-by-a-click-elsewhere", await Until(() => Selected().Length == 0, 3), $"blocks selected={Selected().Length}");
        }

        private static string Words(SelectableTextBlock b) => MessageSelection.Readable(b, 0, int.MaxValue);

        /// <summary>⌘C as posted keys; what it would have put on the clipboard (the clipboard itself is left alone).</summary>
        private async Task<string> CopiedWithCommandC()
        {
            var copied = new List<string>();
            var write = ContextMenus.WriteClipboard;
            ContextMenus.WriteClipboard = (_, text) =>
            {
                copied.Add(text);
                return Task.CompletedTask;
            };
            try
            {
                Key("c", 0x08, Cmd);
                await Until(() => copied.Count > 0, 5);
            }
            finally { ContextMenus.WriteClipboard = write; }
            return copied.LastOrDefault()?.ReplaceLineEndings("\n") ?? "";
        }
    }
}
#endif
