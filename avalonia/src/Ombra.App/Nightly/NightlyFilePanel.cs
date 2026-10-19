using System.Text.Json;
using Avalonia.Platform.Storage;

namespace Ombra.App.Nightly;

/// <summary>
/// The system open and save panels of the Browser page's Nightly window (desktop <c>ZenEmbedDialogs.sys.mjs</c>, question
/// kind <c>panel</c>): a page's file input, a download Nightly asks where to save, Save Page As, Save to PDF, imports.
/// Nightly's own picker would be a sheet on its window, which is never on screen, so Ombra shows the panel as a sheet on
/// its own window (Avalonia's storage provider) and answers with the chosen paths: <c>{paths: [...] | null, filterIndex}</c>.
/// </summary>
internal static class NightlyFilePanel
{
    public enum PanelMode { Open, Multiple, Folder, Save }

    /// <summary>One of the picker's filters: <c>*.ext</c> patterns (<c>*</c> any file) and MIME types.</summary>
    public sealed record Filter(string Title, IReadOnlyList<string> Patterns, IReadOnlyList<string> MimeTypes)
    {
        public bool AnyFile => Patterns.Contains("*");
    }

    public sealed record Request(PanelMode Mode, string Title, string Name, string Extension, string? Directory, int FilterIndex,
        IReadOnlyList<Filter> Filters);

    /// <summary>The question as a panel request; null for any other question (or a bad one).</summary>
    public static Request? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var q = doc.RootElement;
            if (q.ValueKind != JsonValueKind.Object || Str(q, "kind") != "panel") return null;
            var mode = Str(q, "mode") switch
            {
                "multiple" => PanelMode.Multiple,
                "folder" => PanelMode.Folder,
                "save" => PanelMode.Save,
                _ => PanelMode.Open,
            };
            var filters = new List<Filter>();
            if (q.TryGetProperty("filters", out var fs) && fs.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in fs.EnumerateArray())
                {
                    if (f.ValueKind != JsonValueKind.Object) continue;
                    var patterns = Strings(f, "patterns");
                    var mimes = Strings(f, "mimeTypes");
                    if (patterns.Count > 0 || mimes.Count > 0) filters.Add(new Filter(Str(f, "title") ?? "", patterns, mimes));
                }
            }
            var index = q.TryGetProperty("filterIndex", out var i) && i.ValueKind == JsonValueKind.Number && i.TryGetInt32(out var n) ? n : 0;
            return new Request(mode, Str(q, "title") ?? "", Str(q, "name") ?? "", Str(q, "extension") ?? "",
                Str(q, "directory") is { Length: > 0 } d ? d : null, index, MergeUntitled(filters));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A page's accept list comes as untitled filters (one per <c>.ext</c> or MIME type): one "Accepted files" entry.</summary>
    private static List<Filter> MergeUntitled(List<Filter> filters)
    {
        var untitled = filters.Where(f => f.Title.Length == 0).ToList();
        if (untitled.Count == 0) return filters;
        var merged = new Filter(Localization.Loc.T("Accepted files"), [.. untitled.SelectMany(f => f.Patterns).Distinct()],
            [.. untitled.SelectMany(f => f.MimeTypes).Distinct()]);
        var result = filters.Where(f => f.Title.Length > 0).ToList();
        result.Insert(0, merged);
        return result;
    }

    /// <summary>The panel's file types: none (any file) when nothing limits them, or when an open panel may take any file.</summary>
    public static List<FilePickerFileType>? FileTypes(Request request)
    {
        if (request.Filters.Count == 0) return null;
        if (request.Mode != PanelMode.Save && request.Filters.Any(f => f.AnyFile)) return null;
        return [.. request.Filters.Select(f => new FilePickerFileType(f.Title)
        {
            Patterns = f.Patterns.Count > 0 ? [.. f.Patterns] : null,
            MimeTypes = f.MimeTypes.Count > 0 ? [.. f.MimeTypes] : null,
        })];
    }

    /// <summary>Shows the panel as a sheet on <paramref name="storage"/>'s window and returns Nightly's answer.</summary>
    public static async Task<string> ShowAsync(IStorageProvider storage, Request request)
    {
        var start = request.Directory is { } dir && Directory.Exists(dir) ? await storage.TryGetFolderFromPathAsync(dir) : null;
        var types = FileTypes(request);
        switch (request.Mode)
        {
            case PanelMode.Save:
            {
                var options = new FilePickerSaveOptions
                {
                    Title = request.Title,
                    SuggestedFileName = request.Name,
                    DefaultExtension = request.Extension.Length > 0 ? request.Extension : null,
                    FileTypeChoices = types,
                    SuggestedFileType = types is { Count: > 0 } ? types[Math.Clamp(request.FilterIndex, 0, types.Count - 1)] : null,
                    SuggestedStartLocation = start,
                    ShowOverwritePrompt = true,
                };
                var result = await storage.SaveFilePickerWithResultAsync(options);
                var index = types is not null && result.SelectedFileType is { } chosen ? types.IndexOf(chosen) : -1;
                return Answer(result.File?.TryGetLocalPath() is { } path ? [path] : [], index);
            }
            case PanelMode.Folder:
            {
                var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = request.Title, SuggestedStartLocation = start });
                return Answer([.. folders.Select(f => f.TryGetLocalPath()).OfType<string>()], -1);
            }
            default:
            {
                var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = request.Title,
                    AllowMultiple = request.Mode == PanelMode.Multiple,
                    FileTypeFilter = types,
                    SuggestedStartLocation = start,
                });
                return Answer([.. files.Select(f => f.TryGetLocalPath()).OfType<string>()], -1);
            }
        }
    }

    /// <summary><c>{paths, filterIndex}</c>; no paths: cancelled (<c>paths: null</c>).</summary>
    public static string Answer(IReadOnlyList<string> paths, int filterIndex)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            if (paths.Count == 0) w.WriteNull("paths");
            else
            {
                w.WriteStartArray("paths");
                foreach (var p in paths) w.WriteStringValue(p);
                w.WriteEndArray();
            }
            if (filterIndex >= 0) w.WriteNumber("filterIndex", filterIndex);
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static List<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.String).Select(s => s.GetString()!).Where(s => s.Length > 0)]
            : [];
}
