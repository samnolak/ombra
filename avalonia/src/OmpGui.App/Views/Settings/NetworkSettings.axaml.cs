using Avalonia.Controls;
using Avalonia.Platform.Storage;
using OmpGui.App.ViewModels;

namespace OmpGui.App.Views.Settings;

public partial class NetworkSettings : UserControl
{
    private NetworkTrustViewModel? _page;

    public NetworkSettings()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainViewModel vm || vm.NetworkTrust == _page) return;
            if (_page is not null) _page.PickCertificateFile = null;
            _page = vm.NetworkTrust;
            _page.PickCertificateFile = PickCertificateFileAsync;
            WorkspaceReveal.On(_page, nameof(_page.Explaining), () => _page.IsExplaining, NetworkTrustExplain);
        };
    }

    /// <summary>"Choose file…": the system's picker, a local file only.</summary>
    private async Task<string?> PickCertificateFileAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return null;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose your organisation's certificate authority",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Certificates") { Patterns = ["*.pem", "*.crt", "*.cer"], MimeTypes = ["application/x-pem-file", "application/x-x509-ca-cert", "application/pkix-cert"] },
                FilePickerFileTypes.All,
            ],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
