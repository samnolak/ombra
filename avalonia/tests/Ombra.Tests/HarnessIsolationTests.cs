using Ombra.ClientCore;
using Ombra.Verify;

namespace Ombra.Tests;

/// <summary>
/// The guard the feature smoke and the real-omp tests run before omp starts (tools/verify/HarnessIsolation.cs): a run
/// whose omp would use the user's home or agent folder, or whose chats and teams would land in the user's Ombra data
/// folder, is refused — a smoke on an unreadable settings file once wrote a chat into the user's ~/.omp sessions.
/// </summary>
public sealed class HarnessIsolationTests
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
    private static readonly string UserData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "Ombra");
    private static string HomeKey => OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME";

    private static Func<string, string?> Inherited(Dictionary<string, string?> env) => k => env.GetValueOrDefault(k);

    [Fact]
    public void The_defaults_the_app_falls_back_to_are_refused()
    {
        // No settings: omp inherits the user's home, so its sessions would go to ~/.omp/agent/sessions
        var defaults = new OmpRuntimeOptions().ToLaunchSpec().Environment;
        var problems = HarnessIsolation.Problems(defaults, Inherited(new() { [HomeKey] = Home }), TestProcesses.TempDir("iso-chats"), null);
        Assert.Contains(problems, p => p.Contains(Home, StringComparison.Ordinal));
        // A removed home is the user's too (omp asks the system for it)
        Assert.NotEmpty(HarnessIsolation.Problems(new Dictionary<string, string?> { [HomeKey] = null }, Inherited(new() { [HomeKey] = Home }), null, null));
    }

    [Fact]
    public void A_run_with_its_own_home_and_folders_may_start()
    {
        var run = TestProcesses.TempDir("iso-run");
        var options = new OmpRuntimeOptions { Profile = "harness", Environment = new() { [HomeKey] = Path.Combine(run, "home"), ["PI_CODING_AGENT_DIR"] = null } };
        Assert.Empty(HarnessIsolation.Problems(options.ToLaunchSpec().Environment, Inherited(new() { [HomeKey] = Home }),
            Path.Combine(run, "chats"), Path.Combine(run, "teams")));
    }

    [Fact]
    public void An_agent_folder_or_app_folders_of_the_user_are_refused_even_with_a_home_of_its_own()
    {
        var run = TestProcesses.TempDir("iso-agent");
        var env = new Dictionary<string, string?> { [HomeKey] = Path.Combine(run, "home") };
        // PI_CODING_AGENT_DIR inherited from the shell, pointing at the user's agent folder
        Assert.Single(HarnessIsolation.Problems(env, Inherited(new() { ["PI_CODING_AGENT_DIR"] = Path.Combine(Home, ".omp", "agent") }), null, null));
        Assert.Single(HarnessIsolation.Problems(new Dictionary<string, string?> { [HomeKey] = Home }, Inherited([]), null, null));
        // The app's chats or teams in the user's data folder, or not set at all (the default is that folder)
        Assert.Single(HarnessIsolation.Problems(env, Inherited([]), Path.Combine(UserData, "chats"), null));
        Assert.Single(HarnessIsolation.Problems(env, Inherited([]), null, ""));
    }
}
