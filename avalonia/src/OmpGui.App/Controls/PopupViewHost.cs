using Avalonia.Controls;
using Avalonia.Platform;
using OmpGui.ClientCore.Browser;

namespace OmpGui.App.Controls;

/// <summary>
/// Hosts a pop-up's native web view (<see cref="BrowserPopup.NativeHandle"/>, built by the engine for the page that
/// opened it) in a preview tab. Avalonia's NativeWebView cannot take the engine's configuration, so the view is
/// embedded as it is; the pop-up owns it (<see cref="BrowserPopup.Close"/> releases it), this control only shows it.
/// </summary>
internal sealed class PopupViewHost(BrowserPopup popup) : NativeControlHost
{
    public BrowserPopup Popup { get; } = popup;

    /// <summary>The pop-up as a platform handle (screenshots take the same path as the tabs' web views).</summary>
    public IPlatformHandle? Handle => Popup.IsClosed ? null : new PopupPlatformHandle(Popup.NativeHandle, Popup.HandleDescriptor);

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent) =>
        Popup.IsClosed ? base.CreateNativeControlCore(parent) : new PlatformHandle(Popup.NativeHandle, Popup.HandleDescriptor);

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        // The pop-up's view stays the pop-up's: it leaves its superview when the pop-up closes
        if (control.Handle != Popup.NativeHandle) base.DestroyNativeControlCore(control);
    }

    /// <summary>A WKWebView of our own, as Avalonia describes the ones it creates.</summary>
    private sealed class PopupPlatformHandle(nint handle, string descriptor) : IAppleWKWebViewPlatformHandle
    {
        public nint Handle { get; } = handle;
        public string? HandleDescriptor { get; } = descriptor;
        public nint WKWebView => Handle;
        public nint GetWKWebViewRetained() => throw new NotSupportedException("The pop-up keeps its own reference.");
    }
}
