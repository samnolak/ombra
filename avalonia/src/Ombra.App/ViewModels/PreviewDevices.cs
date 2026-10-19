using Ombra.App.Localization;
using System.Globalization;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Ombra.App.ViewModels;

/// <summary>The platform a device preset stands for: the user agent the page sees.</summary>
public enum DevicePlatform { Desktop, IPhone, IPad, Android }

/// <summary>
/// A size to test the page at (CSS pixels, portrait), the platform it stands for and its device pixel ratio (what
/// <c>devicePixelRatio</c> says where the engine can emulate it: the Nightly runtime).
/// </summary>
public sealed record DevicePreset(string Name, int Width, int Height, DevicePlatform Platform, double PixelRatio)
{
    public string SizeText => string.Create(CultureInfo.InvariantCulture, $"{Width}×{Height}");
}

/// <summary>
/// Device mode (the browsers' responsive / device toolbar): the page laid out at a chosen size, centred in the pane,
/// as a phone, a tablet or a desktop, with that platform's user agent where the engine takes one
/// (<see cref="Platform.PageUserAgent"/>). Applies to every tab but pop-ups; a size omp asked for its own tab wins
/// there. Null <see cref="DeviceSize"/>: the page fills the pane. In the Nightly runtime the page also gets the device's
/// pixel ratio and touch (<c>pointer: coarse</c>, touch events, the mouse acting as a finger), and a phone's or tablet's
/// viewport rules and orientation.
/// </summary>
public sealed partial class PreviewViewModel
{
    public const int MinDeviceSide = 200, MaxDeviceSide = 4000;

    public static IReadOnlyList<DevicePreset> DevicePresets { get; } =
    [
        new("iPhone SE", 375, 667, DevicePlatform.IPhone, 2),
        new("iPhone 15 Pro", 393, 852, DevicePlatform.IPhone, 3),
        new("Pixel 8", 412, 915, DevicePlatform.Android, 2.625),
        new("iPad mini", 768, 1024, DevicePlatform.IPad, 2),
        new("iPad Pro 11″", 834, 1194, DevicePlatform.IPad, 2),
        new("Laptop", 1280, 800, DevicePlatform.Desktop, 2),
        new("Desktop", 1440, 900, DevicePlatform.Desktop, 1),
    ];

    public static IReadOnlyList<DevicePlatform> DevicePlatforms { get; } = Enum.GetValues<DevicePlatform>();

    /// <summary>The pixel ratios the device bar offers; 0 is the display's own.</summary>
    public static IReadOnlyList<double> DevicePixelRatios { get; } = [0, 1, 1.5, 2, 2.625, 3, 3.5];

    public static string PixelRatioName(double ratio) =>
        ratio > 0 ? string.Create(CultureInfo.InvariantCulture, $"{ratio:0.###}×") : Loc.T("Display’s");

    public static Avalonia.Data.Converters.FuncValueConverter<double, string> PixelRatioNameConverter { get; } = new(PixelRatioName);

    /// <summary>The <c>devicePixelRatio</c> the page sees (the Nightly runtime); 0: the display's.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmbedDevice))]
    private double _devicePixelRatio;

    /// <summary>Touch input (the Nightly runtime): a phone's or tablet's by default, a desktop's if turned on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmbedDevice))]
    private bool _deviceTouch;

    /// <summary>What the Nightly runtime emulates in the page; null: nothing (the pane's size, Nightly's own user agent).</summary>
    internal Nightly.NightlyProtocol.Device? EmbedDevice =>
        DevicePlatform == DevicePlatform.Desktop && DevicePixelRatio <= 0 && !DeviceTouch
            ? null
            : new(DevicePlatform != DevicePlatform.Desktop, DevicePixelRatio, DeviceUserAgent ?? "", DeviceTouch, DeviceSize is not null && IsLandscape);

    /// <summary>The chosen size (portrait for a phone or tablet preset); null: the page fills the pane.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeviceViewport), nameof(HasDevice), nameof(DeviceLabel), nameof(DeviceTip), nameof(EmbedDevice))]
    private PixelSize? _deviceSize;

    /// <summary>The preset the size came from (the menu ticks it); null for a size typed in or none.</summary>
    [ObservableProperty] private DevicePreset? _devicePreset;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeviceViewport), nameof(DeviceLabel), nameof(DeviceTip), nameof(EmbedDevice))]
    private bool _isLandscape;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeviceUserAgent), nameof(DeviceLabel), nameof(DeviceTip), nameof(EmbedDevice))]
    private DevicePlatform _devicePlatform = DevicePlatform.Desktop;

    /// <summary>A phone or tablet touches, a desktop does not (the device bar can change it after).</summary>
    partial void OnDevicePlatformChanged(DevicePlatform value) => DeviceTouch = value != DevicePlatform.Desktop;

    [ObservableProperty] private string _customWidth = "";
    [ObservableProperty] private string _customHeight = "";

    public bool HasDevice => DeviceSize is not null;

    /// <summary>The size the page gets (turned for landscape); null: the pane's.</summary>
    public PixelSize? DeviceViewport => DeviceSize is { } s ? IsLandscape ? new PixelSize(s.Height, s.Width) : s : null;

    /// <summary>The user agent for <see cref="DevicePlatform"/>; null: the engine's own (desktop Safari on macOS).</summary>
    public string? DeviceUserAgent => UserAgentFor(DevicePlatform);

    /// <summary>Where the engine cannot change the user agent, the platform choice says so.</summary>
    public static bool CanSetUserAgent => Platform.PageUserAgent.IsSupported;

    /// <summary>The toolbar's text while a device is on: "390×844 · iPhone".</summary>
    public string DeviceLabel => DeviceViewport is { } v
        ? string.Create(CultureInfo.InvariantCulture, $"{v.Width}×{v.Height}") + (DevicePlatform == DevicePlatform.Desktop ? "" : " · " + PlatformName(DevicePlatform))
        : DevicePlatform == DevicePlatform.Desktop ? "" : PlatformName(DevicePlatform);

    public string DeviceTip => DeviceLabel.Length > 0 ? Loc.F("Device: {0} (Ctrl/⌘+Shift+M)", DeviceLabel) : Loc.T("Device size and platform (Ctrl/⌘+Shift+M)");

    public static string PlatformName(DevicePlatform p) => p switch
    {
        DevicePlatform.IPhone => "iPhone",
        DevicePlatform.IPad => "iPad",
        DevicePlatform.Android => "Android",
        _ => Loc.T("Desktop"),
    };

    internal static string? UserAgentFor(DevicePlatform p) => p switch
    {
        DevicePlatform.IPhone => "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1",
        DevicePlatform.IPad => "Mozilla/5.0 (iPad; CPU OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1",
        DevicePlatform.Android => "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Mobile Safari/537.36",
        _ => null,
    };

    /// <summary>A preset from the device bar or a test: its size and platform, upright.</summary>
    [RelayCommand]
    private void ChooseDevice(DevicePreset? preset)
    {
        if (preset is not null) DevicePreset = preset;
    }

    partial void OnDevicePresetChanged(DevicePreset? value)
    {
        if (value is null) return;
        IsLandscape = false;
        DeviceSize = new PixelSize(value.Width, value.Height);
        DevicePlatform = value.Platform;
        DeviceTouch = value.Platform != DevicePlatform.Desktop;
        DevicePixelRatio = value.PixelRatio;
        (CustomWidth, CustomHeight) = (value.Width.ToString(CultureInfo.InvariantCulture), value.Height.ToString(CultureInfo.InvariantCulture));
    }

    partial void OnDeviceSizeChanged(PixelSize? value)
    {
        if (value is { } s && DevicePreset is null)
            (CustomWidth, CustomHeight) = (s.Width.ToString(CultureInfo.InvariantCulture), s.Height.ToString(CultureInfo.InvariantCulture));
    }

    public static Avalonia.Data.Converters.FuncValueConverter<DevicePlatform, string> PlatformNameConverter { get; } = new(PlatformName);

    /// <summary>Back to the pane's size and the engine's own user agent, pixel ratio and input (Ctrl/⌘+Shift+M brings the device back).</summary>
    [RelayCommand]
    private void ClearDevice()
    {
        if (DeviceSize is not null) _lastDevice = (DeviceSize.Value, DevicePreset, DevicePlatform, IsLandscape, DevicePixelRatio, DeviceTouch);
        DevicePreset = null;
        IsLandscape = false;
        DeviceSize = null;
        DevicePlatform = DevicePlatform.Desktop;
        DevicePixelRatio = 0;
        DeviceTouch = false;
    }

    /// <summary>The device turned off last, for <see cref="ToggleDeviceCommand"/>.</summary>
    private (PixelSize Size, DevicePreset? Preset, DevicePlatform Platform, bool Landscape, double PixelRatio, bool Touch)? _lastDevice;

    /// <summary>Ctrl/⌘+Shift+M (Chrome's device toolbar key): device mode off, or back on as it was (a phone the first time).</summary>
    [RelayCommand]
    private void ToggleDevice()
    {
        if (DeviceSize is not null)
        {
            ClearDevice();
            return;
        }
        if (_lastDevice is not { } last)
        {
            ChooseDevice(DevicePresets[1]);
            return;
        }
        DevicePreset = last.Preset;
        DeviceSize = last.Size;
        DevicePlatform = last.Platform;
        IsLandscape = last.Landscape;
        DevicePixelRatio = last.PixelRatio;
        DeviceTouch = last.Touch;
    }

    [RelayCommand]
    private void ChoosePlatform(DevicePlatform platform) => DevicePlatform = platform;

    [RelayCommand]
    private void RotateDevice()
    {
        if (DeviceSize is not null) IsLandscape = !IsLandscape;
    }

    [RelayCommand]
    private void ToggleTouch() => DeviceTouch = !DeviceTouch;

    /// <summary>The size typed in the menu (each side <see cref="MinDeviceSide"/>–<see cref="MaxDeviceSide"/>); the platform stays.</summary>
    [RelayCommand]
    private void ApplyCustomSize()
    {
        if (!int.TryParse(CustomWidth.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var w)
            || !int.TryParse(CustomHeight.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var h)
            || w is < MinDeviceSide or > MaxDeviceSide || h is < MinDeviceSide or > MaxDeviceSide)
        {
            Message = Loc.F("A device size is two whole numbers from {0} to {1}.", MinDeviceSide, MaxDeviceSide);
            return;
        }
        Message = "";
        DevicePreset = null;
        IsLandscape = false;
        DeviceSize = new PixelSize(w, h);
    }

    /// <summary>The presets as the agent's <c>browser.devices</c> lists them.</summary>
    internal static IReadOnlyList<Ombra.ClientCore.AgentDevicePreset> AgentDevicePresets { get; } =
        [.. DevicePresets.Select(p => new Ombra.ClientCore.AgentDevicePreset(p.Name, p.Width, p.Height, p.PixelRatio, PlatformId(p.Platform)))];

    private static string PlatformId(DevicePlatform p) => p.ToString().ToLowerInvariant();

    /// <summary>
    /// The agent's <c>browser.set_device</c>: as the device bar would (a preset, then the size, platform, pixel ratio,
    /// touch and orientation asked for over it); null leaves device mode. Everything is checked before anything changes.
    /// </summary>
    internal Ombra.ClientCore.AgentDeviceState ApplyAgentDevice(Ombra.ClientCore.AgentDeviceRequest? request)
    {
        static Ombra.ClientCore.AgentBrowserException Invalid(string message) => new("invalid_params", message);
        if (request is null)
        {
            ClearDevice();
            return AgentDevice();
        }
        DevicePreset? preset = null;
        if (request.Preset is { } name)
            preset = DevicePresets.FirstOrDefault(p => SameName(p.Name, name))
                ?? throw Invalid($"Unknown device \"{name}\"; the presets: {string.Join(", ", DevicePresets.Select(p => p.Name))}.");
        if (request is { Width: { } w, Height: { } h } && (w is < MinDeviceSide or > MaxDeviceSide || h is < MinDeviceSide or > MaxDeviceSide))
            throw Invalid($"A device size is two whole numbers from {MinDeviceSide} to {MaxDeviceSide}.");
        DevicePlatform? platform = null;
        if (request.Platform is { } id)
            platform = DevicePlatforms.Cast<DevicePlatform?>().FirstOrDefault(p => PlatformId(p!.Value) == id.Trim().ToLowerInvariant())
                ?? throw Invalid($"Unknown platform \"{id}\"; one of {string.Join(", ", DevicePlatforms.Select(PlatformId))}.");
        if (request.Landscape is not null && preset is null && request.Width is null && DeviceSize is null)
            throw Invalid("Landscape needs a device size: a preset or a width and height.");

        if (preset is not null)
        {
            DevicePreset = null; // the same preset again starts over from it
            DevicePreset = preset;
        }
        if (request is { Width: { } width, Height: { } height })
        {
            DevicePreset = null;
            IsLandscape = false;
            DeviceSize = new PixelSize(width, height);
        }
        if (platform is { } chosen) DevicePlatform = chosen;
        if (request.PixelRatio is { } ratio) DevicePixelRatio = ratio;
        if (request.Touch is { } touch) DeviceTouch = touch;
        if (request.Landscape is { } landscape) IsLandscape = landscape;
        return AgentDevice();
    }

    private Ombra.ClientCore.AgentDeviceState AgentDevice() =>
        new(DevicePreset?.Name, DeviceViewport?.Width, DeviceViewport?.Height, DevicePixelRatio, DeviceTouch, PlatformId(DevicePlatform),
            IsLandscape, UseNightly);

    // "iPad Pro 11″" answers to "ipad pro 11" and "iPad Pro 11\"" too.
    private static bool SameName(string preset, string asked) =>
        string.Equals(preset.TrimEnd('″'), asked.Trim().TrimEnd('″', '"'), StringComparison.OrdinalIgnoreCase);
}
