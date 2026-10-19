using CommunityToolkit.Mvvm.ComponentModel;
using Ombra.App.Localization;

namespace Ombra.App.ViewModels;

/// <summary>What the command palette lists: everything (⌘K), or the project's files only (⌘P).</summary>
public enum PaletteMode { All, Files }

/// <summary>What a palette entry is, in the order an empty palette lists the kinds.</summary>
public enum PaletteKind { Command, Panel, Settings, Theme, Chat, File }

/// <summary>
/// One entry of the command palette (MainViewModel.Palette.cs): an action of the window, a panel, a settings page, a
/// theme, a chat or a file. Running it runs the window's own command (or opens the chat, the file); one that cannot run
/// now is listed greyed, with why.
/// </summary>
public sealed partial class PaletteItemViewModel(string id, PaletteKind kind, string title, Func<Task> run) : ObservableObject
{
    /// <summary>Stable across openings ("settings:permissions", "chat:/path/file.jsonl"): what the recent list keeps.</summary>
    public string Id { get; } = id;
    public PaletteKind Kind { get; } = kind;
    public string Title { get; } = title;
    /// <summary>The title in English when the UI shows another language: typed English finds the entry too.</summary>
    public string? EnglishTitle { get; init; }
    /// <summary>Said after the title: a chat's project and age, a file's folder.</summary>
    public string Detail { get; init; } = "";
    /// <summary>The key that does the same outside the palette ("⇧⌘F").</summary>
    public string? Shortcut { get; init; }
    public bool HasShortcut => Shortcut is { Length: > 0 };
    /// <summary>A chat whose reply came while it was not shown.</summary>
    public bool IsUnread { get; init; }
    /// <summary>Whether it can run now (null: always); read again each time the palette lists it.</summary>
    internal Func<bool>? CanRun { get; init; }
    /// <summary>Why it cannot run now (listed in place of the detail).</summary>
    internal string? DisabledReason { get; init; }
    internal Func<Task> Run { get; } = run;

    public string KindLabel => Kind switch
    {
        PaletteKind.Panel => Loc.T("Panel"),
        PaletteKind.Settings => Loc.T("Settings"),
        PaletteKind.Theme => Loc.T("Appearance"),
        PaletteKind.Chat => Loc.T("Chat"),
        PaletteKind.File => Loc.T("File"),
        _ => Loc.T("Command"),
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailText), nameof(HasDetail))]
    private bool _isEnabled = true;

    /// <summary>The detail, or why it cannot run now.</summary>
    public string DetailText => IsEnabled ? Detail : DisabledReason ?? Loc.T("Not available right now");
    public bool HasDetail => DetailText.Length > 0;

    /// <summary>The characters of <see cref="Title"/> the typed text matched (highlighted).</summary>
    [ObservableProperty]
    private IReadOnlyList<int> _matches = [];

    internal void Refresh() => IsEnabled = CanRun?.Invoke() ?? true;
}

/// <summary>
/// How well a palette entry's title matches what was typed. Four tiers, each above every score of the next: the title
/// starts with the text; every typed word starts a word of the title, in order ("set perm" → "Settings: Permissions");
/// the text is inside the title; the typed letters are in the title in order, scattered. Within a tier, matches at the
/// start, on word starts and in runs rank higher, shorter titles before longer ones.
/// </summary>
public static class PaletteMatch
{
    private const int Prefix = 5000, Words = 3500, Substring = 2700, Scattered = 1000, ScatteredMax = 2500;

    /// <summary>The score (higher is better), or -1 when <paramref name="text"/> does not match; 0 for an empty query.
    /// <paramref name="positions"/> receives the matched characters' indexes in <paramref name="text"/>.</summary>
    public static int Score(string text, string query, List<int>? positions = null)
    {
        positions?.Clear();
        var q = query.Trim();
        if (q.Length == 0) return 0;
        if (text.StartsWith(q, StringComparison.OrdinalIgnoreCase))
        {
            AddRange(positions, text, 0, q.Length);
            return Prefix + 900 - Math.Min(text.Length - q.Length, 900);
        }
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (WordStarts(text, words, positions) is { } span)
            return Words + (span.First == 0 ? 500 : 0) + 499 - Math.Min(span.Gaps, 499);
        positions?.Clear();
        var at = text.IndexOf(q, StringComparison.OrdinalIgnoreCase);
        if (at >= 0)
        {
            AddRange(positions, text, at, q.Length);
            return Substring + (IsWordStart(text, at) ? 300 : 0) + 199 - Math.Min(at, 199);
        }
        return ScatteredScore(text, string.Concat(words), positions);
    }

    /// <summary>Each word at the start of a word of <paramref name="text"/>, in order: where the first one is and how
    /// many characters lie between the matched words.</summary>
    private static (int First, int Gaps)? WordStarts(string text, string[] words, List<int>? positions)
    {
        var from = 0;
        var first = -1;
        var gaps = 0;
        foreach (var word in words)
        {
            var found = -1;
            for (var i = from; i <= text.Length - word.Length; i++)
            {
                if (IsWordStart(text, i) && string.Compare(text, i, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    found = i;
                    break;
                }
            }
            if (found < 0) return null;
            if (first < 0) first = found;
            else gaps += found - from;
            AddRange(positions, text, found, word.Length);
            from = found + word.Length;
        }
        return (first, gaps);
    }

    /// <summary>The typed letters in order anywhere in the text: the best of the alignments that start at each place
    /// the first letter is (each then taking the earliest next letter).</summary>
    private static int ScatteredScore(string text, string letters, List<int>? positions)
    {
        if (letters.Length == 0 || letters.Length > text.Length) return -1;
        var best = -1;
        List<int>? bestAt = null;
        var at = new List<int>(letters.Length);
        for (var start = 0; start <= text.Length - letters.Length; start++)
        {
            if (char.ToUpperInvariant(text[start]) != char.ToUpperInvariant(letters[0])) continue;
            at.Clear();
            at.Add(start);
            var i = start + 1;
            for (var k = 1; k < letters.Length; k++)
            {
                while (i < text.Length && char.ToUpperInvariant(text[i]) != char.ToUpperInvariant(letters[k])) i++;
                if (i == text.Length) break;
                at.Add(i++);
            }
            if (at.Count < letters.Length) break; // a later start cannot find what this one did not
            var score = Scattered;
            for (var k = 0; k < at.Count; k++)
            {
                if (IsWordStart(text, at[k])) score += 40;
                if (k > 0 && at[k] == at[k - 1] + 1) score += 20;
                if (k > 0) score -= Math.Min(at[k] - at[k - 1] - 1, 20);
            }
            score -= Math.Min(start, 100);
            if (score > best)
            {
                best = score;
                bestAt = [.. at];
            }
        }
        if (best < 0) return -1;
        positions?.AddRange(bestAt!);
        return Math.Clamp(best, 1, ScatteredMax);
    }

    /// <summary>The start of the text, after a character that is not a letter or digit, or a capital after a small letter.</summary>
    internal static bool IsWordStart(string text, int i) =>
        i == 0 || !char.IsLetterOrDigit(text[i - 1]) || char.IsUpper(text[i]) && char.IsLower(text[i - 1]);

    private static void AddRange(List<int>? positions, string text, int start, int length)
    {
        if (positions is null) return;
        for (var i = start; i < start + length && i < text.Length; i++)
            if (!char.IsWhiteSpace(text[i])) positions.Add(i);
    }
}
