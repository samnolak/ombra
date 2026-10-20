using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

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
            var card = new SessionCardViewModel("explain", "IconTerminal", Loc.T("!! runs in omp's terminal"),
                Loc.T("omp keeps a !!command out of the model's context only in its terminal UI. Here, !command runs it and the output joins the context; for a plain shell, use the terminal panel."));
            card.Primary = new SessionCardAction(Loc.T("Open a shell"), new RelayCommand(() => { CloseSessionCard(); NewShellCommand.Execute(null); }));
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
        var args = t.Length > name.Length + 1 ? t[(name.Length + 2)..].Trim() : "";
        // omp's /mcp runs over RPC; a sign-in typed here opens its page in the browser (MainViewModel.Mcp.cs)
        if (name == "mcp") NoteTypedMcpCommand(args);
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
                ShowCard(TerminalCard(cmd, Loc.T("Plugins, MCP servers and skills are in Settings → Plugins. omp's own dashboard is in its terminal."),
                    new SessionCardAction(Loc.T("Open Plugins"), new RelayCommand(() => { CloseSessionCard(); _ = OpenPluginsPageCommand.ExecuteAsync(null); }))));
                return true;
            case GuiEquivalent.NewSession:
                _ = NewSessionHereAsync(); // MainViewModel.Chats.cs
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
                else ShowCard(ExplainCard(cmd, Loc.T("Stop the current run first; then Delete session in the session menu (the title) deletes it after asking.")));
                return true;
            case GuiEquivalent.Rewind:
                if (RewindCommand.CanExecute(null)) _ = RewindCommand.ExecuteAsync(null);
                else ShowCard(ExplainCard(cmd, Loc.T("Stop the current run first; then Rewind… in the session menu (the title) picks the message to start over from.")));
                return true;
            case GuiEquivalent.Fork:
                if (ForkSessionCommand.CanExecute(null)) _ = ForkSessionCommand.ExecuteAsync(null);
                else ShowCard(ExplainCard(cmd, Loc.T("Stop the current run first; then Fork in the session menu (the title) forks the session, or Fork from here on a reply forks at that reply.")));
                return true;
            case GuiEquivalent.BackgroundTasks:
                ShowPaneCommand.Execute(SidePane.Tasks);
                return true;
            case GuiEquivalent.Shortcuts:
                IsShortcutsOpen = true;
                return true;
            case GuiEquivalent.PlanMode when IsPlanModeAvailable:
                if (cmd.Name == "plan-review")
                {
                    ShowCard(ExplainCard(cmd, HasPlanReview
                        ? Loc.T("The proposed plan is above the message box: approve it there, or reject it and say what to change.")
                        : Loc.T("No plan waits for review. In plan mode omp shows the plan it proposes above the message box.")));
                    return true;
                }
                // As omp's /plan: text after it is the first planning message (left in the box to send)
                _ = SetPlanModeAsync(!IsPlanMode);
                if (args.Length > 0) ComposerText = args;
                return true;
            case GuiEquivalent.Goal when IsGoalAvailable:
                // As omp's /goal: text after it is the objective; bare, it opens the goal bar's editor
                OpenGoalFromCommand(args);
                return true;
            case GuiEquivalent.Btw when IsBtwAvailable:
                // As omp's /btw: text after it is the question; bare, it opens the panel with the history
                OpenBtwFromCommand(args);
                return true;
            case GuiEquivalent.Explain:
                ShowCard(ExplainCard(cmd, cmd.Explanation));
                return true;
            default:
                ShowCard(TerminalCard(cmd, Loc.F("{0}. It has no place in this window yet: omp runs it in its own terminal UI, which opens beside the conversation as a separate omp with its own session.", cmd.Description)));
                return true;
        }
    }

    /// <summary>A !command: a row in the conversation (its output when it ends) and a card with Stop meanwhile.</summary>
    private async Task RunShellAsync(string command)
    {
        var open = _open;
        var card = new SessionCardViewModel("shell", "IconTerminal", Loc.T("Running a shell command"),
            Loc.T("omp runs it in the project; its output goes into the conversation and the context.")) { Detail = command, State = SessionCardState.Running };
        card.Primary = new SessionCardAction(Loc.T("Stop"), new AsyncRelayCommand(() => open.Controller.StopShellCommandAsync(_cts.Token)));
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
            ShowCard(ExplainCard(cmd, Loc.T("omp restarts once the current reply ends: stop it first, or type /restart again then.")));
            return;
        }
        await open.Controller.RecoverAsync(_cts.Token);
        ApplyIfShown(open);
    }

    private SessionCardViewModel TerminalCard(TerminalOnlyCommand cmd, string message, SessionCardAction? gui = null)
    {
        var card = new SessionCardViewModel("terminal", "IconTerminal", Loc.F("/{0} runs in omp's terminal", cmd.Name), message);
        var open = new SessionCardAction(Loc.T("Open omp terminal"), new RelayCommand(() => { CloseSessionCard(); OpenOmpTuiCommand.Execute(null); }));
        if (gui is null) card.Primary = open;
        else
        {
            card.Primary = gui;
            card.Secondary = open;
        }
        return card;
    }

    private static SessionCardViewModel ExplainCard(TerminalOnlyCommand cmd, string message) =>
        new("explain", "IconSparkle", Loc.F("/{0} in this window", cmd.Name), message);

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
            .Select(c => new SlashCommand(c.Name, c.Description, null,
                c.Gui == GuiEquivalent.Terminal || (c.Gui == GuiEquivalent.PlanMode && !IsPlanModeAvailable)
                || (c.Gui == GuiEquivalent.Goal && !IsGoalAvailable) || (c.Gui == GuiEquivalent.Btw && !IsBtwAvailable) ? TerminalOnlySource : "builtin", [], [])));
    }
}
