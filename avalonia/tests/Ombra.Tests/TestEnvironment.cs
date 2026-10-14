using System.Runtime.CompilerServices;
using Ombra.ClientCore;

namespace Ombra.Tests;

internal static class TestEnvironment
{
    /// <summary>Every window a test opens makes chat folders (a new chat, the spare omp's): never in the user's data folder.</summary>
    [ModuleInitializer]
    internal static void KeepChatsOutOfTheUsersDataFolder() =>
        Environment.SetEnvironmentVariable(ChatFolders.RootEnvVar, TestProcesses.TempDir("chats"));
}
