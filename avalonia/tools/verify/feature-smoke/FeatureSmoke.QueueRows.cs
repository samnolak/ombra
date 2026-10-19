#if OMBRA_FEATURE_SMOKE
using Ombra.App.ViewModels;
using Ombra.ClientCore;

namespace Ombra.App.Smoke;

internal static partial class FeatureSmoke
{
    private sealed partial class Run
    {
        private (string, Func<Task>)[] QueueRowsSections() => [("queue-update", QueueUpdate), ("message-id", MessageIds)];

        /// <summary>The stand-in model matches some keywords anywhere in the conversation: start from a clean chat.</summary>
        private async Task FreshChat()
        {
            await Until(() => vm.Phase == SessionPhase.Ready, 60);
            await vm.OpenFolderInNewSessionAsync(vm.Session.CurrentLaunch.WorkingDirectory ?? Environment.CurrentDirectory);
            await Until(() => vm.Phase == SessionPhase.Ready, 60);
        }

        private bool SawFrame(string type) => vm.Session.Snapshot().DebugTail.Any(d => d.Type == type);

        /// <summary>The chips follow omp's queue_update: local follow-ups, one queued over RPC by "another client"
        /// (no local echo exists for it), and all of them drain as omp delivers them.</summary>
        private async Task QueueUpdate()
        {
            await FreshChat();
            await Send("SLOWSHORT queue rows");
            Check("queue-update-running", await Until(() => vm.IsRunning, 15), $"phase={vm.Phase}");
            foreach (var text in new[] { "queue rows alpha", "queue rows beta" })
            {
                vm.ComposerText = text;
                FocusComposer();
                Key("\r", 0x24);
                await Until(() => vm.ComposerText.Length == 0, 5);
            }
            await vm.Session.Connection!.RequestAsync("follow_up", w => w.WriteString("message", "queue rows external"));
            string Chips() => string.Join(" | ", vm.QueuedMessages.Select(q => $"{q.Model.Kind}:{q.Text}"));
            var three = await Until(() => vm.QueuedMessages.Select(q => q.Text).SequenceEqual(["queue rows alpha", "queue rows beta", "queue rows external"]), 10);
            Check("queue-update-chips", three, $"chips=[{Chips()}] running={vm.IsRunning}");
            Check("queue-update-frames", SawFrame("queue_update"), "the engine reported its queue");
            Check("queue-update-local-echo-kept", vm.QueuedMessages.Take(2).All(q => q.Model.SubmissionId is not null),
                $"chips=[{Chips()}]");
            await Task.Delay(300);
            Shot("20-queue-update-dark");
            var drained = await Until(() => vm.QueuedMessages.Count == 0
                && UserRowsContain("queue rows alpha") && UserRowsContain("queue rows beta") && UserRowsContain("queue rows external"), 90);
            Check("queue-update-drained", drained, $"chips=[{Chips()}] running={vm.IsRunning} notices=[{Notices()}]");
            await Until(() => !vm.IsRunning, 60);
            Shot("21-queue-update-drained-dark");
        }

        /// <summary>A steer mid-reply makes omp end one assistant message and start the next: every row settles with
        /// its own text and nothing streamed to the wrong row.</summary>
        private async Task MessageIds()
        {
            await FreshChat();
            var mismatches = vm.Session.Snapshot().StreamMismatches;
            var replies = vm.Session.Snapshot().Items.OfType<AssistantItem>().Count();
            await Send("SLOWSHORT message ids");
            Check("message-id-running", await Until(() => vm.IsRunning, 15), $"phase={vm.Phase}");
            await Until(() => AssistantRowsContain("word3 "), 15);
            vm.ComposerText = "SLOWSHORT steer for message ids";
            FocusComposer();
            Key("\r", 0x24, Alt);
            var done = await Until(() => !vm.IsRunning && UserRowsContain("steer for message ids"), 90);
            var s = vm.Session.Snapshot();
            var rows = s.Items.OfType<AssistantItem>().Skip(replies).ToArray();
            Check("message-id-rows", done && rows.Length >= 2 && rows.All(r => !r.Streaming && r.Text.StartsWith("word0 ", StringComparison.Ordinal)),
                $"done={done} rows=[{string.Join(" | ", rows.Select(r => $"{r.Streaming}:{r.Text.Length}"))}]");
            Check("message-id-no-mismatch", s.StreamMismatches == mismatches, $"mismatches={s.StreamMismatches} before={mismatches}");
            Check("message-id-frames", SawFrame("message_update"));
            await Task.Delay(300);
            Shot("22-message-id-dark");
        }
    }
}
#endif
