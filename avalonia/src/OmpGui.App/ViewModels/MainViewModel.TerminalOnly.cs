using CommunityToolkit.Mvvm.Input;
using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

/// <summary>
/// Commands typed in the message box that omp runs only in its terminal UI (/settings, /plan, /login, /new…). Over
/// RPC omp has no handler for them and would pass them to the model as a message, so they never leave the GUI: the
/// GUI's own equivalent runs where there is one, else a card offers omp's terminal. Skills, prompts and extension
/// commands omp lists itself (even under such a name) go to omp as before.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// Handles <paramref name="text"/> in the GUI when it is a terminal-only command (or a bare <c>/model</c>, which
    /// opens the model picker). True when it was handled and must not be sent.
    /// </summary>
    internal bool HandleTerminalOnlyCommand(string text)
    {
        var t = text.Trim();
        // omp's shell prefix: !command runs in the project (RPC bash), its output joins the context
        if (t.StartsWith("!!", StringComparison.Ordinal) && t.Length > 2)
        {
            ComposerText = "";
            var card = new SessionCardViewModel("explain", "IconTerminal", "!! runs in omp's terminal",
                "omp keeps a !!command out of the model's context only in its terminal UI. Here, !command runs it and the output " +
                "joins the context; for a plain shell, use the terminal panel.");
            card.Primary = new SessionCardAction("Open a shell", new RelayCommand(() => { CloseSessionCard(); NewShellCommand.Execute(null); }));
            ShowCard(card);
            return true;
        }
        if (t.StartsWith('!') && t[1..].Trim() is { Length: > 0 } shell)
        {
            ComposerText = "";
            _ = RunShellAsync(shell);
            return true;
        }
        if (OmpBuiltins.NameOf(t) is not { Length: > 0 } name) return false;
        // omp advertised the name itself (a skill, prompt, extension or file command): it runs there
        if (_commands.Any(c => c.Source != "builtin" && (c.Name == name || c.Aliases.Contains(name)))) return false;
        var args = t.Length > name.Length + 1 ? t[(name.Length + 1)..].Trim() : "";
        if (name is "model" or "models" && args.Length == 0)
        {
            ComposerText = "";
            _ = OpenModelMenuCommand.ExecuteAsync(null);
            return true;
        }
        if (TerminalOnlyCommands.Find(name) is not { } cmd) return false;
        ComposerText = "";
        switch (cmd.Gui)
        {
            case GuiEquivalent.Settings:
                _ = OpenSettingsAtAsync("general");
                return true;
            case GuiEquivalent.Providers:
                _ = OpenSettingsAtAsync("providers");
                return true;
            case GuiEquivalent.Plugins:
                ShowCard(TerminalCard(cmd, "Plugins, skills and extensions are in Settings → Plugins and skills. omp's own dashboard is in its terminal.",
                    new SessionCardAction("Open Plugins and skills", new RelayCommand(() => { CloseSessionCard(); _ = OpenSettingsAtAsync("plugins"); }))));
                return true;
            case GuiEquivalent.NewSession:
                _ = NewSessionCommand.ExecuteAsync(null);
                return true;
            case GuiEquivalent.Sessions:
                // The sidebar is the GUI's session list; text after /resume filters it
                if (IsNarrow) SidebarShownWhileNarrow = true;
                else IsSidebarVisible = true;
                SessionFilter = args;
                return true;
            case GuiEquivalent.RestartOmp:
                _ = RestartFromCommandAsync(cmd);
                return true;
            case GuiEquivalent.DeleteSession:
                if (DeleteSessionCommand.CanExecute(null)) DeleteSessionCommand.Execute(null);
                else ShowCard(ExplainCard(cmd, "Stop the current run first; then Delete session in the session menu (the title) deletes it after asking."));
                return true;
            case GuiEquivalent.Rewind:
                if (RewindCommand.CanExecute(null)) _ = RewindCommand.ExecuteAsync(null);
                else ShowCard(ExplainCard(cmd, "Stop the current run first; then Rewind… in the session menu (the title) picks the message to start over from."));
                return true;
            case GuiEquivalent.BackgroundTasks:
                ShowPaneCommand.Execute(SidePane.Tasks);
                return true;
            case GuiEquivalent.Shortcuts:
                IsShortcutsOpen = true;
                return true;
            case GuiEquivalent.Explain:
                ShowCard(ExplainCard(cmd, cmd.Explanation));
                return true;
            default:
                ShowCard(TerminalCard(cmd, $"{cmd.Description}. It has no place in this window yet: omp runs it in its own terminal UI, " +
                    "which opens beside the conversation as a separate omp with its own session."));
                return true;
        }
    }

    /// <summary>A !command: a row in the conversation (its output when it ends) and a card with Stop meanwhile.</summary>
    private async Task RunShellAsync(string command)
    {
        var open = _open;
        var card = new SessionCardViewModel("shell", "IconTerminal", "Running a shell command",
            "omp runs it in the project; its output goes into the conversation and the context.") { Detail = command, State = SessionCardState.Running };
        card.Primary = new SessionCardAction("Stop", new AsyncRelayCommand(() => open.Controller.StopShellCommandAsync(_cts.Token)));
        ShowCard(card);
        await open.Controller.RunShellCommandAsync(command, _cts.Token);
        ApplyIfShown(open);
        // The row in the conversation has the output: the card goes, also when it waits with its chat in the background
        if (ReferenceEquals(SessionCard, card)) CloseSessionCard();
        else if (ReferenceEquals(open.Card, card)) open.Card = null;
    }

    /// <summary>/restart: this chat's omp starts again on its session (a run is never cut short).</summary>
    private async Task RestartFromCommandAsync(TerminalOnlyCommand cmd)
    {
        var open = _open;
        if (IsRunning)
        {
            ShowCard(ExplainCard(cmd, "omp restarts once the current reply ends: stop it first, or type /restart again then."));
            return;
        }
        await open.Controller.RecoverAsync(_cts.Token);
        ApplyIfShown(open);
    }

    private SessionCardViewModel TerminalCard(TerminalOnlyCommand cmd, string message, SessionCardAction? gui = null)
    {
        var card = new SessionCardViewModel("terminal", "IconTerminal", $"/{cmd.Name} runs in omp's terminal", message);
        var open = new SessionCardAction("Open omp terminal", new RelayCommand(() => { CloseSessionCard(); OpenOmpTuiCommand.Execute(null); }));
        if (gui is null) card.Primary = open;
        else
        {
            card.Primary = gui;
            card.Secondary = open;
        }
        return card;
    }

    private static SessionCardViewModel ExplainCard(TerminalOnlyCommand cmd, string message) =>
        new("explain", "IconSparkle", $"/{cmd.Name} in this window", message);

    /// <summary>The source the slash menu gives commands that run only in omp's terminal UI.</summary>
    private const string TerminalOnlySource = "tui";

    /// <summary>
    /// omp's catalog for the slash menu, plus its terminal-only builtins: the ones this window answers itself with its
    /// own commands, the ones only omp's terminal runs marked <see cref="TerminalOnlySource"/> (listed last). Those the
    /// window does another way (quit, copy, open a link, queue) are left out; typed, they still say how.
    /// </summary>
    private IEnumerable<SlashCommand> SlashMenuCommands()
    {
        var known = _commands.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        return _commands.Concat(TerminalOnlyCommands.All
            .Where(c => !known.Contains(c.Name) && c.Gui != GuiEquivalent.Explain
                        && c.Name is not ("q" or "status" or "rewind" or "providers")) // aliases: one entry each
            .Select(c => new SlashCommand(c.Name, c.Description, null, c.Gui == GuiEquivalent.Terminal ? TerminalOnlySource : "builtin", [], [])));
    }
}
