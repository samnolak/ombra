using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OmpGui.App;
using OmpGui.App.ViewModels;
using OmpGui.App.Views;
using OmpGui.ClientCore;

namespace OmpGui.Tests;

/// <summary>Screen readers get a name for every visible button and text field, in the window's main states.</summary>
public sealed class AccessibilityUiTests
{
    private static async Task Until(Func<bool> condition, string what, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("UI never reached: " + what);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>What an automation client reads for the control (explicit name, else what the peer derives from content).</summary>
    private static string NameOf(Control c)
    {
        var explicitName = AutomationProperties.GetName(c);
        if (!string.IsNullOrWhiteSpace(explicitName)) return explicitName;
        var peer = ControlAutomationPeer.CreatePeerForElement(c);
        return peer.GetName() ?? "";
    }

    private static List<string> Unnamed(Window w)
    {
        Dispatcher.UIThread.RunJobs();
        return w.GetVisualDescendants().OfType<Control>()
            .Where(c => c is Button or TextBox && c.IsEffectivelyVisible && c.Bounds.Width > 0)
            .Where(c => !NameOf(c).Any(char.IsLetterOrDigit))
            .Select(c => $"{c.GetType().Name} {c.Name} ({(c as ContentControl)?.Content})")
            .ToList();
    }

    [AvaloniaFact]
    public async Task Every_visible_button_and_field_has_a_name_in_the_main_states()
    {
        var s = new SessionController(TestProcesses.FakeFactory("approval", TestProcesses.TempDir("a11y-sessions")), new LaunchRequest(TestProcesses.TempDir("a11y-project"), ApprovalMode: "write"));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1200, Height = 800 };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready, "ready");
        Assert.Empty(Unnamed(w));                                       // idle, sidebar open

        vm.ComposerText = "/";
        await Until(() => vm.IsCommandMenuOpen, "command menu");
        Assert.Empty(Unnamed(w));                                       // slash-command menu
        vm.ComposerText = "";

        vm.ComposerText = "run it";
        vm.SendCommand.Execute(null);
        await Until(() => vm.HasDialog, "approval card");
        Assert.Empty(Unnamed(w));                                       // approval card
        vm.CurrentDialog!.DenyCommand.Execute(null);
        await Until(() => vm.Phase == SessionPhase.Ready, "run end");

        vm.ToggleTerminalCommand.Execute(null);
        await Until(() => vm.Terminals.Count == 1, "terminal");
        Assert.Empty(Unnamed(w));                                       // terminal panel
        vm.CloseTerminalCommand.Execute(vm.Terminals[0]);

        vm.OpenSettingsCommand.Execute(null);
        Assert.Empty(Unnamed(w));                                       // settings page
        await vm.DisposeAsync();
        w.Close();
    }
}
