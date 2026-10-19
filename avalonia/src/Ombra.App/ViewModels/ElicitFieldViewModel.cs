using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Ombra.App.Localization;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

/// <summary>
/// One field of a connector's form (MCP elicitation): its editor by kind and the same checks omp runs before it
/// passes the answer on (mcp/elicitation.ts), so a mistake is shown here instead of failing the server's request.
/// Values stay in memory only: they are sent to omp and never written to a log or the conversation.
/// </summary>
public sealed partial class ElicitFieldViewModel : ObservableObject
{
    private static readonly Regex Email = new(@"^[^\s@]+@[^\s@]+\.[^\s@]+$", RegexOptions.CultureInvariant);
    private static readonly Regex Date = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant);

    public ElicitFieldViewModel(ElicitField field)
    {
        Field = field;
        Options = [.. field.Options.Select(o => new ElicitOptionViewModel(o, this))];
        switch (field.Kind)
        {
            case "boolean":
                _isChecked = field.Default is JsonValue b && b.TryGetValue<bool>(out var on) && on;
                break;
            case "select":
                _selected = field.Default is JsonValue s && s.TryGetValue<string>(out var v) ? Options.FirstOrDefault(o => o.Value == v) : null;
                break;
            case "multiselect":
                if (field.Default is JsonArray picked)
                    foreach (var o in Options) o.IsChecked = picked.Any(p => p is JsonValue pv && pv.TryGetValue<string>(out var x) && x == o.Value);
                break;
            default:
                _text = field.Default switch
                {
                    JsonValue d when d.TryGetValue<string>(out var t) => t,
                    JsonValue d when d.TryGetValue<double>(out var n) => n.ToString(CultureInfo.InvariantCulture),
                    _ => "",
                };
                break;
        }
    }

    public ElicitField Field { get; }
    public string Name => Field.Name;
    public string Label => (Field.Title.Length > 0 ? Field.Title : Field.Name) + (Field.Required ? " *" : "");
    public string Description => Field.Description ?? "";
    public bool HasDescription => Description.Length > 0;
    public bool IsText => Field.Kind is "text" or "number" or "integer";
    public bool IsBoolean => Field.Kind == "boolean";
    public bool IsSelect => Field.Kind == "select";
    public bool IsMultiSelect => Field.Kind == "multiselect";
    public IReadOnlyList<ElicitOptionViewModel> Options { get; }

    public string Placeholder => Field.Kind switch
    {
        "integer" => Loc.T("A whole number"),
        "number" => Loc.T("A number"),
        _ => Field.Format switch
        {
            "email" => "name@example.com",
            "uri" => "https://…",
            "date" => "YYYY-MM-DD",
            "date-time" => "YYYY-MM-DDThh:mm:ssZ",
            _ => "",
        },
    };

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private ElicitOptionViewModel? _selected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _error = "";

    public bool HasError => Error.Length > 0;

    partial void OnTextChanged(string value) => Error = "";
    partial void OnSelectedChanged(ElicitOptionViewModel? value) => Error = "";
    internal void OptionToggled() => Error = "";

    /// <summary>Checks the value; on success puts it in <paramref name="content"/> (nothing for an empty optional field).</summary>
    internal bool TryAdd(JsonObject content)
    {
        Error = Check(out var value) ?? "";
        if (Error.Length > 0) return false;
        if (value is not null) content[Name] = value;
        return true;
    }

    private string? Check(out JsonNode? value)
    {
        value = null;
        var f = Field;
        switch (f.Kind)
        {
            case "boolean":
                value = JsonValue.Create(IsChecked);
                return null;
            case "select":
                if (Selected is null) return f.Required ? Loc.T("Choose one.") : null;
                value = JsonValue.Create(Selected.Value);
                return null;
            case "multiselect":
            {
                var picked = Options.Where(o => o.IsChecked).Select(o => o.Value).ToList();
                if (picked.Count == 0 && !f.Required && f.MinItems is null or 0) return null;
                if (f.MinItems is { } min && picked.Count < min) return Loc.Plural(min, "Choose at least {0}.", "Choose at least {0}.");
                if (picked.Count == 0) return Loc.T("Choose at least one.");
                if (f.MaxItems is { } max && picked.Count > max) return Loc.Plural(max, "Choose at most {0}.", "Choose at most {0}.");
                value = new JsonArray([.. picked.Select(p => (JsonNode)JsonValue.Create(p))]);
                return null;
            }
            case "number" or "integer":
            {
                var t = Text.Trim();
                if (t.Length == 0) return f.Required ? Loc.T("Required.") : null;
                if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                    && !double.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out n) || !double.IsFinite(n))
                    return Loc.T("Enter a number.");
                if (f.Kind == "integer" && n != Math.Floor(n)) return Loc.T("Enter a whole number.");
                if (f.Minimum is { } lo && n < lo) return Loc.F("At least {0}.", lo.ToString(CultureInfo.CurrentCulture));
                if (f.Maximum is { } hi && n > hi) return Loc.F("At most {0}.", hi.ToString(CultureInfo.CurrentCulture));
                value = f.Kind == "integer" && Math.Abs(n) < 9e15 ? JsonValue.Create((long)n) : JsonValue.Create(n);
                return null;
            }
            default:
            {
                var t = Text;
                if (t.Length == 0) return f.Required ? Loc.T("Required.") : null;
                if (f.MinLength is { } min && t.Length < min) return Loc.Plural(min, "At least {0} character.", "At least {0} characters.");
                if (f.MaxLength is { } max && t.Length > max) return Loc.Plural(max, "At most {0} character.", "At most {0} characters.");
                var formatOk = f.Format switch
                {
                    "email" => Email.IsMatch(t),
                    "uri" => Uri.TryCreate(t, UriKind.Absolute, out _),
                    "date" => Date.IsMatch(t) && DateOnly.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
                    "date-time" => !Date.IsMatch(t) && DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
                    _ => true,
                };
                if (!formatOk)
                    return f.Format switch
                    {
                        "email" => Loc.T("Enter an email address."),
                        "uri" => Loc.T("Enter a full address (https://…)."),
                        "date" => Loc.T("Enter a date as YYYY-MM-DD."),
                        _ => Loc.T("Enter a date and time (YYYY-MM-DDThh:mm:ssZ)."),
                    };
                value = JsonValue.Create(t);
                return null;
            }
        }
    }
}

/// <summary>A choice of a form field (a select's item, or a checkbox of a multi-select).</summary>
public sealed partial class ElicitOptionViewModel(ElicitOption option, ElicitFieldViewModel owner) : ObservableObject
{
    public string Value => option.Value;
    public string Label => option.Label;

    [ObservableProperty] private bool _isChecked;

    partial void OnIsCheckedChanged(bool value) => owner.OptionToggled();

    public override string ToString() => Label;
}
