using System.Diagnostics;
using System.Text.Json;
using Ombra.ClientCore;

namespace Ombra.FakeOmp;

/// <summary>Real OS children for the Windows job tests, not simulated process enumeration.</summary>
internal static class OwnedProcessLifetimeProcess
{
    internal static int Run(string[] args)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows job lifetime scenario");
        var mode = args[0];
        var path = args[1];
        if (mode == "windows-owned-leaf")
        {
            Publish(path, new { pid = Environment.ProcessId, owner = Environment.GetEnvironmentVariable(OwnedProcesses.OwnerVariable), arguments = args.Skip(2).ToArray() });
            Thread.Sleep(TimeSpan.FromMinutes(2)); // bounded even if a failed test cannot finish its cleanup
            return 0;
        }
        if (mode == "windows-owned-branch")
        {
            using var leaf = Process.Start(Self("windows-owned-leaf", path))!;
            WaitForFile(path);
            return 0; // grandchild remains alive after its immediate parent exits
        }
        OwnedProcesses.MarkThisProcess();
        OwnedProcesses.MarkThisProcess(); // idempotent: a second caller cannot replace/close the live job
        var directPath = Path.Combine(path, "direct.json");
        var grandchildPath = Path.Combine(path, "grandchild.json");
        var independentPath = Path.Combine(path, "independent.json");
        using var direct = Process.Start(Self("windows-owned-leaf", directPath))!;
        using (var branch = Process.Start(Self("windows-owned-branch", grandchildPath))!)
        {
            if (!branch.WaitForExit(30_000) || branch.ExitCode != 0) throw new IOException("branch did not finish");
        }
        var independentStart = Self("windows-owned-leaf", independentPath);
        independentStart.UseShellExecute = mode == "windows-owned-shell";
        foreach (var argument in new[] { "", "spaces and Юникод", "trailing\\", "embedded\"quote", "a&b%PATH%'" })
            independentStart.ArgumentList.Add(argument);
        using var independent = OwnedProcesses.StartIndependent(independentStart)!;
        WaitForFile(directPath);
        WaitForFile(independentPath);
        int? bunPid = null;
        if (mode == "windows-owned-bun")
        {
            var bun = Environment.GetEnvironmentVariable("OMBRA_TEST_BUN") ?? throw new IOException("OMBRA_TEST_BUN missing");
            var bunPath = Path.Combine(path, "bun.json");
            var leafScript = "await Bun.write(" + JsonSerializer.Serialize(bunPath + ".tmp") +
                ", JSON.stringify({pid:process.pid,owner:process.env.OMBRA_OWNER}));await (await import('node:fs/promises')).rename(" +
                JsonSerializer.Serialize(bunPath + ".tmp") + "," + JsonSerializer.Serialize(bunPath) + ");setInterval(()=>{},1000);";
            var brokerScript = "const p=Bun.spawn([process.execPath,'--eval'," + JsonSerializer.Serialize(leafScript) +
                "],{detached:true,windowsHide:true,stdin:'ignore',stdout:'ignore',stderr:'ignore'});p.unref();";
            var start = new ProcessStartInfo(bun) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--eval");
            start.ArgumentList.Add(brokerScript);
            using var broker = Process.Start(start)!;
            if (!broker.WaitForExit(30_000) || broker.ExitCode != 0) throw new IOException("Bun detached broker did not finish");
            WaitForFile(bunPath);
            using var info = JsonDocument.Parse(File.ReadAllText(bunPath));
            bunPid = info.RootElement.GetProperty("pid").GetInt32();
        }
        using var grandchild = JsonDocument.Parse(File.ReadAllText(grandchildPath));
        using var independentInfo = JsonDocument.Parse(File.ReadAllText(independentPath));
        Console.WriteLine(JsonSerializer.Serialize(new { direct = direct.Id, grandchild = grandchild.RootElement.GetProperty("pid").GetInt32(),
            independent = independentInfo.RootElement.GetProperty("pid").GetInt32(), bun = bunPid }));
        Console.Out.Flush();
        Console.ReadLine(); // EOF: graceful exit; test may instead kill only this owner, never its process tree
        return 0;
    }

    private static ProcessStartInfo Self(string mode, string path)
    {
        var executable = Environment.ProcessPath ?? throw new IOException("No process path");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(OwnedProcessLifetimeProcess).Assembly.Location);
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(path);
        return start;
    }

    private static void Publish(string path, object value)
    {
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value));
        File.Move(path + ".tmp", path);
    }

    private static void WaitForFile(string path)
    {
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException(path);
            Thread.Sleep(20);
        }
    }
}
