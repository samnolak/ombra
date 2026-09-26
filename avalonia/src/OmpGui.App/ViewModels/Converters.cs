using Avalonia.Data.Converters;
using Avalonia.Media;

namespace OmpGui.App.ViewModels;

public static class Converters
{
    public static readonly IValueConverter BoolToWeight =
        new FuncValueConverter<bool, FontWeight>(b => b ? FontWeight.SemiBold : FontWeight.Normal);

    /// <summary>Two bound values are equal (a menu item is the current choice).</summary>
    public static readonly IMultiValueConverter AreEqual =
        new FuncMultiValueConverter<object?, bool>(values => values.ToList() is [var a, var b] && Equals(a, b));
}
