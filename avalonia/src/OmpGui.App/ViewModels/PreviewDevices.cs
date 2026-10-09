using System.Globalization;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OmpGui.App.ViewModels;

/// <summary>The platform a device preset stands for: the user agent the page sees.</summary>
public enum DevicePlatform { Desktop, IPhone, IPad, Android }

/// <summary>A size to test the page at (CSS pixels, portrait) and the platform it stands for.</summary>
public sealed record DevicePreset(string Name, int Width, int Height, DevicePlatform Platform)
{
    public string SizeText => string.Create(CultureInfo.InvariantCulture, $"{Width}×{Height}");
}

/// <summary>
/// Device mode (the browsers' responsive / device toolbar): the page laid out at a chosen size, centred in the pane,
/// as a phone, a tablet or a desktop, with that platform's user agent where the engine takes one
/// (<see cref="Platform.PageUserAgent"/>). Applies to every tab but pop-ups; a size omp asked for its own tab wins
/// there. Null <see cref="DeviceSize"/>: the page fills the pane.
/// </summary>
public sealed partial class PreviewViewModel
{
    public const int MinDeviceSide = 200, MaxDeviceSide = 4000;

    public static IReadOnlyList<DevicePreset> DevicePresets { get; } =
    [
        new("iPhone SE", 375, 667, DevicePlatform.IPhone),
        new("iPhone 15 Pro", 393, 852, DevicePlatform.IPhone),
        new("Pixel 8", 412, 915, DevicePlatform.Android),
        new("iPad mini", 768, 1024, DevicePlatform.IPad),
        new("iPad Pro 11″", 834, 1194, DevicePlatform.IPad),
        new("Laptop", 1280, 800, DevicePlatform.Desktop),
        new("Desktop", 1440, 900, DevicePlatform.Desktop),
    ];

    public static IReadOnlyList<DevicePlatform> DevicePlatforms { get; } = Enum.GetValues<DevicePlatform>();

    /// <summary>The chosen size (portrait for a phone or tablet preset); null: the page fills the pane.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeviceViewport), nameof(HasDevice), nameof(DeviceLabel), nameof(DeviceTip))]
    private PixelSize? _deviceSize;

    /// <summary>The preset the size came from (the menu ticks it); null for a size typed in or none.</summary>
    [ObservableProperty] private DevicePreset? _devicePreset;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeviceViewport), nameof(DeviceLabel), nameof(DeviceTip))]
    private bool _isLandscape;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeviceUserAgent), nameof(DeviceLabel), nameof(DeviceTip))]
    private DevicePlatform _devicePlatform = DevicePlatform.Desktop;

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

    public string DeviceTip => DeviceLabel.Length > 0 ? $"Device: {DeviceLabel} (Ctrl/⌘+Shift+M)" : "Device size and platform (Ctrl/⌘+Shift+M)";

    public static string PlatformName(DevicePlatform p) => p switch
    {
        DevicePlatform.IPhone => "iPhone",
        DevicePlatform.IPad => "iPad",
        DevicePlatform.Android => "Android",
        _ => "Desktop",
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
        (CustomWidth, CustomHeight) = (value.Width.ToString(CultureInfo.InvariantCulture), value.Height.ToString(CultureInfo.InvariantCulture));
    }

    partial void OnDeviceSizeChanged(PixelSize? value)
    {
        if (value is { } s && DevicePreset is null)
            (CustomWidth, CustomHeight) = (s.Width.ToString(CultureInfo.InvariantCulture), s.Height.ToString(CultureInfo.InvariantCulture));
    }

    public static Avalonia.Data.Converters.FuncValueConverter<DevicePlatform, string> PlatformNameConverter { get; } = new(PlatformName);

    /// <summary>Back to the pane's size and the engine's own user agent (Ctrl/⌘+Shift+M brings the device back).</summary>
    [RelayCommand]
    private void ClearDevice()
    {
        if (DeviceSize is not null) _lastDevice = (DeviceSize.Value, DevicePreset, DevicePlatform, IsLandscape);
        DevicePreset = null;
        IsLandscape = false;
        DeviceSize = null;
        DevicePlatform = DevicePlatform.Desktop;
    }

    /// <summary>The device turned off last, for <see cref="ToggleDeviceCommand"/>.</summary>
    private (PixelSize Size, DevicePreset? Preset, DevicePlatform Platform, bool Landscape)? _lastDevice;

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
    }

    [RelayCommand]
    private void ChoosePlatform(DevicePlatform platform) => DevicePlatform = platform;

    [RelayCommand]
    private void RotateDevice()
    {
        if (DeviceSize is not null) IsLandscape = !IsLandscape;
    }

    /// <summary>The size typed in the menu (each side <see cref="MinDeviceSide"/>–<see cref="MaxDeviceSide"/>); the platform stays.</summary>
    [RelayCommand]
    private void ApplyCustomSize()
    {
        if (!int.TryParse(CustomWidth.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var w)
            || !int.TryParse(CustomHeight.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var h)
            || w is < MinDeviceSide or > MaxDeviceSide || h is < MinDeviceSide or > MaxDeviceSide)
        {
            Message = $"A device size is two whole numbers from {MinDeviceSide} to {MaxDeviceSide}.";
            return;
        }
        Message = "";
        DevicePreset = null;
        IsLandscape = false;
        DeviceSize = new PixelSize(w, h);
    }
}
