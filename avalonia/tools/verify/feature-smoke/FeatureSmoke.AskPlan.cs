#if OMBRA_FEATURE_SMOKE
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.App.Smoke;

internal static partial class FeatureSmoke
{
    private sealed partial class Run
    {
        private (string, Func<Task>)[] AskPlanSections() => [("ask-form", AskForm), ("todo-edit", TodoEdit)];

        /// <summary>
        /// The engine's ask tool after set_ask_dialog: both questions in one tabbed form (ASKCALL), a multi-select and
        /// the user's own words, sent at once; the model's next reply echoes what the tool returned.
        /// </summary>
        private async Task AskForm()
        {
            await Send("ASKCALL ask me two things");
            var shown = await Until(() => vm.CurrentDialog?.Ask is not null, 60);
            if (!Check("ask-form-shown", shown, $"dialog={vm.CurrentDialog?.Kind} notices=[{Notices()}]")) return;
            var dialog = vm.CurrentDialog!;
            var form = dialog.Ask!;
            Check("ask-form-tabs", form.HasTabs && form.Questions.Select(q => q.TabLabel).SequenceEqual(["Color", "Extras"]) && dialog.Title == Loc.F("omp has {0} questions", 2),
                $"tabs=[{string.Join(",", form.Questions.Select(q => q.TabLabel))}] title='{dialog.Title}'");
            Check("ask-form-multi", !form.Questions[0].Multi && form.Questions[1].Multi && form.Questions[0].Options[0].Recommended,
                $"multi=[{string.Join(",", form.Questions.Select(q => q.Multi))}]");
            await Task.Delay(500);
            Shot("20-ask-form-dark");

            // Submit with nothing chosen: the form stays and says what is missing
            if (ButtonFor(form.SubmitCommand) is { } early) Click(early);
            Check("ask-form-needs-answer", await Until(() => form.HasError, 5) && vm.CurrentDialog == dialog, $"error='{form.Error}'");
            await Task.Delay(300);
            Shot("20-ask-form-missing-dark");

            // One choice moves on to the multi-select; two extras and the user's own words
            var amber = form.Questions[0].Options.First(o => o.Label == "Amber");
            if (Check("ask-option-button", ButtonFor(amber.ChooseCommand) is not null)) Click(ButtonFor(amber.ChooseCommand)!);
            Check("ask-form-next-tab", await Until(() => form.Current == form.Questions[1], 5), $"current={form.Current.TabLabel}");
            await Task.Delay(300);
            foreach (var label in new[] { "Tests", "Docs" })
                if (ButtonFor(form.Questions[1].Options.First(o => o.Label == label).ChooseCommand) is { } b) { Click(b); await Task.Delay(250); }
            var own = window.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == "AskOwnAnswer" && t.IsEffectivelyVisible);
            if (Check("ask-own-answer-box", own is not null))
            {
                Click(own!);
                await Task.Delay(200);
                Type("lint");
            }
            Check("ask-form-chosen", await Until(() => form.Questions[1].CustomText == "lint", 5)
                && form.Questions[1].Options.Where(o => o.IsChecked).Select(o => o.Label).SequenceEqual(["Tests", "Docs"]),
                $"checked=[{string.Join(",", form.Questions[1].Options.Where(o => o.IsChecked).Select(o => o.Label))}] own='{form.Questions[1].CustomText}'");
            vm.SettingsTheme = "light";
            await Task.Delay(600);
            Shot("20-ask-form-filled-light");

            if (ButtonFor(form.SubmitCommand) is { } submit) Click(submit);
            var answered = await Until(() => vm.CurrentDialog is null && !vm.IsRunning && AssistantRowsContain("ASKCALL done"), 60);
            var reply = vm.Rows.OfType<AssistantRowViewModel>().LastOrDefault(r => r.Text.Contains("ASKCALL done", StringComparison.Ordinal))?.Text ?? "";
            Check("ask-form-answered", answered && new[] { "Amber", "Tests", "Docs", "lint" }.All(w => reply.Contains(w, StringComparison.Ordinal)), $"reply='{reply}' notices=[{Notices()}]");
            await Task.Delay(500);
            Shot("20-ask-form-answered-light");
        }

        /// <summary>
        /// The Plan pane edits omp's todo list (set_todos): the model writes a plan (TODOCALL), then the user checks a
        /// task off, reorders, renames, adds and deletes tasks; after each change omp's own list (get_state) says the same.
        /// </summary>
        private async Task TodoEdit()
        {
            await Prompt("TODOCALL make a plan");
            vm.ActivePane = SidePane.Plan;
            var planned = await Until(() => Tasks().Count == 3, 30);
            if (!Check("todo-plan-shown", planned, $"tasks=[{string.Join(" | ", Tasks().Select(t => t.Content))}] notices=[{Notices()}]")) return;
            await Task.Delay(600);
            Shot("21-plan-editable-dark");

            // The box: done
            if (TaskButton("plan-check", "Read README.md") is { } box) Click(box);
            Check("todo-check-off", await Until(() => Tasks().Any(t => t.Content == "Read README.md" && t.IsDone), 10)
                && await EngineHas(t => t.Any(x => x.Content == "Read README.md" && x.Status == "completed")), Engine());

            // ⋯ → Move up
            if (await Menu("Ship it", Loc.T("Move up"), "21-plan-task-menu-dark"))
                Check("todo-move-up", await Until(() => Tasks().Select(t => t.Content).SequenceEqual(["Read README.md", "Ship it", "Write the summary"]), 10)
                    && await EngineHas(t => t.Select(x => x.Content).SequenceEqual(["Read README.md", "Ship it", "Write the summary"])), Engine());

            // ⋯ → Edit text: typed in place, Enter keeps it
            if (await Menu("Write the summary", Loc.T("Edit text"), null))
            {
                var edited = await Until(() => FocusedIs("PlanTaskEditBox"), 5);
                Check("todo-edit-box", edited, $"focus={Focused()}");
                Type("Write the summary now"); // the box opens with the text selected: typing replaces it
                await Task.Delay(200);
                Shot("21-plan-editing-dark");
                Key("\r", 0x24);
                Check("todo-edit-text", await Until(() => Tasks().Any(t => t.Content == "Write the summary now"), 10)
                    && await EngineHas(t => t.Any(x => x.Content == "Write the summary now")), Engine());
            }

            // Add a task
            var add = window.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == "PlanNewTaskBox" && t.IsEffectivelyVisible);
            if (Check("todo-add-box", add is not null))
            {
                Click(add!);
                await Task.Delay(200);
                Type("check the banner");
                Key("\r", 0x24);
                Check("todo-add", await Until(() => Tasks().Count == 4 && Tasks()[^1].Content == "check the banner", 10)
                    && await EngineHas(t => t.Count == 4 && t[^1] is { Content: "check the banner", Status: "pending" }), Engine());
            }
            vm.SettingsTheme = "light";
            await Task.Delay(600);
            Shot("21-plan-edited-light");

            // ⋯ → Delete
            if (await Menu("check the banner", Loc.T("Delete task"), null))
                Check("todo-delete", await Until(() => Tasks().Count == 3 && Tasks().All(t => t.Content != "check the banner"), 10)
                    && await EngineHas(t => t.Count == 3), Engine());
            Check("todo-history", vm.Plan.Changes.Any(c => c.What == Loc.T("Completed") && c.Content == "Read README.md"),
                string.Join(" | ", vm.Plan.Changes.Select(c => $"{c.What} {c.Content}")));
            await Task.Delay(500);
            Shot("21-plan-after-delete-light");
        }

        private List<PlanTaskViewModel> Tasks() => [.. vm.Plan.Phases.SelectMany(p => p.Tasks)];

        private Button? TaskButton(string cls, string content) =>
            window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains(cls) && b.DataContext is PlanTaskViewModel t && t.Content == content && b.IsEffectivelyVisible);

        /// <summary>Opens the task's ⋯ menu and clicks <paramref name="item"/>; a screenshot of the open menu when named.</summary>
        private async Task<bool> Menu(string task, string item, string? shot)
        {
            if (TaskButton("plan-more", task) is not { } more) return Check($"todo-menu-{task}", false);
            more.BringIntoView();
            Activate(); // the region screenshot shows the screen: other windows must not cover this one
            await Task.Delay(300);
            Click(more);
            if (!await Until(() => more.Flyout?.IsOpen == true, 5)) return Check($"todo-menu-open-{task}", false);
            await Task.Delay(300);
            if (shot is not null) ShotScreenRegion(shot);
            var items = (more.Flyout as Flyout)?.Content is Control menu ? menu.GetLogicalDescendants().OfType<Button>().ToList() : [];
            var button = items.FirstOrDefault(b => Avalonia.Automation.AutomationProperties.GetName(b) == item);
            if (!Check($"todo-menu-item-{item}", button is not null, string.Join(" | ", items.Select(Label)))) return false;
            Click(button!);
            await Task.Delay(300);
            return true;
        }

        private List<TodoTask> _engine = [];

        /// <summary>omp's own list (get_state), polled until it matches: the change reached the engine.</summary>
        private async Task<bool> EngineHas(Func<List<TodoTask>, bool> match)
        {
            var end = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                if (vm.Session.Connection is { } conn)
                    _engine = [.. ConversationState.ParseTodos((await conn.GetStateAsync()).TodoPhases).SelectMany(p => p.Tasks)];
                if (match(_engine)) return true;
                if (DateTime.UtcNow > end) return false;
                await Task.Delay(200);
            }
        }

        private string Engine() => "engine=[" + string.Join(" | ", _engine.Select(t => $"{t.Content}:{t.Status}")) + "] pane=[" + string.Join(" | ", Tasks().Select(t => $"{t.Content}:{t.Status}")) + "]";
    }
}
#endif
