using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using AvaloniaEdit.TextMate;
using Ombra.App.Localization;
using Ombra.App.Services.Documents;
using TextMateSharp.Grammars;

namespace Ombra.App.Controls;

/// <summary>A native view onto the workspace's single, UI-thread-owned document buffer.</summary>
public sealed partial class DocumentEditor : UserControl
{
    public static readonly StyledProperty<WorkspaceDocument?> WorkspaceDocumentProperty =
        AvaloniaProperty.Register<DocumentEditor, WorkspaceDocument?>(nameof(WorkspaceDocument));
    public static readonly StyledProperty<int?> TargetLineProperty =
        AvaloniaProperty.Register<DocumentEditor, int?>(nameof(TargetLine));
    public static readonly StyledProperty<EditorLanguageService?> LanguageServiceProperty =
        AvaloniaProperty.Register<DocumentEditor, EditorLanguageService?>(nameof(LanguageService));

    public WorkspaceDocument? WorkspaceDocument { get => GetValue(WorkspaceDocumentProperty); set => SetValue(WorkspaceDocumentProperty, value); }
    public int? TargetLine { get => GetValue(TargetLineProperty); set => SetValue(TargetLineProperty, value); }
    public EditorLanguageService? LanguageService { get => GetValue(LanguageServiceProperty); set => SetValue(LanguageServiceProperty, value); }
    public event Action<string, int>? NavigateRequested;
    public TextEditor Editor { get; } = new()
    {
        Name = "NativeTextEditor", ShowLineNumbers = true,
        FontFamily = Application.Current?.TryFindResource("GuiFontMono", out var font) == true && font is FontFamily mono
            ? mono : new FontFamily("SF Mono, Menlo, Cascadia Mono, Consolas, DejaVu Sans Mono, monospace"),
        FontSize = 13, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch
    };
    private readonly TextBlock _status = new() { Name = "EditorStatus", Margin = new Thickness(6), TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _language = new() { Name = "EditorLanguage", MinWidth = 110, MaxWidth = 180 };
    private readonly RegistryOptions _registry = new(ThemeName.DarkPlus);
    private readonly DispatcherTimer _foldTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly TextDocument _empty = new();
    private TextMate.Installation? _textMate;
    private FoldingManager? _folding;
    private WorkspaceDocument? _subscribedDocument;
    private EditorLanguageService? _subscribedService;
    private bool _attached;
    private bool _settingLanguage;
    private long _foldRevision = -1;
    private string? _grammarLanguage;
    private int? _pendingTargetLine;

    public DocumentEditor()
    {
        Editor.Options.EnableRectangularSelection = true;
        Editor.Options.ConvertTabsToSpaces = true;
        Editor.Options.IndentationSize = 4;
        Editor.Document = _empty;
        Editor.TextArea.EditRejected += ShowError;
        Editor.ContextFlyout = ContextMenus.Menu(
            new MenuAction(Loc.T("Cut"), "IconCut", _ => { Editor.TextArea.Focus(); Editor.Cut(); },
                () => !Editor.TextArea.Selection.IsEmpty && Editor.CanCut),
            new MenuAction(Loc.T("Copy"), "IconCopy", _ => { Editor.TextArea.Focus(); Editor.Copy(); },
                () => !Editor.TextArea.Selection.IsEmpty && Editor.CanCopy),
            new MenuAction(Loc.T("Paste"), "IconPaste", _ => { Editor.TextArea.Focus(); Editor.Paste(); },
                () => Editor.CanPaste),
            null,
            new MenuAction(Loc.T("Comment…"), "IconComment", _ => BeginMark(), () => WorkspaceDocument is not null),
            new MenuAction(Loc.T("Share with team…"), "IconShare", _ => BeginShare(), () => WorkspaceDocument is not null),
            null,
            new MenuAction(Loc.T("Format document"), "IconCode", async _ => await FormatAsync(false),
                () => FormatUnavailableReason(false) is null, () => FormatUnavailableReason(false)),
            new MenuAction(Loc.T("Format selection"), "IconCode", async _ => await FormatAsync(true),
                () => FormatUnavailableReason(true) is null, () => FormatUnavailableReason(true)),
            null,
            new MenuAction(Loc.T("Select all"), "IconSelect", _ => { Editor.TextArea.Focus(); Editor.SelectAll(); }));
        ToolTip.SetTip(Editor, "Multiple carets: Ctrl+Alt-click. Column editing: Alt/Option-drag. Escape returns to one caret.");
        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };
        var editActions = new ComboBox
        {
            Name = "EditorEditAction", PlaceholderText = "Edit", MinWidth = 75,
            ItemsSource = new[] { "Find", "Replace", "Go to line", "Undo", "Redo", "Fold", Loc.T("Share with team…") }
        };
        editActions.SelectionChanged += (_, _) =>
        {
            if (editActions.SelectedItem is not string action) return;
            editActions.SelectedIndex = -1;
            switch (action)
            {
                case "Find": ShowSearch(false); break;
                case "Replace": ShowSearch(true); break;
                case "Go to line": ShowGoToLine(); break;
                case "Undo": Editor.Undo(); break;
                case "Redo": Editor.Redo(); break;
                case "Fold": ToggleFold(); break;
                default: if (action == Loc.T("Share with team…")) BeginShare(); break;
            }
        };
        toolbar.Children.Add(editActions);
        BuildNavigationUi(toolbar);
        var languages = _registry.GetAvailableLanguages().Select(x => x.Id).Distinct().OrderBy(x => x).Prepend("plaintext").ToArray();
        _language.ItemsSource = languages;
        _language.SelectionChanged += (_, _) =>
        {
            if (!_settingLanguage && WorkspaceDocument is { } document && _language.SelectedItem is string id)
            {
                document.LanguageId = id;
                ApplyGrammar();
            }
        };
        ToolTip.SetTip(_language, "Language mode (manual override)");
        toolbar.Children.Add(_language);
        var languageActions = new ComboBox
        {
            Name = "EditorLanguageAction", PlaceholderText = "Code actions", MinWidth = 120,
            ItemsSource = new[] { "Completion", "Hover", "Definition", "Rename", "Diagnostics" }
        };
        languageActions.SelectionChanged += async (_, _) =>
        {
            if (languageActions.SelectedItem is not string action) return;
            languageActions.SelectedIndex = -1;
            if (action == "Rename") ShowRename();
            else await RequestLanguageAsync(action.ToLowerInvariant());
        };
        toolbar.Children.Add(languageActions);
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto,Auto") };
        body.Children.Add(toolbar);
        Grid.SetRow(Editor, 1);
        body.Children.Add(Editor);
        BuildLanguageUi(body);
        BuildFormatUi();
        Grid.SetRow(_status, 4);
        body.Children.Add(_status);
        Content = body;
        _foldTimer.Tick += (_, _) => { _foldTimer.Stop(); RefreshFoldings(); };
        AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
        AttachedToVisualTree += (_, _) => AttachEditor();
        DetachedFromVisualTree += (_, _) => DetachEditor();
        ActualThemeVariantChanged += (_, _) => ApplyTheme();
        Editor.LayoutUpdated += (_, _) => CompleteTargetNavigation();
    }

    private static void AddButton(Panel panel, string label, Action action)
    {
        var button = new Button { Content = label, Margin = new Thickness(1), Padding = new Thickness(6, 3) };
        button.Click += (_, _) => action();
        panel.Children.Add(button);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WorkspaceDocumentProperty) SetDocument();
        else if (change.Property == TargetLineProperty) GoToTargetLine();
        else if (change.Property == LanguageServiceProperty) SubscribeLanguageService();
        else if (change.Property == LineChangesProperty) ChangeMargin.SetChanges(LineChanges ?? []);
    }

    private void AttachEditor()
    {
        _attached = true;
        _textMate = Editor.InstallTextMate(_registry, exceptionHandler: error => Dispatcher.UIThread.Post(() => ShowError(error)));
        SetDocument();
        SubscribeLanguageService();
        ApplyTheme();
    }

    private void DetachEditor()
    {
        _attached = false;
        UnsubscribeDocument();
        SubscribeLanguageService();
        CancelLanguageRequest();
        _completion?.Close();
        _hover.Hide();
        _foldTimer.Stop();
        if (_folding is not null) FoldingManager.Uninstall(_folding);
        _folding = null;
        _textMate?.Dispose();
        _textMate = null;
        _grammarLanguage = null;
    }

    private void UnsubscribeDocument()
    {
        if (_subscribedDocument is not null) _subscribedDocument.Changed -= DocumentChanged;
        _subscribedDocument = null;
    }

    private void SetDocument()
    {
        UnsubscribeDocument();
        CancelLanguageRequest();
        _completion?.Close();
        _hover.Hide();
        _renamePanel.IsVisible = false;
        SetDiagnostics(default);
        if (_folding is not null) FoldingManager.Uninstall(_folding);
        _folding = null;
        _foldRevision = -1;
        _grammarLanguage = null;
        Editor.Document = WorkspaceDocument?.Buffer ?? _empty;
        if (_attached && WorkspaceDocument is { } document)
        {
            _subscribedDocument = document;
            document.Changed += DocumentChanged;
            _folding = FoldingManager.Install(Editor.TextArea);
            if (string.IsNullOrEmpty(document.LanguageId))
                document.LanguageId = DetectLanguage(document.FullPath, document.Buffer);
        }
        UpdateDocumentState();
        ApplyGrammar();
        RefreshFoldings();
        GoToTargetLine();
        LoadDiagnostics();
        _ = RefreshCapabilitiesAsync();
    }

    private void DocumentChanged(WorkspaceDocument document)
    {
        if (!ReferenceEquals(document, WorkspaceDocument)) return;
        UpdateDocumentState();
        ApplyGrammar();
        if (_foldRevision != document.Revision)
        {
            _foldTimer.Stop();
            _foldTimer.Start();
            SetDiagnostics(default);
        }
    }

    private void UpdateDocumentState()
    {
        Editor.IsReadOnly = WorkspaceDocument is not { IsReadOnly: false };
        _status.Text = WorkspaceDocument?.Status ?? "No document";
    }

    private void ApplyGrammar()
    {
        if (_textMate is null || WorkspaceDocument is not { } document || _grammarLanguage == document.LanguageId) return;
        _grammarLanguage = document.LanguageId;
        _settingLanguage = true;
        _language.SelectedItem = document.LanguageId;
        _settingLanguage = false;
        _textMate.SetGrammar(_registry.GetScopeByLanguageId(document.LanguageId));
    }

    private void ApplyTheme()
    {
        if (_textMate is null) return;
        _textMate.SetTheme(_registry.LoadTheme(ActualThemeVariant == ThemeVariant.Light ? ThemeName.LightPlus : ThemeName.DarkPlus));
        if (_textMate.TryGetThemeColor("editor.background", out var background)) Editor.Background = Brush.Parse(background);
        if (_textMate.TryGetThemeColor("editor.foreground", out var foreground)) Editor.Foreground = Brush.Parse(foreground);
        if (_textMate.TryGetThemeColor("editor.selectionBackground", out var selection)) Editor.TextArea.SelectionBrush = Brush.Parse(selection);
    }

    internal string DetectLanguage(string path, TextDocument buffer)
    {
        var language = _registry.GetLanguageByExtension(Path.GetExtension(path));
        if (language is not null) return language.Id;
        var filename = Path.GetFileName(path);
        if (filename.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)) return "dockerfile";
        if (filename.Equals("Makefile", StringComparison.OrdinalIgnoreCase)) return "makefile";
        if (buffer.LineCount == 0) return "plaintext";
        var first = buffer.GetText(buffer.GetLineByNumber(1));
        if (!first.StartsWith("#!", StringComparison.Ordinal)) return "plaintext";
        var parts = first[2..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var executable = Path.GetFileName(part);
            if (executable.StartsWith("python", StringComparison.Ordinal)) return "python";
            if (executable is "node" or "nodejs" or "bun" or "deno") return "javascript";
            if (executable is "bash" or "sh" or "zsh" or "fish" or "ksh") return "shellscript";
            if (executable.StartsWith("ruby", StringComparison.Ordinal)) return "ruby";
            if (executable.StartsWith("perl", StringComparison.Ordinal)) return "perl";
            if (executable is "pwsh" or "powershell") return "powershell";
        }
        return "plaintext";
    }

    internal void GoToTargetLine()
    {
        _pendingTargetLine = null;
        if (TargetLine is not { } requested || WorkspaceDocument is not { } document) return;
        var line = Math.Clamp(requested, 1, document.Buffer.LineCount);
        Editor.TextArea.Caret.Line = line;
        Editor.TextArea.Caret.Column = 1;
        if (_folding is not null)
            foreach (var fold in _folding.AllFoldings)
                if (fold.StartOffset < Editor.CaretOffset && Editor.CaretOffset < fold.EndOffset) fold.IsFolded = false;
        // ScrollToLine requires an arranged native editor. Binding can arrive
        // before its template/layout, or while the Files/review view is hidden.
        _pendingTargetLine = line;
        CompleteTargetNavigation();
    }

    private void CompleteTargetNavigation()
    {
        if (_pendingTargetLine is not { } line || !_attached || !Editor.IsEffectivelyVisible
            || Editor.TextArea.TextView.Bounds.Height <= 0) return;
        _pendingTargetLine = null;
        Editor.ScrollToLine(line);
    }

    internal void ShowSearch(bool replace)
    {
        var search = Editor.SearchPanel;
        if (search is null) return;
        search.IsReplaceMode = replace;
        if (!string.IsNullOrEmpty(Editor.SelectedText)) search.SearchPattern = Editor.SelectedText;
        search.Open();
        search.Reactivate();
    }

    internal async Task SaveAsync()
    {
        if (WorkspaceDocument is not { } document) return;
        try { await document.SaveAsync(); }
        catch (Exception error) { ShowError(error); }
    }

    private async void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        var command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if (e.KeyModifiers == command && e.Key == Key.S) { e.Handled = true; await SaveAsync(); }
        else if (e.KeyModifiers == command && e.Key == Key.F) { e.Handled = true; ShowSearch(false); }
        else if (e.KeyModifiers == command && e.Key == Key.H) { e.Handled = true; ShowSearch(true); }
        else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.G) { e.Handled = true; ShowGoToLine(); }
        else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.Space) { e.Handled = true; await RequestLanguageAsync("completion"); }
        else if (e.KeyModifiers == (KeyModifiers.Shift | KeyModifiers.Alt) && e.Key == Key.F)
        {
            // ⇧⌥F: the selection when there is one the server can format, else the whole file
            e.Handled = true;
            await FormatAsync(Editor.SelectionLength > 0 && FormatUnavailableReason(true) is null);
        }
        else if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.F12) { e.Handled = true; await RequestLanguageAsync("definition"); }
        else if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.F2) { e.Handled = true; ShowRename(); }
        else if (e.KeyModifiers == (command | KeyModifiers.Shift) && e.Key == Key.Space) { e.Handled = true; await RequestLanguageAsync("hover"); }
        else if (e.Key == Key.Escape && Editor.TextArea.Selection is AvaloniaEdit.Editing.MultiSelection)
        {
            Editor.TextArea.ClearSelection();
            e.Handled = true;
        }
    }

    private void ShowError(Exception error) => _status.Text = error.Message;

    private void RefreshFoldings()
    {
        if (_folding is null || WorkspaceDocument is not { } document || _foldRevision == document.Revision) return;
        _foldRevision = document.Revision;
        _folding.UpdateFoldings(CreateIndentFoldings(document.Buffer), -1);
    }

    // Indentation-based regions work without pretending to parse any language's strings/comments.
    internal static IEnumerable<NewFolding> CreateIndentFoldings(TextDocument buffer)
    {
        var folds = new List<NewFolding>();
        var stack = new Stack<(int Indent, int Start)>();
        DocumentLine? previous = null;
        var previousIndent = 0;
        foreach (var line in buffer.Lines)
        {
            var indent = 0;
            var offset = line.Offset;
            while (offset < line.EndOffset && buffer.GetCharAt(offset) is ' ' or '\t')
            {
                indent += buffer.GetCharAt(offset++) == '\t' ? 4 : 1;
            }
            if (offset == line.EndOffset) continue;
            while (stack.TryPeek(out var region) && indent <= region.Indent)
            {
                stack.Pop();
                if (previous is not null && previous.EndOffset > region.Start)
                    folds.Add(new NewFolding(region.Start, previous.EndOffset));
            }
            if (previous is not null && indent > previousIndent) stack.Push((previousIndent, previous.EndOffset));
            previous = line;
            previousIndent = indent;
        }
        while (stack.TryPop(out var region))
            if (previous is not null && previous.EndOffset > region.Start) folds.Add(new NewFolding(region.Start, previous.EndOffset));
        folds.Sort((left, right) => left.StartOffset.CompareTo(right.StartOffset));
        return folds;
    }

    private void ToggleFold()
    {
        if (_folding is null) return;
        if (Editor.SelectionLength > 0)
        {
            var start = Editor.SelectionStart;
            var end = start + Editor.SelectionLength;
            if (Editor.Document.GetLineByOffset(start) != Editor.Document.GetLineByOffset(end))
            {
                var fold = _folding.CreateFolding(start, end);
                fold.IsFolded = true;
                Editor.Select(start, 0);
            }
            return;
        }
        var containing = _folding.AllFoldings.LastOrDefault(f => f.StartOffset <= Editor.CaretOffset && f.EndOffset >= Editor.CaretOffset)
            ?? _folding.GetNextFolding(Editor.CaretOffset);
        if (containing is not null) containing.IsFolded = !containing.IsFolded;
    }
}
