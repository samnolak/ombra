using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OmpGui.App.Controls;
using Xunit;

namespace OmpGui.Tests;

/// <summary>Markdown views drawn in states the transcript puts them in (hidden rows, finished streams).</summary>
public sealed class MarkdownViewRenderTests
{
    private static void Frame(Window w)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        using (w.CaptureRenderedFrame()) { }
    }

    /// <summary>A streamed reply with links that ends as its row is hidden (its chat left) is drawn without Avalonia
    /// throwing "Visual was invalidated during the render pass" (the links were templated in the render pass).</summary>
    [AvaloniaFact]
    public void A_reply_with_links_finished_while_hidden_draws_without_throwing()
    {
        var view = new MarkdownView { IsStreaming = true, Markdown = "See [a](https://a.example/) and" };
        var host = new Border { Child = view };
        var w = new Window { Content = new Panel { Children = { host } }, Width = 600, Height = 400 };
        w.Show();
        Frame(w);
        Frame(w);
        view.Markdown += " [b](https://b.example/) too";
        view.IsStreaming = false;
        host.IsVisible = false;
        Frame(w);
        Frame(w);
        w.Close();
    }
}
