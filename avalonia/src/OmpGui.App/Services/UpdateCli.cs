using OmpGui.ClientCore;

namespace OmpGui.App.Services;

/// <summary>
/// <c>OmpGui --update [--yes [--restart]] [--feed url] [--config path]</c>: the update check without a window (for
/// scripts and the update E2E). Without <c>--yes</c> it only reports; with it the new version is downloaded, verified,
/// unpacked next to the app and swapped in once this process has exited; <c>--restart</c> then starts the new version
/// (what Settings → Updates → Restart now does). Exit codes: 0 up to date or done, 1 failed,
/// 2 cannot install in place (the reason is printed), 3 an update exists (without --yes).
/// </summary>
public static class UpdateCli
{
    public static async Task<int> RunAsync(AppArgs args)
    {
        OmpRuntimeOptions options;
        try { options = new ClientSettingsStore(args.ConfigPath ?? Environment.GetEnvironmentVariable(OmpRuntimeOptions.ConfigEnvVar) ?? OmpRuntimeOptions.DefaultConfigPath).Load(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { options = new OmpRuntimeOptions(); }
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        var checker = new UpdateChecker(http, args.UpdateFeed ?? options.UpdateFeed ?? UpdateChecker.DefaultFeed,
            UpdateChecker.ThisVersion(), UpdateChecker.ThisRid(), UpdateChecker.BuiltInPublicKey());
        try
        {
            var check = await checker.CheckAsync(CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine(check.Message);
            if (!check.Available || check.Asset is not { } asset || check.Latest is not { } version) return 0;
            if (check.Notes is { Length: > 0 } notes) Console.WriteLine(notes);
            if (check.BringsNewOmp) Console.WriteLine($"It moves omp to {check.Runtime!.Omp}: installed at the next start of the app.");
            if (!args.Yes)
            {
                Console.WriteLine("Run with --yes to install it.");
                return 3;
            }
            if (UpdateInstaller.ForThisApp(out var whyNot) is not { } installer)
            {
                Console.WriteLine($"Cannot install in place: {whyNot}. Download it from the release page instead.");
                return 2;
            }
            var package = await checker.DownloadAsync(asset, installer.StateDirectory, null, CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine($"Downloaded and verified {asset.File}.");
            var staged = await installer.StageAsync(package, version, CancellationToken.None).ConfigureAwait(false);
            string[] relaunchArgs = args.ConfigPath is { } config ? ["--config", config] : [];
            installer.Apply(staged, checker.Current, Environment.ProcessId, relaunch: args.Restart, relaunchArgs);
            Console.WriteLine($"Version {version} replaces {installer.InstallRoot} as soon as this command has exited; log: {Path.Combine(installer.StateDirectory, "apply.log")}");
            return 0;
        }
        catch (Exception e) when (e is HttpRequestException or InvalidDataException or IOException or UnauthorizedAccessException
                                   or TimeoutException or TaskCanceledException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine("Update failed: " + e.Message);
            return 1;
        }
    }
}
