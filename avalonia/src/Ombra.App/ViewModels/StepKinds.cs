using Ombra.App.Localization;

namespace Ombra.App.ViewModels;

/// <summary>What a tool call is, as a step of a turn: its icon on its line, and its words in a group's line.</summary>
public enum StepKind { Other, Command, Read, Image, Search, List, Edit, WebSearch, Fetch, Browser, Agent, Plan, Ask, Code }

/// <summary>
/// The kinds of steps (Codex's step rows: "Ran commands", "Read files", "Viewed images", "Searched the web") from the
/// tools omp runs, and the icon of each.
/// </summary>
public static class StepKinds
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg", ".ico", ".tif", ".tiff", ".heic", ".avif",
    };

    public static StepKind Of(ToolRowViewModel step) => step.Name switch
    {
        "bash" or "shell" => StepKind.Command,
        "read" => ImageExtensions.Contains(System.IO.Path.GetExtension(step.Summary)) ? StepKind.Image : StepKind.Read,
        "grep" or "ast_grep" or "glob" or "find" or "lsp" => StepKind.Search,
        "ls" => StepKind.List,
        "edit" or "ast_edit" or "notebook_edit" or "write" or "create" or "write_file" => StepKind.Edit,
        "web_search" => StepKind.WebSearch,
        "fetch" => StepKind.Fetch,
        "browser" => StepKind.Browser,
        "task" => StepKind.Agent,
        "todo" => StepKind.Plan,
        "ask" => StepKind.Ask,
        "eval" => StepKind.Code,
        _ => StepKind.Other,
    };

    /// <summary>The icon resource of a kind.</summary>
    public static string IconKey(StepKind kind) => kind switch
    {
        StepKind.Command => "IconTerminal",
        StepKind.Read => "IconFileText",
        StepKind.Image => "IconImage",
        StepKind.Search => "IconSearch",
        StepKind.List => "IconFolder",
        StepKind.Edit => "IconPencil",
        StepKind.WebSearch or StepKind.Fetch or StepKind.Browser => "IconGlobe",
        StepKind.Agent => "IconLayers",
        StepKind.Plan => "IconChecklist",
        StepKind.Ask => "IconComment",
        StepKind.Code => "IconCode",
        _ => "IconTools",
    };

    public static readonly Avalonia.Data.Converters.IValueConverter IconConverter =
        new Avalonia.Data.Converters.FuncValueConverter<StepKind, object?>(kind =>
            Avalonia.Application.Current is { } app && app.TryGetResource(IconKey(kind), app.ActualThemeVariant, out var icon) ? icon : null);

    /// <summary>
    /// What a run of steps did, in Codex's words: one phrase per kind, in the order the kinds came, singular for one call
    /// (one file, one command) and plural for more — "Read files, ran a command". Words, not counts, so every language
    /// says them without plural rules.
    /// </summary>
    public static string Describe(IReadOnlyList<ToolRowViewModel> steps)
    {
        var order = new List<StepKind>();
        var many = new Dictionary<StepKind, HashSet<string>>();
        foreach (var s in steps)
        {
            if (!many.TryGetValue(s.Kind, out var seen))
            {
                order.Add(s.Kind);
                many[s.Kind] = seen = new HashSet<string>(StringComparer.Ordinal);
            }
            // Files count once each, however often they were read or changed; other calls count each time
            seen.Add(s.Kind is StepKind.Read or StepKind.Image or StepKind.Edit ? s.FilePath : s.Key.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        var phrases = order.Select(k => Phrase(k, many[k].Count > 1)).ToList();
        for (var i = 1; i < phrases.Count; i++)
            if (phrases[i].Length > 0) phrases[i] = char.ToLower(phrases[i][0], System.Globalization.CultureInfo.CurrentCulture) + phrases[i][1..];
        return string.Join(", ", phrases);
    }

    private static string Phrase(StepKind kind, bool many) => kind switch
    {
        StepKind.Command => many ? Loc.T("Ran commands") : Loc.T("Ran a command"),
        StepKind.Read => many ? Loc.T("Read files") : Loc.T("Read a file"),
        StepKind.Image => many ? Loc.T("Viewed images") : Loc.T("Viewed an image"),
        StepKind.Search => Loc.T("Searched the code"),
        StepKind.List => many ? Loc.T("Listed folders") : Loc.T("Listed a folder"),
        StepKind.Edit => many ? Loc.T("Edited files") : Loc.T("Edited a file"),
        StepKind.WebSearch => Loc.T("Searched the web"),
        StepKind.Fetch => many ? Loc.T("Opened pages") : Loc.T("Opened a page"),
        StepKind.Browser => Loc.T("Used the browser"),
        StepKind.Agent => many ? Loc.T("Ran subagents") : Loc.T("Ran a subagent"),
        StepKind.Plan => Loc.T("Updated the plan"),
        StepKind.Ask => Loc.T("Asked a question"),
        StepKind.Code => Loc.T("Ran code"),
        _ => many ? Loc.T("Used tools") : Loc.T("Used a tool"),
    };
}
