using System.Text.Json;
using Ombra.ClientCore;

namespace Ombra.FakeOmp;

/// <summary>Real-process journal/admission driver. No RPC, timers for ownership, or simulated lock implementation.</summary>
internal static class JournalOwnershipProcess
{
    internal static int Run(string[] args)
    {
        try
        {
            var config = ClientInstanceLease.SettingsPath(args[1]);
            var needsInstance = args[0] != "draft-owner";
            using var instance = needsInstance ? ClientInstanceLease.TryAcquire(config) : null;
            if (needsInstance && instance is null) { Console.WriteLine("busy"); return 2; }
            if (args[0] == "admission-owner")
            {
                Console.WriteLine("ready");
                Console.ReadLine();
                return 0;
            }
            using var store = new DraftStore(DraftStore.PathFor(config));
            Console.WriteLine(JsonSerializer.Serialize(new { status = "ready", pid = Environment.ProcessId, text = store.Get("one")?.Composer.Text, error = store.LastError }));
            while (Console.ReadLine() is { } line)
            {
                var text = JsonSerializer.Deserialize<string>(line)!;
                store.Update("one", d => d with { Composer = DraftContent.Empty with { Text = text } }, Path.GetDirectoryName(config)!, flush: true);
                Console.WriteLine("saved");
            }
            return 0;
        }
        catch (DraftStoreInUseException) { Console.WriteLine("busy"); return 2; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(e.Message);
            return 3;
        }
    }
}
