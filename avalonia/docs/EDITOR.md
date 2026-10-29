# Editor — audit against VS Code / Zed / Cursor

How Ombra's editor compares with VS Code, Zed and Cursor. It has two surfaces: the rail's **Editor** page (`Views/EditorPage`,
`ViewModels/EditorPageViewModel.cs`) and the right panel's Files viewer. Both host `Controls/DocumentEditor`
(AvaloniaEdit + TextMate) over the same `WorkspaceDocument` buffers. Each surface has its own editor control, caret and
scroll, but one buffer per file. That way an edit, its undo and its unsaved state are the same wherever the file is
shown, and the page and the panel never fight over one editor instance.

✅ = has it · ◐ = partial · ✗ = gap.

| Feature | Ombra | VS Code / Zed / Cursor | Note |
|---|---|---|---|
| File tree | ✅ | ✅ | The Files pane's tree (lazy, git marks, watchers), on the page's left |
| Tabs: several files, dirty dot, close | ✅ | ✅ | Page tabs; closing keeps unsaved buffers (listed under *Unsaved*) |
| Tab reorder | ✅ | ✅ drag | Drag along the strip (the tab takes the place of the one under the pointer, 6 px threshold); tab menu *Move left / right* |
| Quick open (⌘P) | ✅ | ✅ | The command palette's files mode; opens in a page tab while the page is shown |
| Go to line | ✅ ⌃G | ✅ ⌃G / ⌘L | ⌘L is taken by "Focus the message box" (window level); accepts `line:column` |
| Find / replace in file | ✅ | ✅ | AvaloniaEdit search panel, ⌘F / ⌘H |
| Find in files | ✅ | ✅ | .NET search over git's file list (or a walk without git), open files as their buffers are; match case, whole word, regex (.NET, per line, 1 s timeout); skips heavy/hidden folders, >1 MB and binary files; 500-hit cap |
| Replace in files | ✅ | ✅ | Preview per file (lines before/after, include/exclude), confirmation naming unsaved files; applied to the `WorkspaceDocument` buffers (open tabs follow) as one undo step per file; saved files are saved again, already-unsaved ones stay unsaved; conflicted/read-only files are skipped and named |
| Multi-cursor / column select | ◐ | ✅ | Ctrl+Alt-click carets, Alt-drag column. No ⌘D "add next occurrence" and no multi-cursor editing beyond AvaloniaEdit's rectangular/multi selection: AvaloniaEdit has one caret per text area and no API for independent carets, so true multi-cursor needs an editor core change — skipped |
| Syntax highlighting | ✅ | ✅ | TextMate grammars, light/dark themes |
| Folding | ◐ | ✅ | Indentation-based, not syntax-aware |
| LSP: diagnostics, hover, definition, rename, completion | ✅ | ✅ | `EditorLanguageService`, after the workspace is trusted |
| LSP: format document / selection | ✅ ⇧⌥F | ✅ | `textDocument/formatting` / `rangeFormatting` through the engine (`editor_document` methods `formatting`, `rangeFormatting`, `capabilities`); edits applied all-or-nothing as one undo step; disabled with the reason (untrusted, no server, server can't) on the menu row's tooltip |
| Git change gutter | ✅ | ✅ | The buffer (unsaved edits too) against `git show HEAD:<file>`, line-diffed in-process 300 ms after typing; HEAD re-read on open, tab activation and save/reload |
| Diff view | ✅ | ✅ | Page: *Diff vs HEAD* toggle per tab, the buffer (unsaved edits too) against HEAD as a unified diff in the app's `DiffView` card (read-only); the Files viewer's review diff keeps hunks, revert, comments |
| Save / external change / recovery | ✅ | ✅ | ⌘S, conflict banner (keep mine / reload), crash recovery of unsaved buffers |
| Autosave | ✅ | ✅ | Settings › General: off (default) / after 1 s / on focus change (editor loses the keyboard, or the window deactivates); both surfaces; never over a conflict |
| Split view | ✅ | ✅ | Two editor groups side by side (*Open to the side*), each its tabs; the same buffer in both; close a group. At most two groups, no drag between groups |
| Minimap | ✗ | ✅ | Skipped: AvaloniaEdit has no minimap and no second-view rendering of a document; a faithful one means rendering every visual line twice per frame, not worth its cost now |
| Breadcrumbs | ✅ | ✅ | Path segments from the project folder (click → shown in the tree), then the symbol at the caret from LSP `documentSymbol` (nested or flat) when the server lists symbols |
| AI: comment on lines → agent | ✅ | ◐ Cursor | *Comment…* in the editor's context menu adds a code mark to the message |
| Share selection with the team | — | ✗ | Owned by the team-sharing slice (*Share with team…*) |

## Shortcuts

⌘⇧O (Ctrl+Shift+O) toggles the Editor page. ⌘⇧E was the first choice, but it is already "Thinking effort".

⇧⌥F (Shift+Alt+F) formats the document, or the selection when there is one and the server formats ranges (VS Code's
binding). The page's own buttons (diff vs HEAD, open to the side, close group) sit at the right end of each group's tab
strip.
