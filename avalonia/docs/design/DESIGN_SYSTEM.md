# Design system

The client's own design system: its tokens, component styles and icons. This page says how the pieces are wired and
the conventions to follow when adding UI.

Files:

| File | Role |
|---|---|
| `src/OmpGui.App/Views/Tokens.axaml` | Design tokens: colours per theme, radius, type scale, shadows. Edited by hand |
| `src/OmpGui.App/Views/Styles.axaml` | Component styles (style classes) on top of the tokens |
| `src/OmpGui.App/Views/Icons.axaml` | Line icons (`StreamGeometry`, 24-unit grid) |
| `src/OmpGui.App/Controls/Icon.cs` | `ctl:Icon`, the control that draws them |
| `src/OmpGui.App/App.axaml` | Includes Tokens + Icons as resources, then `FluentTheme`, then Styles |

## Foundations

- **Colours**, light and dark: text, surfaces, fills, borders, status and a few component colours. Theme switching is
  Avalonia's `ThemeVariant`; every brush is a `DynamicResource`.
- **Radius**: 4 / 8 / 12 / 16 / 24 / full.
- **Type scale**: 17 / 15 / 13 / 11 for UI and reading text, 20 for the page title, 24 for the empty-session
  greeting, caps for section labels. The font is **Inter** (SIL Open Font License 1.1, bundled through the
  `Avalonia.Fonts.Inter` package, `.WithInterFont()` in `Program.cs`).
- **Shadows**: `GuiShadow` and `GuiShadowPopup` in light; none in dark, where a hairline separates surfaces.
- **Components**: button sizes small 32 / radius 8 and medium 44 / radius 12, filled inputs, status badges on the soft
  status colours, cards on surface 1 with radius 16. The client is a desktop window: it restyles Fluent's controls
  with the tokens instead of drawing its own.

## Tokens: three layers

`Tokens.axaml` has one `ThemeDictionaries` entry per theme (`Light`, `Dark`), each with three layers of brushes, plus
theme-independent resources after them.

1. **`Gui*`**: the client's semantic tokens, one `SolidColorBrush` each (`GuiTextPrimary`, `GuiSurface1`,
   `GuiStatusErrorSoft`, …). New XAML uses only these.
2. **Older alias keys** (`MutedTextBrush`, `PanelBorderBrush`, `DiffAddedBrush`, …) with `Gui*` values. Code
   that builds UI in C# looks them up by name: `Controls/DiffView.cs` (`Diff*Brush`, `MutedTextBrush`) and
   `Controls/MarkdownView.cs` (`PanelBorderBrush`, `DetailsBackgroundBrush`). Do not add new ones.
3. **Fluent keys** (`ButtonBackground`, `AccentButtonBackground`, `TextControlBorderBrushFocused`,
   `ComboBox*`, `CheckBox*`, `ToolTip*`, `ProgressBar*`, `SystemAccentColor`, `SystemControlFocusVisual*`, …) so
   stock controls follow the tokens without per-control styles.

Two tokens need a word:

- `GuiSidebar`: the page grey in light; in dark a step below the window, opaque, so the two panes separate.
- `GuiApprovalBackground` / `GuiApprovalBorder`: surface 1 with a warning border, in both themes (a soft warning
  wash hid the yellow Allow button in light and read as mud in dark).

**Adding a token:** add the key to both theme dictionaries (`Light` and `Dark`) in `Tokens.axaml`, then reference it with
`{DynamicResource …}`. Brushes and colours are
per-theme; radius and type sizes go after the theme dictionaries.

## Semantic tokens in use

Values are light / dark. Only keys referenced from `Styles.axaml` or `MainWindow.axaml` are listed; the rest of the
`Gui*` set feeds the Fluent keys.

| Token | Light / Dark | Used for |
|---|---|---|
| `GuiTextPrimary` | #000 80% / #FFF 90% | Window foreground, hovered icon buttons and links |
| `GuiTextSecondary` | #000 54% / #FFF 70% | `.muted`, `.caption`, `.label`, `.tertiary`, `.caps`, placeholders (at 85 %: 3.3:1 on white, not Fluent's half opacity), project name, icon buttons, tool output |
| `GuiTextTertiary` | #000 40% / #FFF 50% | icons and disabled states only (2.8:1 on light surfaces: never text a user must read) |
| `GuiTextLink` / `GuiTextLinkHover` | #126DF7 / #66A3FF | `link action`, Markdown links, running badge, link banner icon |
| `GuiTextError` | #F52222 / #FF4D4D | `chip danger`, recording mic (large or bold only) |
| `GuiTextSuccess` | #00A328 / #00E037 | diff gutter |
| `GuiTextWarning` | #EF8007 / #FAB619 | recovery banner icon |
| `GuiTextOnInverse` | #FFF / #000 | text on `inverse` buttons |
| `GuiWindow` | #FFF / #202020 | window background |
| `GuiPage` | #F6F7F8 / #202020 | settings page (`Border.page`), events panel |
| `GuiSidebar` | #F6F7F8 / #1A1A1D | sidebar, terminal panel |
| `GuiSurface1` | #FFF / #292929 | cards, section cards, composer, current session, New session button |
| `GuiSurface2` | #FFF / #333 | menus / popups, Jump to latest |
| `GuiFill1` | navy 6% / #FFF 10% | flat-button hover, user bubble, tool name, attachment, details, model list selection |
| `GuiFill1OnWindow` | #F6F7F8 / #2C2C2E | code blocks, command output |
| `GuiFill2` | navy 8% / #FFF 15% | tags, selected terminal tab, current menu item |
| `GuiInverse` (+`Hover`, `Pressed`) | #000 / #FFF | `inverse` buttons (Stop, Bypass permissions) |
| `GuiBlue` | #428BF9 | composer drag-over border |
| `GuiBorderSoft` | hairline | header / sidebar dividers, menus, tool rows, separators, splitters (a 1 px line centred in a wider transparent strip to grab) |
| `GuiBorder` / `GuiBorderStrong` | | composer border / composer border while focused |
| `GuiBorderDarkOnly` | transparent / #FFF 10% | card hairline (dark only) |
| `GuiStatusInfoSoft` | | running badge, link banner icon disc |
| `GuiStatusSuccessSoft` | | done badge |
| `GuiStatusErrorSoft` | | failed badge, `notice error`, `banner error`, `chip danger` |
| `GuiStatusWarningSoft` | | `notice warning`, recovery banner icon disc |
| `GuiStatusNeutralSoft` | | default status badge |
| `GuiStatusInfo` / `Success` / `Error` | | status-line dot (running / ready / error) |
| `GuiApprovalBackground` / `GuiApprovalBorder` | see above | approval card |
| `GuiShadow` | `0 5 20 #000 10%` / none | cards, composer, current session |
| `GuiShadowPopup` | `0 10 40 #000 14%` / none | menus |
| `GuiRadius4` / `8` / `12` / `16` / `Full` | 4 / 8 / 12 / 16 / 999 | 4: tags · 8: small buttons, details, notices · 12: `md` buttons, menus, tool rows, sessions · 16: cards, composer · full: chips, badges |
| `GuiFontL` / `M` / `S` / `Xs` | 17 / 15 / 13 / 11 | L: `.section`, dialog headline · M: `.body`, composer, `md` buttons · S: buttons, `.muted`, `.label` · Xs: `.caps` |
| `GuiFontXl` | 20 | `.page-title` |

Some sizes are still literal in `Styles.axaml` (12 for `.tertiary`, `.caption`, links and badges; 14 for inputs,
session and menu items). Fluent's own `ControlCornerRadius` is 8, `OverlayCornerRadius` 12 and
`ControlContentThemeFontSize` 14.

## Layout: Claude Code as the reference

Structure, placement and sizes follow Claude Code's desktop / web client (behaviour and proportions only — nothing
copied); colours, radius, type and components are the client's own (above).

| Area | Arrangement | Size |
|---|---|---|
| Sidebar | top row: sidebar toggle · name; New session; search; sessions **grouped by project** (folder header with a "new session here" button on hover) as one-line rows (running dot · title · age); bottom: Open folder… · Settings | 260 wide; top row 48 (level with the header); rows 32 / 36, radius 8; open item on fill 2 |
| Header | session title (rename) · project — panel toggles on the right (terminal; preview), their "on" state on fill 2; sidebar toggle and settings appear here only while the sidebar is hidden; no divider line | 48 high, 16 px from the sidebar |
| Conversation | one centered reading column; user messages right-aligned bubbles; assistant text plain; tool calls as one line each (status dot · tool · what it acts on, never raw JSON · chevron), consecutive calls with no gap; a running call pulses its dot and shows its newest 4 output lines under a pale bar; the output opens below; a reply that only calls tools takes no room; "Thinking…" pulses while the model thinks, then reads "Thought for 4s"; the message actions (Copy) sit under the reply that ends a turn, not under every message | 768 max width; bubble radius 16, max 600; tool line 30 high |
| Empty session | the app mark and "What should we work on?" (heading 5, 24 / 28) in the middle; the project is the folder chip in the composer | — |
| Setup | omp cannot start in a new session: the page shows only what to do — the app mark (onboarding) or a warning mark, a title, one sentence, one accent action and the alternatives, the raw error under *Show details*; no message box. Mid-conversation the same content is a card above the message box | 520 max |
| Questions | the card above the composer; choices numbered 1–9 (the digit picks), approvals Allow `1` / Deny `2`, close (×) in the card's corner | — |
| Composer | the message field on a card; attachments above the text (images, page comments as numbered chips); in the card under the text, left: "+" (an outlined 32 px circle mirroring Send; a menu: attach images, commands and skills, comment on the page); right: thinking effort, model, dictation, then a round send / stop button. Under the card, outside it (as Claude Code and Codex keep session settings out of the box): the project folder (new session only) and the permission mode on the left, the context gauge on the right, in 28 px quiet pills | radius 24; field min 48; toolbar buttons 32; send and "+" 32 circles, 10 px from the edges; row under the card 28 |
| Preview | right of the conversation, full height, resizable (`Controls/PreviewPanel`): back · forward · reload · address · open in the browser · close in a 48 px toolbar level with the header; local server addresses from tool output as chips; the native web engine of the OS (WebView2 / WKWebView / WebKitGTK), a card with "Open in browser" when there is none | 520 default, 320 min |
| Annotate (preview) | Codex's page comments: the 💬 toggle (`icon on` while active) puts the page in annotate mode — drawn by the page script in a closed shadow root: a 2 px blue outline with a dark tag (`button#buy 220×56`), a white comment card (radius 12, textarea, Cancel / accent Comment), numbered **pins** (22 px accent-yellow circles, dark number, white ring) and a dark hint pill at the bottom; the panel lists the comments under the page (pin number, comment, element in mono tertiary, ×) with Clear and an accent Send; the composer shows "N comments on the page" as an attachment chip | pins 22; list max 200 |
| Dictation | the mic button or Ctrl/⌘+Shift+Space turns the message field into the dictation bar (`Controls/DictationBar`): cancel · level waveform · time · finish (accent circle); Esc cancels, Enter inserts the text at the caret (never sends) | field height |
| Cards above the composer | recovery, sign-in link, tasks, the approval / question card; they scroll when the window is too low for them and the composer, which always stays on screen (a new question scrolls its answer buttons into view) | same 768 column |
| Activity line | in the column above the composer, in a slot that keeps its 20 px when idle (a run starting or ending moves nothing); while omp works or after an error: pulsing dot · what it does now ("Thinking", "Writing", "Running bash", "Waiting for your approval" in the warning colour) · elapsed time · "Esc to stop"; model, RPC and context in its tooltip | 12 px |
| Pet | a pixel pet (`Controls/PetPerch`, `Controls/PetView`, data in `Pets/`) on the composer's top edge, 20 px from its right end, its feet on the border; it has a band of its own (the composer's top margin grows by the pet's height), so it never covers the conversation, a card or a question; a click opens a speech bubble to its left (surface 2, radius 12) and never takes focus; hidden on the setup screen, the settings page and in a window under 520 px tall (the band goes with it). Art: 24×24 pet pixels (`PetArt`) with a one-pixel outline, three-step shading, a soft highlight, blush, 2×3 eyes with a glint and a soft contact shadow; a 12-pixel room on its left for effects. Motion (`PetLife`): eased breathing, blinks every 2–6 s, glances, an idle action every 8–20 s, each mood's own motion, eased changes between moods, a look at the pointer, a hop on click, a glance at the composer while typing; drawn nearest-neighbour at screen pixels, and it wakes only when the picture changes, only while shown in the active window; a mood that lasts (asleep, waiting, omp stopped) comes to rest on a still. Previews: `screens/pets-preview-light.gif`, `screens/pets-preview-dark.gif` | 48 (medium, 2 px per pet pixel) / 32 (small, 4/3) tall |
| Tasks | omp's todo list as one line above the composer: the task in progress, done / total, a chevron; open, the whole list with checkboxes (done: struck through; in progress: an info-coloured box with a dot) | 32 high line; list max 180 |
| New session | the greeting and the composer together in the middle of the page; with the first message the composer goes to the bottom | — |

**Keeping things in place.** The conversation stays on its latest message through resizes, panels opening (terminal,
tasks, a question, the preview) and rows growing — re-anchored inside the layout pass, so no frame is drawn at the old
offset — until the reader scrolls away (wheel, scroll bar, keys); then "Jump to latest" appears. Side panels never take
the conversation below 420 px: the sidebar gives way first, then the preview narrows to its 320 minimum; a window too
narrow for both shows the preview over the conversation until it is closed (`MainWindow.FitPanels`).

Model and thinking effort live in the composer, next to the message they apply to; the permission mode and the context
gauge sit in the row under it. The model list opens upwards from its chip. Each row sheds labels only as far as its
content needs to fit (`MainWindow.FitComposerToolbar`, measured): in the card a shorter model, the thinking pill as its
icon, the model as its icon, then no thinking pill; under it a shorter project, the permission pill as its icon, the
context without its percentage, the project as its icon. Voice and send always stay on screen.

## Components (Styles.axaml)

### Buttons

| Classes | Looks like |
|---|---|
| *(none)* | fill 1, primary text, 32 high, radius 8, 13 px medium |
| `accent` | yellow (`GuiAccent`) fill, dark text: the primary action |
| `md` | 44 high, radius 12, 15 px (combine: `accent md`) |
| `inverse` | black in light, white in dark |
| `flat` | no fill until hovered (fill 1) |
| `icon` | 32 × 32, no fill, secondary foreground → primary on hover; `icon wide` for icon + text |
| `link` | no chrome, 12 px secondary text → primary on hover |
| `link action` | blue link text (`GuiTextLink`) at 13 px |
| `chip` | full radius pill, 32 high, fill 1 (also `ComboBox.chip`); `chip danger` = error soft + error text |
| `chip ghost` | the composer's pills: no fill until hovered, secondary text |
| `send` | 32 px circle, icon only — `accent send` (Send), `inverse send` (Stop) |
| `round` | full radius for an `icon` button (attach, dictation) |
| `icon on` | a panel toggle whose panel is open: fill 2, primary icon |
| `segment` in `Border.segmented` | a 2–3 option choice: fill 1 track, the chosen segment raised off it (`GuiSegmentSelected`: white in light, #555 in dark, lighter than the track) with the small shadow; an unavailable segment is tertiary text on the track |
| `sidebar-action` | a flat 36 px row with an icon (New session, Open folder…, Settings); `current` = fill 2 |

`accent` is Fluent's `AccentButton` theme; its colours come from the Fluent keys in `Tokens.axaml`, not from a style
in `Styles.axaml`. Other button classes are scoped to one
place: `title`, `option` (dialog choices), `session`, `sidebar-action` (`primary` = New session), `menu-item`,
`ToggleButton.expander` (the chevron rotates 90° when checked), `ToggleButton.tool-row` (a whole tool line; its
`Icon.chevron` rotates when the output is open), `ToggleButton.todo-head` (the task line).

**On / off and done / not done.** A setting that is on or off is a **switch** (`ToggleButton.switch`: a 36 × 20
pill, fill 3 → accent when on, a white knob that slides), in a row with its name and description on the left —
as in Claude Code's settings; not a check box. A task that is done or not is a **checkbox** (`Border.md-check`, 16 px,
radius 4, a strong border; `checked` = inverse fill with a check; `active` = info border; `dropped` = pale
with a cross), drawn — never the ☑ / ☐ / ◐ glyphs, which every font draws differently.

### Inputs

`TextBox` is filled (`GuiFill1OnSurface1`), 44 high (medium; `search` and `sm` are small, 32), with a transparent border that turns `GuiBorderHover` on
hover and **inverse** (`GuiInverse`) on focus, where the fill switches to surface 1. `TextBox.search`
leaves room for a leading `ctl:Icon` in `InnerLeftContent`. The composer is the exception: `Border#ComposerBox` is the
field (surface 1, `GuiBorder`, radius 24, `GuiBorderStrong` on focus-within) and the `TextBox` inside stays
bare in every state.

### Cards and surfaces

- `Border.card`, `.dialog`, `.banner`, `.widgets`: surface 1, radius 16, `GuiShadow` (a shadow in light, a 1 px
  `GuiBorderDarkOnly` hairline in dark), padding 16,14 (`widgets` 14,10). `banner error` is flat error soft.
- **Approval card** (`Border.dialog.approval`): surface 1 with a `status.warning` border in both themes, no
  shadow; the shield icon is in the warning colour. It carries the shield icon and a `caption` line, a headline at `GuiFontL`, the command in
  `Border.details`, then **Allow** (`accent md`) and **Deny** (`md`) and a `link` Dismiss.
- `Border.menu`: surface 2, radius 12, pale border, popup shadow.
- `Border.notice`: transcript notices; plain text by default, `notice error` / `notice warning` get a soft status
  fill, radius 8 and padding 12,8.
- `Border.status` badges: full radius, 20 high, 12 px medium. `status running` (info soft / link text),
  `status done` (success), `status failed` (error), plain `status` neutral.
- Transcript: `Border.user-bubble` (fill 1, radius 16), tool lines (`ToggleButton.tool-row` with `Ellipse.tool-dot`
  in the status colour, `TextBlock.tool-name` 14 semibold, `.tool-summary` mono secondary, `.tool-state`) and their
  `Border.tool-output` (fill-1-on-window, radius 12), `Border.tool-tail` (a running tool's last lines beside a 2 px pale
  bar), `Border.command-output` and `Border.codeblock` (fill-1-on-window, radius 12, the language as a caps label and
  a copy **icon** that turns into a check for 1.5 s). Running dots (`tool-dot running`, `state-dot running`) and the
  live "Thinking…" pulse (opacity 1 ↔ 0.3, 1.1 s); nothing animates while idle.
- `Border.tag` (fill 2, radius 4), `Border.attachment` (fill 1, radius 12), `Border.tab` / `.tab.selected`.

### Sidebar and sessions

`Border.sidebar` is `GuiSidebar` with a pale right border. **New session** is its one filled button
(`sidebar-action primary`: surface 1 with a pale border, 40 high, kept when disabled); the other actions are flat. Session rows are
`Button.session` (radius 8, 14 px) under a `TextBlock.group-label` ("Recents"). The open session is `session current`:
surface 1 on the grey sidebar, no shadow. The list's own `ListBoxItem` hover/selection chrome is switched off.

### Settings page

`Border.page` (`GuiPage`) under a `Border.header` with a `TextBlock.page-title` and a `Done` (`md`) button. Each group
is a `Border.section-card`: surface 1, radius 16, padding 20,18, dark-mode hairline, **no shadow**. Inside:
`TextBlock.section` title (17 px semibold), `.muted` description, `TextBlock.label` above fields, 1 px `GuiBorderSoft`
separators, and at most one `accent md` action (Save and restart omp).

### Type classes

`.muted` (secondary, 13), `.tertiary` (12), `.body` (15 / line 24, message text), `.mono`, `.error`, `.warning`,
`.caption` (12 semibold secondary), `.label`, `.section`, `.page-title`, and **`.caps`**: 11 px semibold, letter
spacing 0.8, tertiary, used for section labels (SESSIONS, RPC EVENTS, ASK BEFORE RUNNING TOOLS, todo phase names).
The style does not uppercase; write the label text in capitals.

## When to use which

- **One primary action per card.** `accent` marks the action the card exists for (Allow, Yes, Submit, Send, Install,
  Save and restart omp, Download). Alternatives next to it are default buttons (Deny, No, Cancel, Copy); ways out are
  `flat` (Dismiss, Not now, Show in folder) or `link`.
- **Use `md` for the buttons of a card's main decision** (approval, confirm, recovery banner, settings Save / Done);
  small (default) elsewhere.
- **`inverse` stops or overrides**: Stop in the composer, "Bypass permissions" in the permission-mode confirmation. It is not a
  second primary.
- **Destructive or risky state uses the error colours**: `chip danger` while approvals are off, `notice error`
  around the bypass confirmation, `status failed`, `banner error`. There is no red button class.
- **Status is shown with soft fills plus a mark in the status colour, never by coloured small text**: badge text is
  primary with a coloured dot (`Border.status` > `Ellipse`); error and warning lines are primary text with an
  `IconAlert` in `GuiStatusError` / `GuiStatusWarning` (`Icon.error-mark` / `Icon.warning-mark`). Text in a status
  colour on its own pale fill measures 2.5–3.9:1; graphics need only 3:1.
- **Elevation, not borders, separates surfaces in light**; dark mode relies on the `GuiBorderDarkOnly` hairline
  because there are no shadows there.
- Reading text uses `body`/`GuiFontM`; dense UI uses 13–14 px; `GuiFontXl` only for the page title.

## Icons

Line icons drawn for this client (no third-party set), `StreamGeometry` resources in `Icons.axaml`:

`IconMenu` · `IconPlus` · `IconFolder` · `IconSearch` · `IconTerminal` · `IconSettings` · `IconArrowUp` · `IconStop` ·
`IconAttach` · `IconMic` · `IconClose` · `IconChevronDown` · `IconChevronRight` · `IconCopy` · `IconShield` ·
`IconSparkle` · `IconCheck` · `IconLink` · `IconAlert` · `IconPanelLeft` (sidebar toggle) · `IconCompose` (new session) ·
`IconBrain` (thinking effort) · `IconArrowLeft` · `IconArrowRight` · `IconReload` · `IconExternal` · `IconGlobe` (preview).

```xml
<ctl:Icon Data="{StaticResource IconTerminal}" Size="20" />
<ctl:Icon Data="{StaticResource IconStop}" Size="16" Filled="True" />
```

- `ctl` is `xmlns:ctl="using:OmpGui.App.Controls"`.
- Paths are drawn on a **24-unit grid**; `Size` (default 18) scales them. The stroke is **1.75 px** at every size
  (`StrokeThickness` is in screen pixels), with round caps and joins; `Filled="True"` also fills the shape.
- The icon is stroked in the **inherited foreground** (`TextElement.Foreground`), so it follows hover, pressed and
  disabled states of the button around it and both themes. To tint one, set `TextElement.Foreground` on it or its
  container (as the recovery and link banners do).
- Sizes in use: 20 in header and composer icon buttons, 18 in the sidebar and banner discs, 14–16 inline in chips,
  menus and small buttons.

**Adding an icon:** draw it on a 24 × 24 grid with open strokes (no fills unless it is meant for `Filled`), keeping
about 3 units of padding like the existing ones; add `<StreamGeometry x:Key="IconName">…</StreamGeometry>` to
`Icons.axaml`; use it through `ctl:Icon`. Do not embed colours.

## Accessibility

- Every **icon-only button needs `AutomationProperties.Name`** (and a `ToolTip.Tip`); the icon has no text for screen
  readers. Examples in `MainWindow.axaml`: *Toggle sessions*, *Terminal*, *Settings* (header), *Attach images*,
  *Dictate* (composer), *Remove attachment*, *Close terminal*, *Hide terminal panel*, and the tool-row expander
  (*Show tool output*). Buttons whose content is a template (sessions, menu items, dialog options) bind it to their
  label.
- **Focus rings use inverse**: `SystemControlFocusVisualPrimaryBrush` = `GuiInverse` (black in light, white
  in dark) with `GuiWindow` as the secondary ring, and a focused `TextBox` gets a `GuiInverse` border. Both are
  set once through Fluent keys in `Tokens.axaml`; no control overrides its focus visual.
- Keep keyboard access keys on dialog buttons (`<AccessText Text="_Allow" />`, `_Deny`, `_Yes`, `_No`, `Dis_miss`).
- Text colours come only from the `GuiText*` tokens (and `GuiTextOn*` on filled buttons), never from literal colours,
  so both themes keep their contrast pairs.

## Screens

Rendered by `DesignGalleryTests` (headless, 1180 × 760).

| | Light | Dark |
|---|---|---|
| Transcript | ![](screens/after/design-transcript-light.png) | ![](screens/after/design-transcript-dark.png) |
| Approval | ![](screens/after/design-approval-light.png) | ![](screens/after/design-approval-dark.png) |

More: [running command, light](screens/after/design-running-light.png) ·
[running command, dark](screens/after/design-running-dark.png) ·
[preview, light](screens/after/design-preview-light.png) ·
[preview, dark](screens/after/design-preview-dark.png) ·
[dictation, light](screens/after/design-dictation-light.png) ·
[new session, light](screens/after/design-new-session-light.png) ·
[new session, dark](screens/after/design-new-session-dark.png) ·
[tasks, light](screens/after/design-todos-light.png) ·
[first run, light](screens/after/design-first-run-light.png) ·
[first run, dark](screens/after/design-first-run-dark.png) ·
[settings, light](screens/after/design-settings-light.png) ·
[settings, dark](screens/after/design-settings-dark.png).

To re-render (from `avalonia/`):

```sh
taskset -c 1-3 dotnet build tests/OmpGui.Tests
OMPGUI_SHOT_DIR=$PWD/docs/design/screens/after taskset -c 1-3 \
  dotnet test tests/OmpGui.Tests --no-build --filter FullyQualifiedName~DesignGallery
```

The test writes every screen above, including the new-session and tasks screens.
