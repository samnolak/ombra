using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Ombra.App.Controls;
using Ombra.App.Platform.Mac;
using static Ombra.App.Platform.Mac.MacRuntime;

namespace Ombra.App.Nightly;

internal static partial class NightlyAcceptance
{
    private sealed partial class Run
    {
        /// <summary>
        /// Annotations on the agent's pane in the right panel (driver command <c>annotate URL COMMENT</c>), on the engine
        /// it uses (the Nightly pane, or the built-in preview). The fixture's contract: a button <c>#buy</c> at (40, 40)
        /// 200×80 CSS px whose clicks <c>window.c</c> counts. The toolbar's Annotate pressed, a click on the button opens
        /// the comment box (the page never sees that click), the comment typed (keys; an input method's commit where it is
        /// not ASCII) and Enter: the chat's marks have it with its element, the composer shows its chip, its pin is drawn
        /// where the click was. Esc in the page leaves the mode (the page's clicks work again); the pin clicked then opens
        /// the comment again, and the edit reaches the mark.
        /// </summary>
        private async Task AnnotationChecks(string page, string comment)
        {
            var engine = vm.Preview.UseNightly ? "nightly" : "preview";
            vm.IsPreviewOpen = true;
            vm.Preview.NavigateCommand.Execute(page);
            var loaded = await Until(() => vm.Preview.CurrentUrl?.AbsoluteUri == page && !vm.Preview.IsLoading && PagePoint(0, 0) is not null
                && (!vm.Preview.UseNightly || AgentPane() is { FramesShown: > 0 }), 60);
            Check($"annotate-{engine}-loaded", loaded, $"url={vm.Preview.CurrentUrl} loading={vm.Preview.IsLoading}");
            if (!loaded) return;
            await Task.Delay(1000); // the page's script has started
            await KeyWindow();
            var before = vm.Marks.Items.Count(m => m.IsPage);
            Press(Named<Button>("AnnotateButton", AgentPanel()), "Annotate");
            Check($"annotate-{engine}-mode", await Until(() => vm.Preview.IsAnnotating, 5), $"annotating={vm.Preview.IsAnnotating}");
            // The pointer leaves the button for the page (mouseMoved: to the view under it, as a hardware move): its
            // tooltip closes (the built-in web view, a native view, is covered by a picture of itself while an Avalonia
            // popup is open)
            if (PagePoint(300, 300) is { } away) Hover(ClientToWindow(away));
            await Until(() => vm.Preview.UseNightly || AgentPanel()?.WebView is { IsEffectivelyVisible: true }, 5);
            await Task.Delay(400);

            await KeyWindow();
            PageClick(100, 70);
            await Task.Delay(800);
            Shot($"annotate-{engine}-box");
            TypeText(comment);
            await Task.Delay(300);
            KeyPress("\r", 0x24, 0);
            var added = await Until(() => vm.Marks.Items.Count(m => m.IsPage) == before + 1, 5);
            var mark = vm.Marks.Items.LastOrDefault(m => m.IsPage);
            var clicks = await PageClicks();
            Check($"annotate-{engine}-mark", added && mark?.Note == comment && mark.Page?.Element.Selector == "#buy" && clicks == 0,
                $"marks {before}→{vm.Marks.Items.Count(m => m.IsPage)} note='{mark?.Note}' selector='{mark?.Page?.Element.Selector}' page clicks={clicks}");
            Check($"annotate-{engine}-chip", Named<ItemsControl>("MarksList") is { ItemCount: > 0 } chips && chips.ItemCount == vm.Marks.Items.Count,
                $"chips={Named<ItemsControl>("MarksList")?.ItemCount} marks={vm.Marks.Items.Count}");
            await Task.Delay(600);
            var pinned = Shot($"annotate-{engine}-pin");
            Check($"annotate-{engine}-pin", PinDrawn(pinned, 100, 70, out var pinDetail), pinDetail);

            await KeyWindow();
            KeyPress("\u001b", 0x35, 0);
            Check($"annotate-{engine}-esc", await Until(() => !vm.Preview.IsAnnotating, 5), $"annotating={vm.Preview.IsAnnotating}");
            await Task.Delay(300);
            await KeyWindow();
            PageClick(200, 100);
            var clicked = await Until(async () => await PageClicks() == 1, 5);
            Check($"annotate-{engine}-page-clicks", clicked, $"page clicks={await PageClicks()}");

            if (mark is null) return;
            await KeyWindow();
            PageClick(100, 70); // the pin
            await Task.Delay(800);
            Shot($"annotate-{engine}-edit");
            TypeText(" ok");
            await Task.Delay(300);
            KeyPress("\r", 0x24, 0);
            var edited = await Until(() => mark.Note == comment + " ok", 5);
            Check($"annotate-{engine}-edit", edited && await PageClicks() == 1, $"note='{mark.Note}' page clicks={await PageClicks()}");
        }

        /// <summary>The window is the key window again: posted input goes to the key window, and other apps (another
        /// GUI check, a browser test) may have taken the focus since.</summary>
        private async Task KeyWindow()
        {
            if (window.IsActive) return;
            Activate();
            if (!await Until(() => window.IsActive, 3)) Log("window not active: another app has the focus");
        }

        /// <summary>The right panel's browser (not the Browser page's).</summary>
        private PreviewPanel? AgentPanel() =>
            window.GetVisualDescendants().OfType<PreviewPanel>().FirstOrDefault(p => p.Name == "PreviewPanel" && p.IsEffectivelyVisible);

        private NightlyPane? AgentPane() => Panes().FirstOrDefault(p => !p.IsBrowserWindow && p.IsEffectivelyVisible);

        /// <summary>A point of the agent's page (CSS px) in the window's client area: on the Nightly pane's frame as shown,
        /// or on the built-in web view (also while a picture of it covers it).</summary>
        private Point? PagePoint(double x, double y)
        {
            if (!vm.Preview.UseNightly) return AgentPanel()?.WebView is { } web ? web.TranslatePoint(new Point(x, y), window) : null;
            if (AgentPane() is not { } pane) return null;
            var (shown, _, _) = pane.FrameLayout;
            var k = PageScale(pane);
            return pane.TranslatePoint(new Point(shown.X + x * k, shown.Y + y * k), window);
        }

        private void PageClick(double x, double y)
        {
            if (PagePoint(x, y) is not { } client)
            {
                Log($"page click ({x},{y}): no page");
                return;
            }
            var at = ClientToWindow(client);
            Post(MouseEvent(5, at, 0));
            Post(MouseEvent(1, at, 1));
            Post(MouseEvent(2, at, 1));
            Log($"page click ({x},{y}) window ({at.X:0.#},{at.Y:0.#})");
        }

        /// <summary>Text as the keyboard gives it: ASCII as keys, anything else as an input method's commit.</summary>
        private void TypeText(string text)
        {
            if (text.All(char.IsAscii))
            {
                foreach (var ch in text) KeyPress(ch.ToString(), CharKeyCode(ch), 0);
                return;
            }
            if (TextInputClient() is var client and not 0) InsertText(client, text);
            else Log($"type '{text}': no text input client");
        }

        /// <summary>The fixture's count of the clicks its button got (-1: no answer).</summary>
        private async Task<int> PageClicks()
        {
            try
            {
                return vm.Preview.ActiveTab.RunScript is { } run && await run("String(window.c)") is { } text
                    && int.TryParse(text.Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;
            }
            catch (Exception e)
            {
                Log($"page clicks: {e.Message}");
                return -1;
            }
        }

        /// <summary>The pin's yellow (#ffc21a) around a page point in a window shot (left, right, above and below its number).</summary>
        private unsafe bool PinDrawn(string file, double x, double y, out string detail)
        {
            if (PagePoint(x, y) is not { } client || NightlyCocoa.ScreenBottomLeft(NsWindow, client.X, client.Y, 0, 0) is not { } onScreen)
            {
                detail = "no page";
                return false;
            }
            var s = window.RenderScaling;
            var frame = ((delegate* unmanaged<nint, nint, CGRect>)MacObjC.MsgSend)(NsWindow, MacObjC.Sel("frame"));
            double cx = (onScreen.X - frame.X) * s, cy = (frame.Y + frame.Height - onScreen.Y) * s;
            using var bitmap = new Bitmap(file);
            var size = bitmap.PixelSize;
            var stride = size.Width * 4;
            var pixels = new byte[stride * size.Height];
            fixed (byte* buffer = pixels) bitmap.CopyPixels(new PixelRect(size), (nint)buffer, pixels.Length, stride);
            var red = bitmap.Format == PixelFormats.Rgba8888 ? 0 : 2;
            var yellow = 0;
            var seen = new List<string>();
            foreach (var (dx, dy) in new[] { (-6, 0), (6, 0), (0, -6), (0, 6) })
            {
                int px = (int)Math.Round(cx + dx * s), py = (int)Math.Round(cy + dy * s);
                if (px < 0 || py < 0 || px >= size.Width || py >= size.Height) continue;
                var o = py * stride + px * 4;
                int r = pixels[o + red], g = pixels[o + 1], b = pixels[o + 2 - red];
                seen.Add($"{r},{g},{b}");
                if (r > 200 && g is > 140 and < 225 && b < 110) yellow++;
            }
            detail = $"{yellow}/4 yellow around ({cx:0},{cy:0}) px in {Path.GetFileName(file)}: {string.Join(" ", seen)}";
            return yellow >= 3;
        }
    }
}
