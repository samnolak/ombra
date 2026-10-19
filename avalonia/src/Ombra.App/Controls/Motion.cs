using Avalonia;
using Avalonia.Controls;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.VisualTree;
using Ombra.App.Platform;

namespace Ombra.App.Controls;

/// <summary>One motion policy per window. Re-read the OS preference on activation, including after Settings changes.
/// Only opacity/transforms animate; transcript heights and scroll anchors never do.</summary>
public sealed class Motion : AvaloniaObject
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Motion, Window, bool>("IsEnabled");
    public static readonly AttachedProperty<MotionKind> KindProperty =
        AvaloniaProperty.RegisterAttached<Motion, Control, MotionKind>("Kind");

    public static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(120);
    public static readonly TimeSpan Base = TimeSpan.FromMilliseconds(150);
    private static readonly CubicEaseOut Ease = new();

    static Motion()
    {
        IsEnabledProperty.Changed.AddClassHandler<Window>((window, change) =>
        {
            window.Activated -= Activated;
            if (change.NewValue is true)
            {
                Refresh(window);
                window.Activated += Activated;
            }
        });
        KindProperty.Changed.AddClassHandler<Control>((control, _) =>
        {
            control.AttachedToVisualTree -= Attached;
            control.Transitions = Create(GetKind(control));
            if (GetKind(control) != MotionKind.None)
            {
                control.AttachedToVisualTree += Attached;
                Update(control);
            }
        });
    }

    public static bool GetIsEnabled(Window window) => window.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(Window window, bool value) => window.SetValue(IsEnabledProperty, value);
    public static MotionKind GetKind(Control control) => control.GetValue(KindProperty);
    public static void SetKind(Control control, MotionKind value) => control.SetValue(KindProperty, value);
    private static void Attached(object? sender, VisualTreeAttachmentEventArgs e) { if (sender is Control control) Update(control); }

    // A style's Transitions object is shared. Each control must own its clocks and duration: a resource binding on a
    // shared transition otherwise retains the first window's reduced-motion preference.
    private static Transitions? Create(MotionKind kind) => kind switch
    {
        MotionKind.FadeFast or MotionKind.FadeBase => [new DoubleTransition { Property = Visual.OpacityProperty, Easing = Ease }],
        MotionKind.Chevron =>
        [
            new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Easing = Ease },
            new DoubleTransition { Property = Visual.OpacityProperty, Easing = Ease },
        ],
        MotionKind.Send => [new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Easing = Ease }],
        MotionKind.SwitchTrack => [new BrushTransition { Property = Border.BackgroundProperty, Easing = Ease }],
        MotionKind.SwitchKnob => [new ThicknessTransition { Property = Control.MarginProperty, Easing = Ease }],
        _ => null,
    };

    private static void Update(Control control)
    {
        if (GetKind(control) == MotionKind.None || control.Transitions is not { } transitions) return;
        var key = GetKind(control) == MotionKind.FadeBase ? "GuiMotionBase" : "GuiMotionFast";
        var duration = TopLevel.GetTopLevel(control) is Window window && window.Resources.TryGetResource(key, null, out var value)
            && value is TimeSpan token ? token : TimeSpan.Zero;
        foreach (var transition in transitions)
            switch (transition)
            {
                case DoubleTransition fade: fade.Duration = duration; break;
                case TransformOperationsTransition transform: transform.Duration = duration; break;
                case BrushTransition brush: brush.Duration = duration; break;
                case ThicknessTransition thickness: thickness.Duration = duration; break;
            }
    }
    private static void Activated(object? sender, EventArgs e) { if (sender is Window window) Refresh(window); }

    internal static void Refresh(Window window)
    {
        var reduced = ReducedMotion.System;
        window.Classes.Set("reduced-motion", reduced);
        window.Resources["GuiMotionFast"] = reduced ? TimeSpan.Zero : Fast;
        window.Resources["GuiMotionBase"] = reduced ? TimeSpan.Zero : Base;
        foreach (var visual in window.GetVisualDescendants())
            if (visual is Control control) Update(control);
    }
}

public enum MotionKind { None, FadeFast, FadeBase, Chevron, Send, SwitchTrack, SwitchKnob }
