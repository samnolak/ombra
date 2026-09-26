using Avalonia.Data.Converters;

namespace OmpGui.App.ViewModels;

/// <summary>Converters of the session menu, card and usage popover.</summary>
public static class SessionConverters
{
    /// <summary>The session card's detail: a path or a link fits in a few lines; omp's memory scrolls in a taller box.</summary>
    public static readonly IValueConverter DetailHeight = new FuncValueConverter<bool, double>(isLong => isLong ? 220 : 96);
}
