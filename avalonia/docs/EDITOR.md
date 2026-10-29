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
| Tab reorder | ◐ | ✅ drag | Tab menu *Move left / right*; no drag yet |
| Quick open (⌘P) | ✅ | ✅ | The command palette's files mode; opens in a page tab while the page is shown |
| Go to line | ✅ ⌃G | ✅ ⌃G / ⌘L | ⌘L is taken by "Focus the message box" (window level); accepts `line:column` |
| Find / replace in file | ✅ | ✅ | AvaloniaEdit search panel, ⌘F / ⌘H |
| Find in files | ✅ | ✅ | .NET search over git's file list (or a walk without git); skips heavy/hidden folders, >1 MB and binary files; 500-hit cap; no regex/replace-in-files |
| Multi-cursor / column select | ◐ | ✅ | Ctrl+Alt-click carets, Alt-drag column; no ⌘D "add next occurrence" |
| Syntax highlighting | ✅ | ✅ | TextMate grammars, light/dark themes |
| Folding | ◐ | ✅ | Indentation-based, not syntax-aware |
| LSP: diagnostics, hover, definition, rename, completion | ✅ | ✅ | `EditorLanguageService`, after the workspace is trusted |
| LSP: format document | ✗ | ✅ | Gap |
| Git change gutter | ✅ | ✅ | The buffer (unsaved edits too) against `git show HEAD:<file>`, line-diffed in-process 300 ms after typing; HEAD re-read on open, tab activation and save/reload |
| Diff view | ◐ | ✅ | The Files viewer's review diff (hunks, revert, comments); no side-by-side diff in the page |
| Save / external change / recovery | ✅ | ✅ | ⌘S, conflict banner (keep mine / reload), crash recovery of unsaved buffers |
| Autosave | ✗ | ✅ | Gap |
| Split view | ✗ | ✅ | Gap (the page and the right panel can show the same file side by side) |
| Minimap | ✗ | ✅ | Gap |
| Breadcrumbs | ✗ | ✅ | Gap (the tab tooltip shows the relative path) |
| AI: comment on lines → agent | ✅ | ◐ Cursor | *Comment…* in the editor's context menu adds a code mark to the message |
| Share selection with the team | — | ✗ | Owned by the team-sharing slice (*Share with team…*) |

## Shortcuts

⌘⇧O (Ctrl+Shift+O) toggles the Editor page. ⌘⇧E was the first choice, but it is already "Thinking effort".
