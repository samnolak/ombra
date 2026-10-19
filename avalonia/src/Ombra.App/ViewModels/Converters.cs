using Avalonia.Data.Converters;
using Avalonia.Media;
using Ombra.App.Localization;
using Ombra.ClientCore.Browser;

namespace Ombra.App.ViewModels;

public static class Converters
{
    public static readonly IValueConverter BoolToWeight =
        new FuncValueConverter<bool, FontWeight>(b => b ? FontWeight.SemiBold : FontWeight.Normal);

    /// <summary>Two bound values are equal (a menu item is the current choice).</summary>
    public static readonly IMultiValueConverter AreEqual =
        new FuncMultiValueConverter<object?, bool>(values => values.ToList() is [var a, var b] && Equals(a, b));

    /// <summary>What the downloads list says under a download's name; bound to its State, BytesReceived, TotalBytes and Error.</summary>
    public static readonly IMultiValueConverter DownloadStatus =
        new FuncMultiValueConverter<object?, string>(values => values.ToList() is [BrowserDownloadState state, long received, long total, var error]
            ? DownloadStatusText(state, received, total, error as string)
            : "");

    internal static string DownloadStatusText(BrowserDownloadState state, long received, long total, string? error) => state switch
    {
        BrowserDownloadState.Asking => Loc.T("Waiting for a place to save it"),
        BrowserDownloadState.Saving => total > 0
            ? Loc.F("{0} of {1}", BrowserDialogCardViewModel.FormatSize(received), BrowserDialogCardViewModel.FormatSize(total))
            : received > 0 ? BrowserDialogCardViewModel.FormatSize(received) : Loc.T("Starting…"),
        BrowserDownloadState.Finished => total > 0 ? Loc.F("Saved · {0}", BrowserDialogCardViewModel.FormatSize(total)) : Loc.T("Saved"),
        BrowserDownloadState.Failed => error is { Length: > 0 } e ? Loc.F("Failed: {0}", e) : Loc.T("Failed"),
        _ => Loc.T("Cancelled"),
    };
}
