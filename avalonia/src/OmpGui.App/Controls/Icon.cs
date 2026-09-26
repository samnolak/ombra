using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace OmpGui.App.Controls;

/// <summary>
/// A line icon (Views/Icons.axaml, drawn on a 24-unit grid) at any size, stroked in the inherited text foreground, so
/// it follows the button or text around it through hover, pressed and disabled states and both themes.
/// </summary>
public sealed class Icon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty = AvaloniaProperty.Register<Icon, Geometry?>(nameof(Data));
    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<Icon, double>(nameof(Size), 18);
    public static readonly StyledProperty<double> StrokeThicknessProperty = AvaloniaProperty.Register<Icon, double>(nameof(StrokeThickness), 1.75);
    public static readonly StyledProperty<bool> FilledProperty = AvaloniaProperty.Register<Icon, bool>(nameof(Filled));

    static Icon()
    {
        AffectsRender<Icon>(DataProperty, StrokeThicknessProperty, FilledProperty, TextElement.ForegroundProperty);
        AffectsMeasure<Icon>(SizeProperty);
    }

    public Geometry? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public double StrokeThickness { get => GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public bool Filled { get => GetValue(FilledProperty); set => SetValue(FilledProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        if (Data is not { } data) return;
        var brush = TextElement.GetForeground(this) ?? Brushes.Black;
        var scale = Size / 24.0;
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
        {
            // The stroke is given in screen pixels: divided by the scale so every size draws the same line weight.
            var pen = new Pen(brush, StrokeThickness / scale, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            context.DrawGeometry(Filled ? brush : null, pen, data);
        }
    }
}
