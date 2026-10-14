using Ombra.App.Services;
using Ombra.ClientCore;
using Xunit;

namespace Ombra.Tests;

/// <summary>The move from OMP GUI's data folder: nothing is lost, and chats made under the old name still count as chats.</summary>
public class LegacyNamesTests
{
    [Fact]
    public void Old_data_folder_moves_to_Ombra_with_a_link_left_and_the_settings_file_renamed()
    {
        var root = Directory.CreateTempSubdirectory("ombra-legacy-").FullName;
        try
        {
            var old = Path.Combine(root, "OmpGui");
            Directory.CreateDirectory(Path.Combine(old, "chats", "chat-1"));
            File.WriteAllText(Path.Combine(old, "omp-gui.local.json"), "{\"theme\":\"dark\"}");

            LegacyNames.MoveDataFolder(root, _ => { });

            var moved = Path.Combine(root, "Ombra");
            Assert.Equal("{\"theme\":\"dark\"}", File.ReadAllText(Path.Combine(moved, "ombra.local.json")));
            Assert.False(File.Exists(Path.Combine(moved, "omp-gui.local.json")));
            Assert.True(Directory.Exists(Path.Combine(moved, "chats", "chat-1")));
            // omp recorded old chats by the old path: it still leads to them
            Assert.NotNull(new DirectoryInfo(old).LinkTarget);
            Assert.True(Directory.Exists(Path.Combine(old, "chats", "chat-1")));

            // A second start changes nothing (the old path is now the link)
            LegacyNames.MoveDataFolder(root, _ => { });
            Assert.True(File.Exists(Path.Combine(moved, "ombra.local.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void An_existing_Ombra_folder_is_never_overwritten()
    {
        var root = Directory.CreateTempSubdirectory("ombra-legacy-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "OmpGui"));
            File.WriteAllText(Path.Combine(root, "OmpGui", "omp-gui.local.json"), "old");
            Directory.CreateDirectory(Path.Combine(root, "Ombra"));
            File.WriteAllText(Path.Combine(root, "Ombra", "ombra.local.json"), "new");

            LegacyNames.MoveDataFolder(root, _ => { });

            Assert.Equal("new", File.ReadAllText(Path.Combine(root, "Ombra", "ombra.local.json")));
            Assert.Equal("old", File.ReadAllText(Path.Combine(root, "OmpGui", "omp-gui.local.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Chats_made_under_the_old_root_still_count_as_chats()
    {
        var chats = new ChatFolders("/data/Ombra/chats", "/data/OmpGui/chats");

        Assert.True(chats.Contains("/data/OmpGui/chats/20260101-000000-000-abcdef"));
        Assert.True(chats.Contains("/data/Ombra/chats/20260101-000000-000-abcdef"));
        Assert.False(chats.Contains("/data/OmpGui/chats"));
        Assert.False(new ChatFolders("/data/Ombra/chats").Contains("/data/OmpGui/chats/x"));
    }
}
