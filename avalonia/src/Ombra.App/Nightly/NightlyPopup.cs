using System.Text.Json;

namespace Ombra.App.Nightly;

/// <summary>
/// A popup a Nightly page opened, which Ombra draws (desktop <c>zen-embed.js</c>, <c>ZenEmbedPopups</c>): a
/// <c>&lt;select&gt;</c> dropdown, form autocomplete or the page's context menu. <see cref="Anchor"/> is in the window's
/// points (top-left origin); the answer is an index into <see cref="Items"/> (<see cref="NightlyProtocol.PopupChoiceMessage"/>).
/// </summary>
internal sealed record NightlyPopup(NightlyPopup.PopupKind Kind, (double X, double Y, double Width, double Height) Anchor,
    IReadOnlyList<NightlyPopup.Item> Items, int Selected)
{
    public enum PopupKind { Select, Autocomplete, Menu }

    public enum ItemKind { Item, Header, Separator }

    /// <param name="Label">The page's text (a menu's commands have none: <see cref="Command"/> names them).</param>
    /// <param name="Detail">Secondary text (an autocomplete entry's comment).</param>
    /// <param name="Command">A context menu command (<c>open-link</c>, <c>copy</c> …), labelled by Ombra.</param>
    /// <param name="Indent">An option inside an <c>&lt;optgroup&gt;</c>.</param>
    public sealed record Item(ItemKind Kind, string Label, string Detail, string? Command, bool Disabled, bool Indent)
    {
        public bool CanChoose => Kind == ItemKind.Item && !Disabled;
    }

    /// <summary>The popup the JSON describes; null when it is not one (an unknown kind, no items, bad JSON).</summary>
    public static NightlyPopup? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            PopupKind? kind = Str(root, "kind") switch
            {
                "select" => PopupKind.Select,
                "autocomplete" => PopupKind.Autocomplete,
                "menu" => PopupKind.Menu,
                _ => null,
            };
            if (kind is null || !root.TryGetProperty("items", out var array) || array.ValueKind != JsonValueKind.Array) return null;
            var items = new List<Item>();
            foreach (var e in array.EnumerateArray())
            {
                var itemKind = Str(e, "kind") switch { "header" => ItemKind.Header, "separator" => ItemKind.Separator, _ => ItemKind.Item };
                items.Add(new Item(itemKind, Str(e, "label") ?? "", Str(e, "detail") ?? "", Str(e, "command"), Bool(e, "disabled"), Bool(e, "indent")));
            }
            if (items.Count == 0) return null;
            var rect = root.TryGetProperty("rect", out var r) && r.ValueKind == JsonValueKind.Object
                ? (Num(r, "x"), Num(r, "y"), Math.Max(0, Num(r, "width")), Math.Max(0, Num(r, "height")))
                : default;
            var selected = root.TryGetProperty("selected", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var i) && i >= 0 && i < items.Count
                ? i : -1;
            return new NightlyPopup(kind.Value, rect, items, selected);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : 0;
}
