namespace Ombra.Server.Cli;

public static class EntryPoint
{
    public static async Task<int> Main(string[] args)
    {
        var envFile = Environment.GetEnvironmentVariable("OMBRA_ENV_FILE") ?? "/etc/ombra/server.env";
        return await AdminCli.RunAsync(args, AdminCli.LoadConfiguration(envFile), Console.Out, Console.Error);
    }
}
