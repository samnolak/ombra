using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.Tests;

public sealed class SessionTitlesTests
{

    [Fact]
    public void A_custom_host_without_an_owned_source_path_does_not_import_the_dependency_copy()
    {
        var titles = new SessionTitles(Path.Combine(TestProcesses.TempDir("custom-title-runtime"), "extensions"));
        var before = new OmpLaunchSpec { FileName = "custom-engine" };
        Assert.Same(before, titles.Apply(before));
        Assert.False(File.Exists(titles.ExtensionPath));
    }
}
