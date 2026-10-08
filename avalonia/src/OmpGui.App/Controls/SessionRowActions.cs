using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace OmpGui.App.Controls;

/// <summary>
/// Gives a sidebar session row the class <c>actions-shown</c> while its pin or ⋯ button shows without the pointer on
/// the row: reached with the keyboard (<c>:focus-visible</c>) or with its menu open (<c>:flyout-open</c>; the pointer is
/// then in the menu's popup). The row's age or status dot hides on that class as it does on hover, so neither is drawn
/// under a button. Avalonia's selectors have no <c>:has()</c> to say this in the styles alone. Set on
/// <c>Panel.session-row</c> by Styles.Workspace.axaml.
/// </summary>
public static class SessionRowActions
{
    public static readonly AttachedProperty<bool> TrackProperty =
        AvaloniaProperty.RegisterAttached<Panel, bool>("Track", typeof(SessionRowActions));

    /// <summary>The rows already followed (a recycled row keeps its buttons).</summary>
    private static readonly ConditionalWeakTable<Panel, Button[]> Followed = new();

    static SessionRowActions() => TrackProperty.Changed.AddClassHandler<Panel>((row, e) =>
    {
        if (e.NewValue is not true) return;
        row.Loaded += (_, _) => Follow(row);
        if (row.IsLoaded) Follow(row);
    });

    public static bool GetTrack(Panel row) => row.GetValue(TrackProperty);
    public static void SetTrack(Panel row, bool value) => row.SetValue(TrackProperty, value);

    private static void Follow(Panel row)
    {
        if (Followed.TryGetValue(row, out _)) return;
        var buttons = row.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("session-action")).ToArray();
        if (buttons.Length == 0) return;
        Followed.Add(row, buttons);
        void Update() => row.Classes.Set("actions-shown", buttons.Any(b => b.Classes.Contains(":focus-visible") || b.Classes.Contains(":flyout-open")));
        // A pseudo-class is an entry of Classes: its changes are the collection's
        foreach (var b in buttons) b.Classes.CollectionChanged += (_, _) => Update();
        Update();
    }
}
