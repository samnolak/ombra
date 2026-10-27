using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

/// <summary>
/// One local mark as a chip in the message box (and, for a page mark, a numbered pin on the page): where it is, its
/// optional short note and its status. The same chip serves marks on code (Changes, the editor, a diff in the
/// conversation), on pages and restored ones.
/// </summary>
public sealed partial class MarkViewModel(LocalMark mark, MarkCollection owner) : ObservableObject
{
    public string Id { get; } = mark.Id;
    public CodeAnchor? Code { get; } = mark.Code;
    public PageAnchor? Page { get; } = mark.Page;
    /// <summary>A draft's page comments saved before marks were structured, sent verbatim.</summary>
    public string? Legacy { get; } = mark.Legacy;
    public Uri? PageUri { get; } = mark.Page is { } p && Uri.TryCreate(p.Url, UriKind.Absolute, out var u) ? u : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tooltip), nameof(DisplayNote), nameof(HasNote))]
    private string _note = mark.Note;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsResolved), nameof(Tooltip), nameof(ResolveLabel))]
    private MarkStatus _status = mark.Status;

    [ObservableProperty] private int _number;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editText = "";

    /// <summary>A sketch's picture (the page around it with its strokes), once taken.</summary>
    public ImageAttachment? Image { get; internal set; } = mark.Image;

    public LocalMark Model => new(Id, Note, Status, Code, Page, Legacy, Image);

    public bool IsResolved => Status == MarkStatus.Resolved;
    public bool IsCode => Code is not null;
    public bool IsPage => Page is not null;
    public bool CanShow => Code is not null || PageUri is not null;
    public bool HasNote => Note.Length > 0;
    public string DisplayNote => Note.Length > 0 ? Note : Legacy is not null ? Loc.T("Page comments from a saved draft") : Loc.T("No note");
    public string ResolveLabel => IsResolved ? Loc.T("Reopen") : Loc.T("Resolve");

    /// <summary>"app.py:41", "app.py:3-7", "localhost:5173/about", or "Saved draft".</summary>
    public string Where => Code is { } c
        ? System.IO.Path.GetFileName(c.Path) + (c.Line is { } n ? ":" + n.ToString(CultureInfo.InvariantCulture)
            + (c.EndLine is { } end && end > n ? "-" + end.ToString(CultureInfo.InvariantCulture) : "") : "")
        : PageUri is { } u ? u.Authority + (u.AbsolutePath == "/" ? "" : u.AbsolutePath.TrimEnd('/'))
        : Loc.T("Saved draft");

    /// <summary>What is marked in a few words: the quoted line, or "Point on button — Sign in", "Area 240×120 in section".</summary>
    public string TargetLabel
    {
        get
        {
            if (Code is { } c) return c.LineText.Trim().Split('\n')[0].Trim();
            if (Page is { } p)
            {
                var on = p.Element.Text.Length > 0 ? $"{p.Element.Tag} — {p.Element.Text}" : p.Element.Selector;
                return p.Mark.Kind switch
                {
                    PageMarkKind.Area => Loc.F("Area {0}×{1} in {2}", p.Mark.Width, p.Mark.Height, on),
                    PageMarkKind.Sketch => Loc.F("Sketch {0}×{1} over {2}", p.Mark.Width, p.Mark.Height, on),
                    _ => Loc.F("Point on {0}", on),
                };
            }
            return (Legacy ?? "").Split('\n')[0];
        }
    }

    public string Tooltip
    {
        get
        {
            var where = Code is { } c
                ? $"{c.Path}{(c.Line is { } n ? ", " + (c.EndLine is { } e && e > n ? Loc.F("lines {0}-{1}", n, e) : Loc.F("line {0}", n)) : "")}{(c.RemovedLine ? " (" + Loc.T("removed") + ")" : "")}"
                : Page is { } p ? (p.Title.Length > 0 ? $"{p.Title} — " : "") + Where
                : Loc.T("Page comments from a saved draft");
            return $"{where}\n{TargetLabel}\n\n{DisplayNote}{(IsResolved ? "\n\n" + Loc.T("Resolved: not sent") : "")}";
        }
    }

    [RelayCommand] private void Remove() => owner.Remove(this);

    [RelayCommand]
    private void BeginEdit()
    {
        EditText = Note;
        IsEditing = true;
    }

    [RelayCommand]
    private void SaveEdit()
    {
        IsEditing = false;
        owner.Edit(this, EditText);
    }

    [RelayCommand] private void CancelEdit() => IsEditing = false;

    [RelayCommand] private void Show() => owner.Show(this);

    [RelayCommand] private void ToggleResolved() => owner.SetStatus(this, IsResolved ? MarkStatus.Open : MarkStatus.Resolved);
}

/// <summary>
/// The local marks of the chat shown: one list (numbered like the chips and the page's pins) shared by the message box,
/// the Files pane, the editor, the conversation's diffs and the preview. Open marks go with the next request.
/// </summary>
public sealed partial class MarkCollection : ObservableObject
{
    public ObservableCollection<MarkViewModel> Items { get; } = [];

    public MarkCollection() => Items.CollectionChanged += OnItemsChanged;

    public bool HasMarks => Items.Count > 0;
    public bool HasOpen => Items.Any(m => !m.IsResolved);
    public string Title => Loc.Plural(Items.Count, "1 comment", "{0} comments");
    /// <summary>The message box's button that sends the open marks to the team, with their number.</summary>
    public string SendToTeamLabel => Loc.Plural(Items.Count(m => !m.IsResolved), "Send {0} comment to the team…", "Send {0} comments to the team…");

    /// <summary>A note or status changed (the page's pins show the note; the draft is saved).</summary>
    public event Action? Edited;
    /// <summary>A mark's place was asked for: its file and line, or its page.</summary>
    public event Action<MarkViewModel>? ShowRequested;
    /// <summary>A mark could not be added: the reason, for the message box.</summary>
    public event Action<string>? Refused;

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        for (var i = 0; i < Items.Count; i++) Items[i].Number = i + 1;
        OnPropertyChanged(nameof(HasMarks));
        OnPropertyChanged(nameof(HasOpen));
        OnPropertyChanged(nameof(SendToTeamLabel));
        OnPropertyChanged(nameof(Title));
    }

    /// <summary>Adds a mark at the end; false (and <see cref="Refused"/>) when there are as many as allowed already.</summary>
    public bool Add(LocalMark mark)
    {
        if (Items.Count >= LocalMarks.MaxMarks)
        {
            Refused?.Invoke(Loc.F("At most {0} comments at a time: send or clear some first.", LocalMarks.MaxMarks));
            return false;
        }
        Items.Add(new MarkViewModel(mark with { Note = Cap(mark.Note.Trim()) }, this));
        return true;
    }

    public void Remove(MarkViewModel mark) => Items.Remove(mark);

    /// <summary>The chip or the page changed a note (an empty one changes nothing: × removes a mark).</summary>
    public void Edit(MarkViewModel mark, string text)
    {
        var note = text.Trim();
        if (note.Length == 0 || note == mark.Note) return;
        mark.Note = Cap(note);
        Edited?.Invoke();
    }

    public void SetStatus(MarkViewModel mark, MarkStatus status)
    {
        if (mark.Status == status) return;
        mark.Status = status;
        OnPropertyChanged(nameof(HasOpen));
        OnPropertyChanged(nameof(SendToTeamLabel));
        Edited?.Invoke();
    }

    /// <summary>A sketch's picture was taken (the draft keeps it; the request attaches it).</summary>
    public void SetImage(MarkViewModel mark, ImageAttachment image)
    {
        mark.Image = image;
        Edited?.Invoke();
    }

    public void Show(MarkViewModel mark) => ShowRequested?.Invoke(mark);

    public IReadOnlyList<LocalMark> Models() => [.. Items.Select(m => m.Model)];

    /// <summary>After a request went out: the open marks were sent; resolved ones stay until removed.</summary>
    public void RemoveSent()
    {
        foreach (var m in Items.Where(m => !m.IsResolved).ToList()) Items.Remove(m);
    }

    private static string Cap(string note) => note.Length <= LocalMarks.MaxNote ? note : note[..GraphemeText.PrefixLength(note, LocalMarks.MaxNote)] + "…";
}

/// <summary>Local marks in the message box: added from the Files pane, the editor, the conversation's diffs and the preview.</summary>
public sealed partial class MainViewModel
{
    /// <summary>The chat's marks; the preview shares this list (<see cref="PreviewViewModel.Marks"/>).</summary>
    public MarkCollection Marks => _marks ??= NewMarks();
    private MarkCollection? _marks;

    private MarkCollection NewMarks()
    {
        var marks = new MarkCollection();
        marks.Items.CollectionChanged += (_, _) => OnMarksChanged();
        marks.Edited += OnMarksChanged;
        marks.ShowRequested += OnMarkShowRequested;
        marks.Refused += message => ComposerMessage = message;
        return marks;
    }

    /// <summary>A mark on code goes in the message box as a chip; false when there are too many already.</summary>
    internal bool AddCodeMark(CodeAnchor anchor, string note) => Marks.Add(new LocalMark(LocalMarks.NewId(), note, Code: anchor));

    /// <summary>A line of a change shown in the conversation was commented on (<see cref="Controls.DiffView.CommentCommand"/>).</summary>
    [RelayCommand]
    private void CommentOnChange(Controls.DiffLineComment? c)
    {
        if (c is null || c.Note.Trim().Length == 0) return;
        var path = c.Path;
        if (ProjectRoot is { } root && System.IO.Path.IsPathFullyQualified(path) && Services.ProjectFiles.Relative(root, path) is { Length: > 0 } rel) path = rel;
        if (AddCodeMark(new CodeAnchor(path.Replace('\\', '/'), c.Line, c.Removed, c.Text, CodeMarkOrigin.Conversation, Before: c.Before, After: c.After), c.Note))
            FocusComposerRequested?.Invoke();
    }

    private void OnMarksChanged()
    {
        ComposerInputChanged();
        AttachmentsChanged();
        SendCommand.NotifyCanExecuteChanged();
        SteerCommand.NotifyCanExecuteChanged();
        InterruptAndSendCommand.NotifyCanExecuteChanged();
        SaveDraft();
    }

    private void OnMarkShowRequested(MarkViewModel m)
    {
        if (m.Code is { } c) Files.OpenLinkCommand.Execute(new Controls.FileLink(c.Path, c.Line));
        else if (m.PageUri is not null) Preview.ShowMark(m);
    }
}
