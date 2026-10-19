using System.Text.RegularExpressions;
using Ombra.App.Controls;

namespace Ombra.App.Nightly;

internal static partial class NightlyAcceptance
{
    private sealed partial class Run
    {
        /// <summary>
        /// A composition that Nightly ends on its own while the input method still composes in Ombra (a click elsewhere
        /// in the page or the page's script takes the focus from the field: Nightly commits it), or may end (Esc to the
        /// page, the app losing the focus, no editor having the keyboard), then the input method's own end of it (an empty
        /// <c>setMarkedText:</c>). With no composition left in Nightly that end is nothing (it crashed Nightly in
        /// <c>IMEInputHandler::DispatchCompositionCommitEvent</c>, MOZ_RELEASE_ASSERT(isSome())): the same runtime, the
        /// field's text as the page left it, and the field composes again. <paramref name="rounds"/> times each way, one
        /// check per way; the K1 fixture in <paramref name="pane"/> (the agent's pane).
        /// </summary>
        private async Task CompositionEndedByNightly(NightlyPane pane, int rounds)
        {
            var pid = pane.RuntimePid;
            (string Name, bool InField, bool NightlyEnds, Func<Task> End)[] ways =
            [
                ("page-click", true, true, () =>
                {
                    Click(pane, 150, 400); // the page's text under the field
                    return Task.CompletedTask;
                }),
                ("page-script", true, true, async () => await pane.Agent.RunScriptAsync("document.getElementById('ime').blur(), ''")),
                ("esc", true, false, () =>
                {
                    Key("\u001b", 0x35);
                    return Task.CompletedTask;
                }),
                ("app-focus-loss", true, false, LoseAppFocus),
                ("no-editor", false, false, () => Task.CompletedTask),
            ];
            foreach (var (name, inField, nightlyEnds, end) in ways)
            {
                var failures = new List<string>();
                var ran = 0;
                for (; ran < rounds && pane.RuntimePid == pid; ran++)
                {
                    if (await CompositionEndedRound(pane, pid, inField, nightlyEnds, end) is { } failure)
                    {
                        failures.Add($"#{ran}: {failure}");
                        Log($"ime-ended {name} #{ran} FAILED {failure}");
                    }
                }
                Check($"ime-ended-{name}", ran == rounds && failures.Count == 0 && pane.RuntimePid == pid && NightlyMach.IsAlive(pid),
                    $"{ran - failures.Count}/{rounds} rounds, runtime pid {pid}→{pane.RuntimePid}"
                    + (failures.Count > 0 ? ": " + string.Join("; ", failures.Take(3)) : ""));
            }
        }

        /// <summary>One composition ended by <paramref name="end"/> (by Nightly for sure when <paramref name="nightlyEnds"/>),
        /// then the input method's empty end and a composition after it; null when all went as it should, else what did
        /// not.</summary>
        private async Task<string?> CompositionEndedRound(NightlyPane pane, int pid, bool inField, bool nightlyEnds, Func<Task> end)
        {
            await EnsureActive();
            await pane.Agent.RunScriptAsync("ime.value = ''; comp = ''; update(), ''");
            // In the field, or where no editor has the keyboard (the page's text under it).
            if (!await ClickToFocus(pane, inField ? 420 : 150, inField ? 136 : 400, inField ? "ime" : ""))
                return $"the page did not take the click ({(inField ? "field" : "text")})";
            var client = TextInputClient();
            if (client == 0) return "no text input client";
            SetMarkedText(client, "かな");
            if (inField && !await Until(() => Fixture.Contains(" ime=かな comp=かな ", StringComparison.Ordinal), 10))
                return $"no composition: '{Title}'";
            if (!inField) await Task.Delay(300);
            await end();
            if (nightlyEnds && !await Until(() => Fixture.Contains(" ime=かな comp=end:かな ", StringComparison.Ordinal), 10))
                return $"Nightly did not commit it: '{Title}'";
            await Task.Delay(600);
            var before = Fixture;
            var endedByNightly = before.Contains(" comp=end:かな ", StringComparison.Ordinal);
            Log($"ime-ended by Nightly={endedByNightly} title='{before}'");
            if ((client = TextInputClient()) == 0) return "no text input client after the end";
            SetMarkedText(client, "");
            await Task.Delay(500);
            if (pane.RuntimePid != pid || !NightlyMach.IsAlive(pid)) return $"Nightly gone (pid {pid}→{pane.RuntimePid})";
            // What the page has stays: the committed text (Nightly's end), or nothing (the input method's end cancelled it).
            var expected = endedByNightly ? " ime=かな " : " ime= ";
            if (!await Until(() => Fixture.Contains(expected, StringComparison.Ordinal), 5))
                return $"after the empty end '{Title}', not{expected}";
            if (!await ClickToFocus(pane, 420, 136, "ime")) return "the field did not take the click again";
            if ((client = TextInputClient()) == 0) return "no text input client to compose again";
            SetMarkedText(client, "じ");
            InsertText(client, "字");
            if (!await Until(() => ComposedAgain.IsMatch(Fixture), 10)) return $"no composition after it: '{Title}'";
            return pane.RuntimePid == pid ? null : $"Nightly gone (pid {pid}→{pane.RuntimePid})";
        }

        /// <summary>A click on the page (its events queued, then handled), then the page's focused element is
        /// <paramref name="id"/> ("": none, the page). A click in a field ends its composition, so none is started before.</summary>
        private async Task<bool> ClickToFocus(NightlyPane pane, double x, double y, string id)
        {
            Click(pane, x, y);
            await Task.Delay(300);
            return await Until(async () => (await pane.Agent.RunScriptAsync("document.activeElement?.id ?? ''") ?? "") == id, 5);
        }

        private static readonly Regex ComposedAgain = new(" ime=[^ ]*字 comp=end:字 ");
    }
}
