using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OmpGui.App.Services;
using OmpGui.App.ViewModels;

namespace OmpGui.App.Views;

/// <summary>View-only concerns: keys, focus, auto-scroll and the close sequence.</summary>
public sealed partial class MainWindow : Window
{
    private const double NarrowWidth = 760;

    /// <summary>OS notifications (chosen per platform by the composition root).</summary>
    public OmpGui.App.Platform.INotifier Notifier { get; set; } = new OmpGui.App.Platform.NoNotifier();

    /// <summary>Opens the chat of the last notification while the window was in the background (where activating the
    /// window is the notification's click).</summary>
    private Action? _notificationClick;

    public MainWindow()
    {
        InitializeComponent();
        // Dictation keys first (tunnel): Ctrl/⌘+Shift+Space toggles it anywhere; while the bar shows, Esc cancels and
        // Enter finishes — before the composer's own Enter could send.
        AddHandler(KeyDownEvent, (_, e) => { if (Vm is { } vm) OmpGui.App.Controls.DictationBar.HandleKey(vm, e); }, RoutingStrategies.Tunnel);
        // 1–9 answer the card (Claude Code's menus): 1 Allow / 2 Deny, or a numbered choice. Not while typing: only when
        // the keyboard is outside text fields, or in the message box while it is empty.
        AddHandler(KeyDownEvent, OnDigitForDialog, RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, (_, e) => { if (_swallowText) { _swallowText = false; e.Handled = true; } }, RoutingStrategies.Tunnel);
        Composer.AddHandler(KeyDownEvent, OnComposerKeyDown, RoutingStrategies.Tunnel);
        // After the box took the typed text (its own handler marks it handled): "@" and a name open the files menu
        Composer.AddHandler(TextInputEvent, (_, _) => Vm?.OnComposerTyped(), RoutingStrategies.Bubble, handledEventsToo: true);
        // Every paste into the message box (keys, its context menu) goes through PasteAsync: files and images attach,
        // a long text becomes a chip
        Composer.AddHandler(TextBox.PastingFromClipboardEvent, (_, e) =>
        {
            e.Handled = true;
            _ = PasteAsync();
        });
        // The model menu opens above the message box, so the message stays in view while choosing
        ComposerBox.SizeChanged += (_, _) => PlaceModelMenu();
        // Opening the preview or the event panel, or dragging their splitters, narrows the column: density follows it.
        HeaderBar.PropertyChanged += (_, e) => { if (e.Property == BoundsProperty) UpdateHeaderDensity(); };
        // A longer title, another project or branch: the pane toggles fold (or come back) as the header's content needs
        HeaderLeft.SizeChanged += (_, _) => QueueHeaderFold();
        HeaderBranch.SizeChanged += (_, _) => QueueHeaderFold();
        // The page list beside the page needs about 760 px (the list, a readable card): the page has the whole window
        SettingsPage.SizeChanged += (_, e) =>
        {
            if (Vm is not { } vm || e.NewSize.Width <= 0) return;
            var narrow = e.NewSize.Width < 760;
            if (vm.IsSettingsNarrow == narrow) return;
            vm.IsSettingsNarrow = narrow;
            Dispatcher.UIThread.Post(CenterCurrentSettingsTab, DispatcherPriority.Background);
        };
        CardsScroll.PropertyChanged += (_, e) => { if (e.Property == BoundsProperty || e.Property == ScrollViewer.MaxHeightProperty) FitCardsShadowRoom(); };
        SidebarScrim.PointerPressed += (_, e) => { if (Vm is { ShowSidebar: true } vm) { vm.ToggleSidebarCommand.Execute(null); e.Handled = true; } };
        WireSidebarGrip();
        // Ctrl+Tab / Ctrl+Shift+Tab: the next / previous session in the sidebar (before Tab moves the focus)
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Tab || (e.KeyModifiers & ~KeyModifiers.Shift) != KeyModifiers.Control || Vm is not { IsSettingsOpen: false, IsImageViewerOpen: false } vm) return;
            vm.CycleSession(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        SettingsScroll.SizeChanged += (_, e) => SettingsColumn.Width = Math.Max(0, Math.Min(SettingsColumn.MaxWidth, e.NewSize.Width - SettingsColumn.Margin.Left - SettingsColumn.Margin.Right));
        SettingsNavStrip.ScrollChanged += (_, _) => UpdateSettingsStripFade();
        SettingsNavStrip.SizeChanged += (_, _) => UpdateSettingsStripFade();
        // A mouse wheel scrolls the strip sideways (it has no vertical scrolling)
        SettingsNavStrip.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (e.Delta.X != 0 || e.Delta.Y == 0) return;
            var max = Math.Max(0, SettingsNavStrip.Extent.Width - SettingsNavStrip.Viewport.Width);
            SettingsNavStrip.Offset = new Vector(Math.Clamp(SettingsNavStrip.Offset.X - e.Delta.Y * 48, 0, max), 0);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        RenameBox.AddHandler(KeyDownEvent, OnRenameKeyDown, RoutingStrategies.Tunnel);
        // Files and images dropped anywhere on the window go to the composer.
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => ComposerBox.Classes.Set("dragover", false));
        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(this, true);
        RenameBox.LostFocus += (_, _) => { if (Vm is { IsRenaming: true } vm) vm.CommitRenameCommand.Execute(null); };
        JumpToLatest.Click += (_, _) => FollowLatest();
        // The model picker works from the keyboard: it opens with the filter focused, Enter takes the first match
        // (or the entry chosen with the arrows), Esc closes it.
        ModelPopup.Opened += (_, _) => Dispatcher.UIThread.Post(() => ModelFilterBox.Focus(), DispatcherPriority.Background);
        // Closing hands focus back to the chip that opened it; after a choice it goes to the composer instead (else the
        // next Space re-opens the picker and the message goes into its filter). Esc returns to the chip.
        ModelPopup.Closed += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (_modelMenuEscaped) ModelButton.Focus();
            else if (Composer.IsEffectivelyEnabled) Composer.Focus();
            else RestoreLostFocus();
            _modelMenuEscaped = false;
        }, DispatcherPriority.Background);
        ModelMenu.AddHandler(KeyDownEvent, OnModelMenuKeyDown, RoutingStrategies.Tunnel);
        Opened += (_, _) =>
        {
            Composer.Focus();
            AssistantRowViewModel.CopyRequested += CopyToClipboard;
        };
        Closed += (_, _) => AssistantRowViewModel.CopyRequested -= CopyToClipboard;
        // Like native apps, the caret blinks only in the active window, and (like GTK's cursor-blink-timeout) stops
        // blinking after a while without input. A blinking caret repaints twice a second: with software rendering
        // that alone cost 2–5% of a core at idle (measured). Not relying on activation
        // events alone: some window systems do not report them (seen under Xvfb + openbox).
        _caretIdle = new DispatcherTimer { Interval = CaretIdleTimeout };
        _caretIdle.Tick += (_, _) =>
        {
            _caretIdle.Stop();
            Composer.CaretBlinkInterval = TimeSpan.Zero;
        };
        AddHandler(KeyDownEvent, (_, _) => ResumeCaret(), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, (_, _) => ResumeCaret(), RoutingStrategies.Tunnel, handledEventsToo: true);
        Opened += (_, _) => _caretIdle.Start();
        Activated += (_, _) =>
        {
            ResumeCaret();
            Vm?.SetWindowActive(true);
            // Where the notification is the flashing window itself, bringing it to the front is its click
            if (Notifier.ClickActivatesWindow && Interlocked.Exchange(ref _notificationClick, null) is { } open) open();
        };
        Deactivated += (_, _) =>
        {
            _caretIdle.Stop();
            Composer.CaretBlinkInterval = TimeSpan.Zero;
            Vm?.SetWindowActive(false);
        };
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty && DataContext is MainViewModel vm) vm.SetWindowState(WindowState.ToString());
        };
        // Below this width the conversation needs all the room; the sidebar opens on demand (Ctrl/⌘+B).
        SizeChanged += (_, _) => FitPanels();
        ConversationArea.SizeChanged += (_, _) => FitEmptyState();
        ComposerToolbar.SizeChanged += (_, _) => QueueComposerFit();
        ComposerFooter.SizeChanged += (_, _) => QueueComposerFit();
        ComposerBox.PropertyChanged += (_, e) => { if (e.Property == BoundsProperty && e.OldValue is Rect o && e.NewValue is Rect n && o.Height != n.Height || e.Property == MarginProperty) FitPanels(); };
        // A dragged side pane, preview, terminal or Events width is kept for the next time it opens (a splitter between two
        // docked panels resizes both)
        PreviewSplitter.DragCompleted += (_, _) => KeepDraggedWidths();
        SidePaneSplitter.DragCompleted += (_, _) => KeepDraggedWidths();
        TerminalSplitter.DragCompleted += (_, _) => KeepDraggedWidths();
        DebugSplitter.DragCompleted += (_, _) => KeepDraggedWidths();
    }

    private bool _composerFitQueued;

    /// <summary>
    /// Refit once the current layout pass is over (width and content changes in a row: once). Never from LayoutUpdated:
    /// the fit lays the toolbar out again, and fractional widths never settled (an endless loop).
    /// </summary>
    private void QueueComposerFit()
    {
        if (_composerFitQueued) return;
        _composerFitQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _composerFitQueued = false;
            FitComposerToolbar();
        }, DispatcherPriority.Background);
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private const double ChatMinWidth = 420, PreviewMinWidth = 320, SidePaneMinWidth = 280, TerminalMinWidth = 360, DebugMinWidth = 320;
    private double _previewWidth = 520, _sidePaneWidth = 340, _terminalWidth = 520, _debugWidth = 420;

    private void KeepDraggedWidths()
    {
        var col = MainColumn.ColumnDefinitions;
        if (SidePaneSplitter.IsVisible) _sidePaneWidth = col[Grid.GetColumn(SidePanePanel)].ActualWidth;
        if (PreviewSplitter.IsVisible) _previewWidth = col[Grid.GetColumn(PreviewPanel)].ActualWidth;
        if (TerminalSplitter.IsVisible) _terminalWidth = col[Grid.GetColumn(TerminalPanel)].ActualWidth;
        if (DebugSplitter.IsVisible) _debugWidth = col[Grid.GetColumn(DebugPanel)].ActualWidth;
    }
    /// <summary>Both laid over a narrow conversation: the one opened last is on top.</summary>
    private bool _sidePaneOnTop;

    /// <summary>
    /// Sidebar, conversation, side pane (Views menu), preview, terminal and the Events panel share the window: the side
    /// panels count when deciding that the sidebar has to give way. The side pane docks first (the narrower), then the
    /// preview, the terminal and Events, each beside the conversation; each gets what the conversation (at least 420)
    /// leaves, up to its preferred width, never less than its minimum. One that does not fit is shown over the
    /// conversation until it is closed.
    /// </summary>
    private void FitPanels()
    {
        if (Vm is not { } vm) return;
        var preview = vm.IsPreviewOpen;
        var pane = vm.IsSidePaneOpen;
        var terminal = vm.IsTerminalOpen;
        var debug = vm.IsDebugVisible;
        // A sidebar dragged wider than its default leaves the conversation as much room before it gives way
        vm.IsNarrow = Bounds.Width < NarrowWidth + Math.Max(0, vm.SidebarWidth - MainViewModel.SidebarDefaultWidth)
            + (preview ? PreviewMinWidth : 0) + (pane ? SidePaneMinWidth : 0) + (terminal ? TerminalMinWidth : 0) + (debug ? DebugMinWidth : 0);
        FitSidebar(vm);
        var main = Bounds.Width - (vm.ShowSidebar && !vm.IsNarrow ? vm.SidebarWidth : 0);
        var room = main - ChatMinWidth; // for the docked panels and their 1 px splitters
        // Dock in this order while each still fits at its minimum: side pane, preview, terminal, Events
        var panels = new (Control Panel, GridSplitter Splitter, bool Open, double Min, double Preferred)[]
        {
            (SidePanePanel, SidePaneSplitter, pane, SidePaneMinWidth, _sidePaneWidth),
            (PreviewPanel, PreviewSplitter, preview, PreviewMinWidth, _previewWidth),
            (TerminalPanel, TerminalSplitter, terminal, TerminalMinWidth, _terminalWidth),
            (DebugPanel, DebugSplitter, debug, DebugMinWidth, _debugWidth),
        };
        var docked = new bool[panels.Length];
        var reserved = 0.0;
        for (var i = 0; i < panels.Length; i++)
        {
            docked[i] = panels[i].Open && room - reserved >= 1 + panels[i].Min;
            if (docked[i]) reserved += 1 + panels[i].Min;
        }
        // Each docked panel gets its preferred width while the others keep at least their minimum
        var widths = new double[panels.Length];
        var used = 0.0;
        for (var i = 0; i < panels.Length; i++)
        {
            if (!docked[i]) continue;
            var others = 0.0;
            for (var j = i + 1; j < panels.Length; j++) if (docked[j]) others += 1 + panels[j].Min;
            widths[i] = Math.Clamp(panels[i].Preferred, panels[i].Min, Math.Max(panels[i].Min, room - used - 1 - others));
            used += 1 + widths[i];
        }
        // Columns: the conversation, then each docked panel after its splitter, in slots 2, 4, 6, 8 (a splitter resizes
        // its two neighbours, so docked panels take the slots in order with no empty column between them)
        var col = MainColumn.ColumnDefinitions;
        col[0].Width = new GridLength(1, GridUnitType.Star);
        col[0].MinWidth = docked.Any(d => d) ? ChatMinWidth : 0;
        for (var c = 2; c <= 8; c += 2) col[c].Width = GridLength.Auto;
        var slot = 2;
        for (var i = 0; i < panels.Length; i++)
        {
            var (panel, splitter, open, _, _) = panels[i];
            splitter.IsVisible = docked[i];
            if (docked[i])
            {
                col[slot].Width = new GridLength(widths[i]);
                Grid.SetColumn(splitter, slot - 1);
                Grid.SetColumn(panel, slot);
                Grid.SetColumnSpan(panel, 1);
                panel.ZIndex = 0;
                slot += 2;
                continue;
            }
            // Not docked: over the conversation's part (the whole column when nothing is docked), until it is closed
            Grid.SetColumn(panel, 0);
            Grid.SetColumnSpan(panel, 1);
            panel.ZIndex = !open ? 0 : panel == DebugPanel ? 13 : panel == TerminalPanel ? 12 : panel == SidePanePanel ? (_sidePaneOnTop ? 11 : 9) : (_sidePaneOnTop ? 9 : 10);
        }
        // Cards (an approval, setup, todos) never push the composer off a low window: they scroll instead
        // (40 = the message box's margins and some air; its top margin grows by the pet's band when a pet is shown)
        CardsScroll.MaxHeight = Math.Max(96, Bounds.Height - 48 - Math.Max(ComposerBox.Bounds.Height, 96) - 34 - ComposerBox.Margin.Top
            - (ComposerFooter.IsVisible ? Math.Max(0, ComposerFooter.Bounds.Height + ComposerFooter.Margin.Top) : 0));
        UpdateHeaderDensity();
        FitEmptyState();
    }

    /// <summary>
    /// A low window has no room for the whole greeting above the centred composer: the logo goes first, then the
    /// starter prompts; the question itself (and the notices above it) always stays (nothing is cut off at the top).
    /// </summary>
    private void FitEmptyState()
    {
        if (!EmptyState.IsVisible) return;
        var room = ConversationArea.Bounds.Height - EmptyState.Margin.Top - EmptyState.Margin.Bottom;
        if (room <= 0) return;
        var width = Math.Max(0, Math.Min(EmptyState.MaxWidth, ConversationArea.Bounds.Width - EmptyState.Margin.Left - EmptyState.Margin.Right));
        double Need(Control c) { c.Measure(new Size(width, double.PositiveInfinity)); return c.DesiredSize.Height; }
        // A hidden control measures as 0: show both before measuring, then decide (layout runs once, afterwards)
        StarterPrompts.IsVisible = true;
        GreetingLogo.IsVisible = true;
        var greeting = Need(Greeting) + (GreetingNotices.IsVisible ? Need(GreetingNotices) + EmptyState.Spacing : 0);
        var starters = StarterPrompts.ItemCount > 0 ? Need(StarterPrompts) + EmptyState.Spacing : 0;
        var logo = GreetingLogo.Height + EmptyState.Spacing;
        StarterPrompts.IsVisible = greeting + starters <= room;
        GreetingLogo.IsVisible = greeting + (StarterPrompts.IsVisible ? starters : 0) + logo <= room;
    }

    /// <summary>
    /// A new session (Claude Code): the greeting and the composer together in the middle of the page; with the first
    /// message the composer goes to the bottom. The last row (empty otherwise) takes the space under the composer.
    /// </summary>
    private void UpdateEmptyLayout(MainViewModel vm)
    {
        var empty = vm.IsConversationEmpty;
        MainColumn.RowDefinitions[4].Height = empty ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        EmptyState.VerticalAlignment = empty ? VerticalAlignment.Bottom : VerticalAlignment.Center;
        EmptyState.Margin = new Thickness(24, 0, 24, empty ? 28 : 48);
        // omp cannot start in a new session: the setup screen alone, in the middle (rows 1 and 4 share the height)
        ComposerBox.IsVisible = !vm.ShowSetupScreen;
        ComposerFooter.IsVisible = !vm.ShowSetupScreen;
        FitEmptyState(); // a notice listed above the greeting takes room from the logo and the starters
    }

    /// <summary>
    /// The composer's pill menus close once a choice is made: a menu item, or the bypass confirmation's button (the
    /// bypass item itself keeps the menu open for that confirmation).
    /// </summary>
    private static void CloseOnChoice(Button owner, Func<bool>? keepOpen = null)
    {
        if (owner.Flyout is not Flyout { Content: Control content } flyout) return;
        content.AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (e.Source is not Button { Classes: var c } b || !(c.Contains("menu-item") || b.Name == "ConfirmYoloButton")) return;
            Dispatcher.UIThread.Post(() => { if (keepOpen?.Invoke() != true) flyout.Hide(); }, DispatcherPriority.Background);
        });
    }

    /// <summary>
    /// The header and the composer toolbar fit the conversation column: compact below 800, tight below 600
    /// (Styles.axaml).
    /// </summary>
    private void UpdateHeaderDensity()
    {
        // The conversation column's real width: the header spans it exactly (sidebar, preview and event panels excluded).
        var column = HeaderBar.Bounds.Width > 0 ? HeaderBar.Bounds.Width : Bounds.Width - (Vm is { ShowSidebar: true, IsNarrow: false } v ? v.SidebarWidth : 0);
        HeaderBar.Classes.Set("compact", column < 800);
        HeaderBar.Classes.Set("tight", column < 600);
        HeaderBar.Classes.Set("narrow", column < 520);
        // The title takes at most half the header: a long one had left the project chip an empty "folder ▾"
        SessionTitle.MaxWidth = Math.Clamp(column * 0.5, 160, 560);
        FoldPaneToggles(column);
        FitComposerToolbar();
    }

    private double _paneTogglesWidth;

    /// <summary>
    /// The header's pane toggles fold into ⋮ when the title, project and branch would not fit beside them (measured,
    /// as the composer's toolbar: a fixed breakpoint folded them in roomy headers or clipped the branch in full ones).
    /// </summary>
    private void FoldPaneToggles(double column)
    {
        if (PaneToggles.IsVisible)
        {
            PaneToggles.Measure(Size.Infinity);
            if (PaneToggles.DesiredSize.Width > 0) _paneTogglesWidth = PaneToggles.DesiredSize.Width;
        }
        HeaderLeft.Measure(Size.Infinity);
        var others = HeaderBar.Padding.Left + HeaderBar.Padding.Right + HeaderLeft.Margin.Right
            + (Vm is { ShowSidebar: false } ? 38 + 36 : 0); // the sidebar toggle on the left and Settings on the right
        var fold = column < 520 || HeaderLeft.DesiredSize.Width + _paneTogglesWidth + others > column;
        HeaderBar.Classes.Set("fold-panes", fold);
        HeaderLeft.InvalidateMeasure();
    }

    private bool _headerFoldQueued;

    private void QueueHeaderFold()
    {
        if (_headerFoldQueued) return;
        _headerFoldQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _headerFoldQueued = false;
            if (HeaderBar.Bounds.Width > 0) FoldPaneToggles(HeaderBar.Bounds.Width);
        }, DispatcherPriority.Background);
    }

    private bool _fittingComposer;

    private bool _cardsCapped;

    /// <summary>The cards' shadow room while they fit; none while they scroll at their cap (with hysteresis: no flicker).</summary>
    private void FitCardsShadowRoom()
    {
        var cap = CardsScroll.MaxHeight;
        if (double.IsInfinity(cap) || double.IsNaN(cap)) return;
        // Without the room the content is 20 px shorter: leave the capped state only when it clearly fits again
        var height = CardsScroll.Bounds.Height;
        var capped = _cardsCapped ? height >= cap - 24 : height >= cap - 0.5;
        if (capped == _cardsCapped) return;
        _cardsCapped = capped;
        CardsScroll.Margin = new Thickness(0, 0, 0, capped ? 0 : -20);
        CardsStack.Margin = new Thickness(24, 4, 24, capped ? 6 : 26);
    }

    /// <summary>
    /// A narrow window's sidebar is a drawer: over the conversation (which had shrunk to 220 px in a 480 px window), with
    /// a shadow, and the dimmed rest of the window closes it.
    /// </summary>
    private void FitSidebar(MainViewModel vm)
    {
        var drawer = vm.IsNarrow && vm.ShowSidebar;
        Grid.SetColumn(Sidebar, drawer ? 1 : 0);
        Sidebar.HorizontalAlignment = drawer ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        Sidebar.ZIndex = drawer ? 20 : 0;
        Sidebar.Classes.Set("drawer", drawer);
        // A wide sidebar as a drawer still leaves a strip of the dimmed window to tap
        Sidebar.MaxWidth = drawer ? Math.Max(MainViewModel.SidebarMinWidth, Bounds.Width - 56) : double.PositiveInfinity;
        SidebarScrim.IsVisible = drawer;
    }

    /// <summary>
    /// The composer's toolbar and the row under it shed labels and chips (Styles.axaml, "density1" onwards) only until
    /// their content fits the width: a fixed breakpoint lost the voice and send buttons past the right edge once the row
    /// held more. Measured, not guessed, at every change of width or content.
    /// </summary>
    private void FitComposerToolbar()
    {
        if (_fittingComposer) return;
        _fittingComposer = true;
        try
        {
            FitRow(ComposerBox, ComposerToolbar, ComposerLeft, ComposerRight, 5);
            FitRow(ComposerFooter, ComposerFooter, FooterLeft, UsageIndicator, 4);
        }
        finally { _fittingComposer = false; }
    }

    private static void FitRow(StyledElement holder, Control row, Control left, Control right, int levels)
    {
        var available = row.Bounds.Width;
        if (available <= 0) return;
        for (var level = 0; level <= levels; level++)
        {
            for (var l = 1; l <= levels; l++) holder.Classes.Set("density" + l, l <= level);
            // A control measured again with the same size returns its cached answer, and a label hidden deep inside a
            // chip does not reach the chip until the next layout pass: measure the whole row anew
            foreach (var c in row.GetVisualDescendants().OfType<Layoutable>()) c.InvalidateMeasure();
            left.Measure(Size.Infinity);
            right.Measure(Size.Infinity);
            if (left.DesiredSize.Width + right.DesiredSize.Width + 8 <= available) break;
        }
    }

    // ───────────── The sidebar's right edge: drag to resize (220–480, kept), double-click for the default ─────────────

    private double _gripStartX, _gripStartWidth;
    private bool _gripDragging;

    private void WireSidebarGrip()
    {
        SidebarGrip.PointerPressed += (_, e) =>
        {
            if (Vm is not { } vm || Sidebar.Classes.Contains("drawer") || !e.GetCurrentPoint(SidebarGrip).Properties.IsLeftButtonPressed) return;
            if (e.ClickCount == 2)
            {
                vm.SidebarWidth = MainViewModel.SidebarDefaultWidth;
                vm.SaveSidebarWidth();
                e.Handled = true;
                return;
            }
            _gripDragging = true;
            _gripStartX = e.GetPosition(this).X;
            _gripStartWidth = vm.SidebarWidth;
            e.Pointer.Capture(SidebarGrip);
            SidebarGrip.Classes.Add("dragging");
            e.Handled = true;
        };
        SidebarGrip.PointerMoved += (_, e) =>
        {
            if (!_gripDragging || Vm is not { } vm) return;
            // Never so wide that the conversation drops under its minimum
            var width = _gripStartWidth + e.GetPosition(this).X - _gripStartX;
            vm.SidebarWidth = Math.Min(width, Math.Max(MainViewModel.SidebarMinWidth, Bounds.Width - ChatMinWidth));
        };
        SidebarGrip.PointerReleased += (_, e) =>
        {
            if (_gripDragging) e.Pointer.Capture(null);
            EndSidebarDrag();
        };
        SidebarGrip.PointerCaptureLost += (_, _) => EndSidebarDrag();
    }

    private void EndSidebarDrag()
    {
        if (!_gripDragging) return;
        _gripDragging = false;
        SidebarGrip.Classes.Remove("dragging");
        Vm?.SaveSidebarWidth();
    }

    /// <summary>The narrow settings strip fades at an edge that has more tabs past it: it says the strip scrolls.</summary>
    private void UpdateSettingsStripFade()
    {
        var strip = SettingsNavStrip;
        var max = strip.Extent.Width - strip.Viewport.Width;
        var left = strip.Offset.X > 0.5;
        var right = strip.Offset.X < max - 0.5;
        if (!left && !right || strip.Bounds.Width <= 0)
        {
            strip.OpacityMask = null;
            return;
        }
        var fade = Math.Min(0.4, 40 / strip.Bounds.Width);
        strip.OpacityMask = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(left ? Colors.Transparent : Colors.Black, 0),
                new GradientStop(Colors.Black, fade),
                new GradientStop(Colors.Black, 1 - fade),
                new GradientStop(right ? Colors.Transparent : Colors.Black, 1),
            },
        };
    }

    private readonly DispatcherTimer _caretIdle;

    /// <summary>Time without input after which the caret stops blinking (tests shorten it).</summary>
    internal TimeSpan CaretIdleTimeout
    {
        get => _caretIdleTimeout;
        set { _caretIdleTimeout = value; if (_caretIdle is not null) _caretIdle.Interval = value; }
    }
    private TimeSpan _caretIdleTimeout = TimeSpan.FromSeconds(10);

    private void ResumeCaret()
    {
        Composer.ClearValue(TextBox.CaretBlinkIntervalProperty);
        _caretIdle.Stop();
        _caretIdle.Start();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Vm is { } vm)
        {
            // A flyout's content gets the window's data only once it opens, after its popup was placed: the menu then
            // grew past its place (the thinking menu covered its chip on its first opening). Bound beforehand, it is
            // measured at its full size when placed.
            foreach (var b in new[] { ThinkingBox, AttachButton, ProjectButton, ApprovalButton })
                if (b.Flyout is Flyout { Content: Control content }) content.DataContext = vm;
            vm.ScrollToLatestRequested += FollowLatest;
            vm.ModelRowsNavigated += FocusModelRow;
            vm.FocusComposerRequested += () => Dispatcher.UIThread.Post(() =>
            {
                if (!Composer.IsEffectivelyVisible) return;
                Composer.Focus();
                Composer.CaretIndex = Composer.Text?.Length ?? 0;
            }, DispatcherPriority.Background);
            vm.Rows.CollectionChanged += (_, _) => UpdateEmptyLayout(vm);
            UpdateEmptyLayout(vm);
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(MainViewModel.SessionTitle) or nameof(MainViewModel.ProjectName) or nameof(MainViewModel.IsRenaming))
                    QueueHeaderFold();
            };
            vm.OpenUrlRequested += url => _ = Launcher.LaunchUriAsync(new Uri(url));
            // "Restart now" is the user's own choice to quit (it refuses during a run), and the update script already
            // waits for this process: asking again could leave it waiting for a quit that comes much later.
            vm.QuitRequested += () => Dispatcher.UIThread.Post(QuitWithoutAsking);
            vm.PickFolderRequested += PickFolderAsync;
            vm.PickImagesRequested += PickImagesAsync;
            vm.SaveFileRequested += SaveFileAsync;
            vm.CaretToEndRequested += () => Dispatcher.UIThread.Post(() => Composer.CaretIndex = Composer.Text?.Length ?? 0);
            vm.Terminals.CollectionChanged += (_, e) => SyncTerminals(vm);
            vm.AttentionRequested += (title, message, open) =>
            {
                void Clicked() => Dispatcher.UIThread.Post(() =>
                {
                    if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                    Activate();
                    open();
                });
                if (!IsActive) _notificationClick = open;
                Notifier.Notify(title, message, TryGetPlatformHandle()?.Handle ?? 0, Clicked);
            };
            // Each chat keeps where its reader was while another one is shown (MainWindow.ChatScroll.cs)
            vm.SaveScrollRequested += SaveChatScroll;
            vm.RestoreScrollRequested += RestoreChatScroll;
            vm.CopyTextRequested += text => { if (Clipboard is { } c) _ = c.SetTextAsync(text); };
            // The event list follows the newest entry while it is open (once per UI apply, not per entry).
            vm.DebugLogAppended += () =>
            {
                if (vm.IsDebugVisible && vm.DebugLog.Count > 0) DebugList.ScrollIntoView(vm.DebugLog.Count - 1);
            };
            // Opening the panel shows the newest events.
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.IsRenaming) && vm.IsRenaming)
                    Dispatcher.UIThread.Post(() => { RenameBox.Focus(); RenameBox.SelectAll(); }, DispatcherPriority.Background);
                if (e.PropertyName == nameof(MainViewModel.IsDebugVisible) && vm.IsDebugVisible && vm.DebugLog.Count > 0)
                    Dispatcher.UIThread.Post(() => DebugList.ScrollIntoView(vm.DebugLog.Count - 1), DispatcherPriority.Background);
                // The side pane or the preview opened last is on top when both cover a narrow conversation
                if (e.PropertyName == nameof(MainViewModel.ActivePane) && vm.IsSidePaneOpen) _sidePaneOnTop = true;
                if (e.PropertyName == nameof(MainViewModel.IsPreviewOpen) && vm.IsPreviewOpen) _sidePaneOnTop = false;
                // What the composer's toolbar holds changed: fit it again
                if (e.PropertyName is nameof(MainViewModel.ShowGreeting) or nameof(MainViewModel.ProjectName) or nameof(MainViewModel.CurrentModel)
                    or nameof(MainViewModel.ApprovalLabel) or nameof(MainViewModel.CanQueue) or nameof(MainViewModel.IsIdle) or nameof(MainViewModel.IsRunning))
                    QueueComposerFit();
                if (e.PropertyName is nameof(MainViewModel.ShowSidebar) or nameof(MainViewModel.IsPreviewOpen) or nameof(MainViewModel.IsDebugVisible)
                    or nameof(MainViewModel.ActivePane) or nameof(MainViewModel.IsTerminalOpen) or nameof(MainViewModel.SidebarWidth))
                    FitPanels();
                if (e.PropertyName == nameof(MainViewModel.ShowSetupScreen)) UpdateEmptyLayout(vm);
                // A question on a low window: its answer buttons (at the card's end) in view, not its top
                if (e.PropertyName == nameof(MainViewModel.HasDialog) && vm.HasDialog)
                    Dispatcher.UIThread.Post(() => CardsScroll.ScrollToEnd(), DispatcherPriority.Background);
                // Dictation over: the typing goes on in the message field
                if (e.PropertyName == nameof(MainViewModel.IsDictationBarVisible) && !vm.IsDictationBarVisible)
                    Dispatcher.UIThread.Post(() => Composer.Focus(), DispatcherPriority.Background);
                if (e.PropertyName is nameof(MainViewModel.Phase) or nameof(MainViewModel.HasDialog) or nameof(MainViewModel.IsSettingsOpen))
                    Dispatcher.UIThread.Post(RestoreLostFocus, DispatcherPriority.Background);
                // Another settings page: a new page starts at its top; in the narrow strip its name is scrolled into view
                if (e.PropertyName is nameof(MainViewModel.SettingsCategory) or nameof(MainViewModel.IsSettingsOpen))
                    Dispatcher.UIThread.Post(() =>
                    {
                        SettingsScroll.ScrollToHome();
                        CenterCurrentSettingsTab();
                    }, DispatcherPriority.Background);
            };
        }
    }

    /// <summary>
    /// The narrow strip scrolls the open page's tab to its middle: brought just into view, it had sat cut at the edge
    /// with nothing past it to say the strip scrolls.
    /// </summary>
    private void CenterCurrentSettingsTab()
    {
        if (SettingsNavStrip.Content is not Visual content) return;
        var tab = SettingsNavStrip.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("current"));
        if (tab?.TranslatePoint(default, content) is not { } at) return;
        var left = (content as Layoutable)?.Margin.Left ?? 0;
        var max = Math.Max(0, SettingsNavStrip.Extent.Width - SettingsNavStrip.Viewport.Width);
        var x = left + at.X + tab.Bounds.Width / 2 - SettingsNavStrip.Viewport.Width / 2;
        SettingsNavStrip.Offset = new Vector(Math.Clamp(x, 0, max), 0);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        // Command-modifier shortcuts follow the platform (⌘ on macOS, Ctrl elsewhere) via Avalonia's
        // hotkey configuration, not an OS check: ⌘/Ctrl+Enter sends too.
        if (this.GetPlatformSettings()?.HotkeyConfiguration.CommandModifiers is { } command)
        {
            SendButton.HotKey = new KeyGesture(Key.Enter, command);
            NewSessionButton.HotKey = new KeyGesture(Key.N, command);
            if (Vm is { } vm)
            {
                KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.B, command), Command = vm.ToggleSidebarCommand });
                // Settings, as in Claude Code and every desktop app (on macOS the app menu's ⌘, does it)
                if (!OperatingSystem.IsMacOS())
                    KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.OemComma, KeyModifiers.Control), Command = vm.OpenSettingsCommand });
                KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.OemTilde, KeyModifiers.Control), Command = vm.ToggleTerminalCommand });
                // Claude Code's browser keys: the preview, and selecting an element in it
                KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.B, command | KeyModifiers.Shift), Command = vm.TogglePreviewCommand });
                KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.S, command | KeyModifiers.Shift), Command = vm.SelectElementCommand });
                // The Views menu's panes (MainViewModel.Panes.cs): Files, Plan, Background tasks
                KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.F, command | KeyModifiers.Shift), Command = vm.TogglePaneCommand, CommandParameter = SidePane.Files });
                KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.P, command | KeyModifiers.Shift), Command = vm.TogglePaneCommand, CommandParameter = SidePane.Plan });
                KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.T, command | KeyModifiers.Shift), Command = vm.TogglePaneCommand, CommandParameter = SidePane.Tasks });
                // Claude Code's shortcut sheet
                KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.OemQuestion, command), Command = vm.ToggleShortcutsCommand });
            }
        }
        CloseOnChoice(ThinkingBox);
        CloseOnChoice(AttachButton);
        CloseOnChoice(ProjectButton);
        // The project in a new session's greeting opens the same project menu as the chip under the message box
        GreetingProjectButton.Click += (_, _) => ProjectButton.Flyout?.ShowAt(GreetingProjectButton);
        CloseOnChoice(ApprovalButton, () => Vm?.ConfirmYolo == true);
        if (ApprovalButton.Flyout is { } approvals)
            approvals.Closed += (_, _) => { if (Vm is { ConfirmYolo: true } vm) vm.CancelYoloCommand.Execute(null); };
        WireTranscriptScroll();
    }

    private readonly Dictionary<TerminalViewModel, Iciclecreek.Terminal.TerminalControl> _terminals = [];

    /// <summary>The control of a terminal tab (tests).</summary>
    internal Iciclecreek.Terminal.TerminalControl? TerminalOf(TerminalViewModel t) => _terminals.GetValueOrDefault(t);

    /// <summary>One terminal control (and PTY process) per tab; closing a tab ends its process.</summary>
    private void SyncTerminals(MainViewModel vm)
    {
        foreach (var gone in _terminals.Keys.Except(vm.Terminals).ToList())
        {
            KillTerminal(_terminals[gone]);
            TerminalHost.Children.Remove(_terminals[gone]);
            _terminals.Remove(gone);
        }
        foreach (var t in vm.Terminals.Where(t => !_terminals.ContainsKey(t)))
        {
            // Process stays empty until the client launches it: the library's inner view starts whatever Process holds
            // in its own Loaded, which raced the client's launch (a tab marked failed while its shell ran anyway).
            var control = new Iciclecreek.Terminal.TerminalControl
            {
                Process = "",
                BufferSize = 5000,
                FontSize = 13,
            };
            control.Bind(IsVisibleProperty, new Avalonia.Data.Binding(nameof(TerminalViewModel.IsSelected)) { Source = t });
            control.ProcessExited += (_, e) => Dispatcher.UIThread.Post(() => t.OnExited(e.ExitCode));
            control.Loaded += (_, _) => StartWhenReady(control, t);
            // The tab shown takes the keyboard: omp's setup and TUI are driven by keys, and typing went to the composer
            t.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(TerminalViewModel.IsSelected) && t.IsSelected)
                    Dispatcher.UIThread.Post(() => FocusTerminal(control), DispatcherPriority.Background);
            };
            _terminals[t] = control;
            TerminalHost.Children.Add(control);
        }
    }

    /// <summary>
    /// Launches the tab's process once the terminal's inner view exists and has loaded: its template and Loaded can
    /// come later than the control's own Loaded on a busy machine. Readiness is polled (50 ms) rather than awaited on
    /// one event, so a missed event cannot leave a tab that never starts; after 10 s it launches anyway and any
    /// failure is shown on the tab.
    /// </summary>
    internal void StartWhenReady(Iciclecreek.Terminal.TerminalControl control, TerminalViewModel t, int attempt = 0)
    {
        // A tab closed (or a window closing) while this waited never starts a process nobody would end.
        if (_terminalsClosed || !_terminals.ContainsValue(control)) return;
        if (control.IsLive || t.HasExited || t.Started) return;
        control.ApplyTemplate();
        var view = control.GetVisualDescendants().OfType<Iciclecreek.Terminal.TerminalView>().FirstOrDefault();
        if (view is not { IsLoaded: true } && attempt < 200)
        {
            DispatcherTimer.RunOnce(() => StartWhenReady(control, t, attempt + 1), TimeSpan.FromMilliseconds(50));
            return;
        }
        t.Started = true;
        _ = LaunchAsync(control, t);
        if (t.IsSelected) FocusTerminal(control);
    }

    /// <summary>Keys go to the terminal's view (the control itself only hosts it).</summary>
    private static void FocusTerminal(Iciclecreek.Terminal.TerminalControl control)
    {
        if (!control.IsEffectivelyVisible) return;
        var view = control.GetVisualDescendants().OfType<Iciclecreek.Terminal.TerminalView>().FirstOrDefault();
        if (view is { Focusable: true }) view.Focus();
        else control.Focus();
    }

    private static async Task LaunchAsync(Iciclecreek.Terminal.TerminalControl control, TerminalViewModel t)
    {
        control.EnvironmentVariables = new Dictionary<string, string>(t.Environment);
        try { await control.LaunchProcess(t.WorkingDirectory, t.File, [.. t.Args]); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or DllNotFoundException or UnauthorizedAccessException or EntryPointNotFoundException)
        {
            t.Status = "could not start: " + ex.Message;
            t.HasExited = true;
        }
    }

    private static void KillTerminal(Iciclecreek.Terminal.TerminalControl control)
    {
        try { if (control.IsLive) control.Kill(); }
        catch (Exception e) when (e is InvalidOperationException or IOException or System.ComponentModel.Win32Exception) { }
    }

    /// <summary>Terminal processes end with the window: no shell or omp TUI is left behind.</summary>
    private bool _terminalsClosed;

    internal IReadOnlyList<int> KillAllTerminals()
    {
        _terminalsClosed = true;
        var pids = _terminals.Values.Where(c => c.IsLive).Select(c => c.Pid).ToList();
        foreach (var c in _terminals.Values) KillTerminal(c);
        return pids;
    }

    private void CopyToClipboard(string text)
    {
        if (IsActive && Clipboard is { } clipboard) _ = clipboard.SetTextAsync(text);
    }

    /// <summary>
    /// Keyboard first: a card's button the user clicked (Restart omp, Allow…) goes away with its card, and focus went
    /// with it, so the next keystrokes went nowhere. When nothing visible has focus any more, the composer gets it; a
    /// control the user is in keeps it, also one in a popup (the model picker's filter lives in its own popup window).
    /// While the window is in the background it has no focus by design, and the element it will give focus back to on
    /// return is left alone.
    /// </summary>
    private void RestoreLostFocus()
    {
        if (!IsActive || IsModelMenuOpenNow()) return;
        if (FocusManager?.GetFocusedElement() is Visual { IsEffectivelyVisible: true }) return;
        if (Composer.IsEffectivelyVisible && Composer.IsEffectivelyEnabled) Composer.Focus();
    }

    private bool IsModelMenuOpenNow() => ModelPopup.IsOpen;

    private bool _modelMenuEscaped;

    private void OnModelMenuKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        var inList = e.Source is Visual v && ModelList.IsVisualAncestorOf(v) || e.Source == ModelList;
        switch (e.Key)
        {
            case Key.Enter when (inList ? ModelList.SelectedItem : vm.Models.FirstOrDefault()) is ModelItemViewModel m:
                vm.ChooseModelCommand.Execute(m);
                e.Handled = true;
                break;
            case Key.Down when !inList && vm.Models.Count > 0:
                FocusModelRow(0);
                e.Handled = true;
                break;
            // A family opens with →, closes with ← (or Esc, before Esc closes the menu)
            case Key.Right when inList && ModelList.SelectedItem is ModelItemViewModel { IsFamily: true } family:
                vm.OpenModelFamily(family);
                e.Handled = true;
                break;
            case Key.Left when inList && vm.CloseModelFamily():
                e.Handled = true;
                break;
            case Key.Escape when vm.CloseModelFamily():
                e.Handled = true;
                break;
            case Key.Escape:
                _modelMenuEscaped = true;
                vm.IsModelMenuOpen = false;
                e.Handled = true;
                break;
        }
    }

    /// <summary>Selects and focuses a row of the model menu once its container exists (after a family opens or closes).</summary>
    private void FocusModelRow(int index)
    {
        if (index < 0 || Vm is not { } vm || index >= vm.Models.Count) return;
        ModelList.SelectedIndex = index;
        ModelList.ScrollIntoView(index);
        Dispatcher.UIThread.Post(() => (ModelList.ContainerFromIndex(index) as InputElement ?? ModelList).Focus(NavigationMethod.Directional),
            DispatcherPriority.Loaded);
    }

    private void OnRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            vm.CommitRenameCommand.Execute(null);
            Composer.Focus();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            vm.CancelRenameCommand.Execute(null);
            Composer.Focus();
        }
    }

    private async Task<Stream?> SaveFileAsync(string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save diagnostics",
            SuggestedFileName = suggestedName,
            DefaultExtension = "zip",
            FileTypeChoices = [new FilePickerFileType("Zip archive") { Patterns = ["*.zip"], MimeTypes = ["application/zip"] }],
        });
        return file is null ? null : await file.OpenWriteAsync();
    }

    private async Task<string?> PickFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = "Open a project folder",
            AllowMultiple = false,
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    private void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (vm.IsCommandMenuOpen)
        {
            // The menu (slash commands, or the "@" files) takes the navigation keys while it is open.
            var handled = e.Key switch
            {
                Key.Down => vm.MoveSuggestion(1),
                Key.Up => vm.MoveSuggestion(-1),
                Key.Tab when e.KeyModifiers == KeyModifiers.Shift => vm.MoveSuggestion(-1),
                Key.Tab => vm.AcceptSuggestion(),
                Key.Enter when e.KeyModifiers == KeyModifiers.None => vm.AcceptSuggestion(enter: true),
                Key.Escape => Close(vm),
                _ => false,
            };
            if (handled)
            {
                e.Handled = true;
                if (e.Key is Key.Down or Key.Up or Key.Tab) ShowSelectedSuggestion(vm);
                return;
            }
        }
        // Esc while an "@" search has not shown its list yet: it closes that, it does not stop omp
        if (e.Key == Key.Escape && vm.DismissMention())
        {
            e.Handled = true;
            return;
        }
        var command = this.GetPlatformSettings()?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
        if (e.Key == Key.Tab && e.KeyModifiers == KeyModifiers.Shift)
        {
            // Claude Code: Shift+Tab steps through the permission modes; Bypass asks first, in the mode's menu
            e.Handled = true;
            vm.CycleApprovalMode();
            if (vm.ConfirmYolo) ApprovalButton.Flyout?.ShowAt(ApprovalButton);
        }
        else if (e.Key == Key.Up && e.KeyModifiers == KeyModifiers.None && vm.EditLastQueued())
        {
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Alt)
        {
            e.Handled = true;
            if (vm.SteerCommand.CanExecute(null)) vm.SteerCommand.Execute(null);
        }
        else if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)
                 && (!vm.SendWithModifier || e.KeyModifiers.HasFlag(command)))
        {
            // Enter sends (or ⌘/Ctrl+Enter, when Settings → General says so; plain Enter then adds a line)
            e.Handled = true;
            if (vm.SendCommand.CanExecute(null)) vm.SendCommand.Execute(null);
        }
    }

    /// <summary>The slash menu scrolls: the entry chosen with the keys stays in view.</summary>
    private void ShowSelectedSuggestion(MainViewModel vm)
    {
        if (!vm.IsCommandMenuOpen) return;
        CommandList.UpdateLayout();
        if (CommandList.ContainerFromIndex(vm.SelectedSuggestionIndex) is Control row) row.BringIntoView();
    }

    private static bool Close(MainViewModel vm)
    {
        vm.CloseCommandMenu();
        return true;
    }

    /// <summary>
    /// The message box's paste: files on the clipboard attach (images) or go in as paths, an image attaches, a long
    /// text becomes a chip (sent in full), and anything else goes in at the caret as the box's own paste would.
    /// </summary>
    private async Task PasteAsync()
    {
        if (Clipboard is not { } clipboard || Vm is not { } vm) return;
        try
        {
            var files = await clipboard.TryGetFilesAsync();
            if (files is { Length: > 0 })
            {
                await vm.AddFilesAsync([.. files.Select(f => f.TryGetLocalPath()).OfType<string>()]);
                return;
            }
            if (await clipboard.TryGetBitmapAsync() is { } bitmap)
            {
                using (bitmap) vm.TryAddImage(ImageAttachments.FromBitmap("pasted.png", bitmap), "Pasted image");
                return;
            }
            if (await clipboard.TryGetTextAsync() is not { Length: > 0 } text || vm.TryAddPastedText(text)) return;
            Composer.SelectedText = text;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or IOException)
        {
            vm.ComposerMessage = "Could not read the clipboard: " + ex.Message;
        }
    }

    /// <summary>
    /// The model menu's card 6 px above the message box, its right edge on the chip's: it covers the conversation, not
    /// the message being written. (The popup places its card's edges, not its margin's, at the chip's corner.)
    /// </summary>
    private void PlaceModelMenu()
    {
        if (ModelButton.TranslatePoint(default, ComposerBox) is not { } at) return;
        ModelPopup.VerticalOffset = -at.Y - 6;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var hasFiles = e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        ComposerBox.Classes.Set("dragover", hasFiles);
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        ComposerBox.Classes.Set("dragover", false);
        if (Vm is not { } vm || e.DataTransfer.TryGetFiles() is not { Length: > 0 } files) return;
        e.Handled = true;
        await vm.AddFilesAsync([.. files.Select(f => f.TryGetLocalPath()).OfType<string>()]);
        Composer.Focus();
    }

    private async Task<IReadOnlyList<string>> PickImagesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Attach images",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp"], MimeTypes = ["image/*"] }],
        });
        return [.. files.Select(f => f.TryGetLocalPath()).OfType<string>()];
    }

    private bool _swallowText;

    private void OnDigitForDialog(object? sender, KeyEventArgs e)
    {
        // Not while the image viewer holds the keyboard: the card behind it is out of sight
        if (Vm?.CurrentDialog is not { } dialog || Vm.IsImageViewerOpen || e.KeyModifiers != KeyModifiers.None) return;
        var digit = e.Key is >= Key.D1 and <= Key.D9 ? e.Key - Key.D0 : e.Key is >= Key.NumPad1 and <= Key.NumPad9 ? e.Key - Key.NumPad0 : 0;
        if (digit == 0) return;
        var focused = FocusManager?.GetFocusedElement();
        var typing = focused is TextBox box && !(ReferenceEquals(box, Composer) && string.IsNullOrEmpty(Composer.Text));
        if (typing || !dialog.TryPick(digit)) return;
        e.Handled = true;
        _swallowText = true; // the digit's text input must not land in the message box
        Dispatcher.UIThread.Post(() => _swallowText = false, DispatcherPriority.Background);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Esc closes what is on top first: a right-click menu, the image viewer, the shortcut sheet, then a request, the
        // settings page; only then does it stop omp
        if (e.Key == Key.Escape && Controls.ContextMenus.CloseOpen())
        {
            e.Handled = true;
            base.OnKeyDown(e);
            return;
        }
        if (e.Key == Key.Escape && Vm is { IsImageViewerOpen: true } viewer)
        {
            viewer.CloseImageViewerCommand.Execute(null);
            e.Handled = true;
            base.OnKeyDown(e);
            return;
        }
        if (e.Key == Key.Escape && Vm is { IsShortcutsOpen: true } sheet)
        {
            sheet.CloseShortcutsCommand.Execute(null);
            e.Handled = true;
            base.OnKeyDown(e);
            return;
        }
        if (e.Key == Key.Escape && Vm?.CurrentDialog is { } dialog && FocusManager?.GetFocusedElement() is Visual focused
            && DialogHost.IsVisualAncestorOf(focused))
        {
            // Esc inside the dialog closes the dialog only (the "say why" box first); elsewhere it stops the run (which closes it too).
            if (dialog.IsWritingFeedback) dialog.CancelFeedbackCommand.Execute(null);
            else dialog.DismissCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && Vm is { IsSettingsOpen: true } settingsOpen)
        {
            // Esc leaves the settings page (as its "Back to app")
            settingsOpen.CloseSettingsCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && EscapeStaysLocal())
        {
            // Esc in a search box, the preview, a terminal or the session card means "leave this", never "stop omp"
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && Vm is { Phase: OmpGui.ClientCore.SessionPhase.Running } && Vm.AbortCommand.CanExecute(null))
        {
            Vm.AbortCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.F12 && Vm is { } vm)
        {
            vm.ToggleDebugCommand.Execute(null);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }
}
