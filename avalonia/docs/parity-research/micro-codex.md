# Микро-UX: Codex / ChatGPT desktop → Ombra

**Источники.** Changelog ChatGPT & Codex (`/tmp/cl/codex.txt`, все записи; оригинал: https://learn.chatgpt.com/codex/changelog), раздел «Codex app 26.xxx», «ChatGPT desktop 26.7xx–26.9xx», «Codex CLI 0.15x–0.162» (TUI-мелочи, у которых есть GUI-аналог; ссылки на PR: `https://github.com/openai/codex/pull/<N>`). Документация приложения: [commands](https://learn.chatgpt.com/docs/reference/commands), [settings](https://learn.chatgpt.com/docs/reference/settings), [notifications](https://learn.chatgpt.com/docs/notifications), [code-review](https://learn.chatgpt.com/docs/code-review) (раздел «Navigating the review pane»), [prompting](https://learn.chatgpt.com/docs/prompting) («Steering and queuing»), [integrated-terminal](https://learn.chatgpt.com/docs/integrated-terminal), [projects](https://learn.chatgpt.com/docs/projects), [browser](https://learn.chatgpt.com/docs/browser), [pets](https://learn.chatgpt.com/docs/pets).

**Метод.** Каждый пункт проверен по коду `avalonia/src/Ombra.App` (grep/read; файл + символ/строка). Статусы: **Have** / **Partial** / **Missing** / **Unclear**. Крупные фичи из `docs/PARITY_ROADMAP.md` (Find в чате, палитра ⌘K/⌘P, архив, ⌘1–9 и ⌘[ ⌘], история промптов ↑, кликабельные ссылки в карточках, rich HTML copy, reduced motion, зум UI, tray/dock-бейдж, deep links, настраиваемые шорткаты, поиск в настройках, Send now, Quote/Ask по выделению, Mermaid/LaTeX) сюда **не** включены; упомянуты только мелкие детали вокруг них.

**Ограничение по анимациям.** В приложении почти нет `Transitions`: только переключатель (`Styles.axaml:512–519`) и посадка питомца (`Styles.Pets.axaml:15–16`). Причина записана в `Controls/Pulse.cs:10–23`: на macOS каждый кадр Avalonia.Native стоит ≈4 мс CPU, поэтому постоянные анимации делаются ступенчато через `Pulse`. Короткие разовые переходы (100–150 мс) тратят кадры только пока идут. Поэтому все пункты раздела «Motion» ниже — именно такие разовые переходы, без бесконечных стилевых анимаций.

---

## 1. Sidebar

| Микро-взаимодействие | Где у них (источник) | У нас (статус + доказательство) | Что сделать | Effort | P |
|---|---|---|---|---|---|
| Двойной клик по строке чата → переименование | Codex app 26.203 «thread renaming on double-click in the thread list» | **Missing**: `DoubleTapped` есть только в `Controls/DiffView.cs:360`; переименование строки — только через меню `Views/Sidebar/SessionRowMenu.axaml` → `RenameSessionItemAsync` (`MainViewModel.Sessions.cs:669`) | `DoubleTapped` на `Panel.session-row` → `RenameSessionItemCommand` (команда уже открывает чат и поле имени в заголовке) | S | P1 |
| «Пометить как непрочитанное» вручную (⌘⇧U) | [commands](https://learn.chatgpt.com/docs/reference/commands): Mark chat as unread ⌘⇧U | **Missing**: `OpenSession.Unread` ставится только в `CheckAttention` (`MainViewModel.Attention.cs:81`), в меню строки этого пункта нет | Пункт «Mark as unread / Mark as read» в `SessionRowMenu` + шорткат; при открытии чата флаг снимается, как сейчас | S | P2 |
| Снять все отметки непрочитанного (⇧Esc, «Mark all as read») | commands: Clear all unread ⇧Esc; [notifications](https://learn.chatgpt.com/docs/notifications): Mark all as read | **Missing** | Команда сбрасывает `Unread` у всех `OpenSession` и вызывает `UpdateSessionStatuses()`; пункт в контекстном меню заголовка группы | S | P3 |
| «Show in Finder / Explorer» из меню чата | Codex app 26.313 «Open in Finder/Explorer/File Manager action from thread menus» | **Partial**: у проекта есть `RevealCommand` (`MainWindow.axaml:197–203`); в `SessionRowMenu` только Rename / Pin / Copy path / Delete… | Добавить в `SessionRowMenu` Reveal (cwd чата) и «Open terminal here»; подпись брать из `SessionGroupViewModel.RevealLabel` | S | P2 |
| Шорткаты на текущий чат: Rename ⌘⌥R, Pin ⌘⌥P | commands (Chats) | **Missing**: в `KeyboardShortcuts.For` и `MainWindow.OnLoaded` (`MainWindow.axaml.cs:624–638`) их нет | Два `KeyBinding` на `StartRenameCommand` / `TogglePinCommand` и строки в листе шорткатов | S | P2 |
| После удаления выделение уходит на соседнюю строку | CLI #50505 «Keep Command Center selection adjacent after task removal», #45978 | **Partial**: при удалении открытого чата стартует новый пустой чат (`MainViewModel.Session.cs:579`: «A new session started in this project») | Открывать соседний чат (следующий, иначе предыдущий) в порядке `CycleSession` (`MainViewModel.Sessions.cs:714`); новый чат — только если соседа нет | S | P2 |
| Шорткаты в строках контекстных меню | commands: у каждого действия свой шорткат, меню Codex их показывает | **Missing**: `ContextMenus.Row` (`Controls/ContextMenus.cs:75–90`) рисует только иконку и подпись; XAML-меню такие же | Необязательная колонка `Gesture` в `MenuAction` (текст из `KeyboardShortcuts.Text`) и третья колонка в XAML-строках | S | P3 |
| Порядок проектов перетаскиванием | Codex app 26.609 «fixed manual project ordering» | **Missing**: в сайдбаре нет `DragDrop` (единственный drop-обработчик — `MainWindow.axaml.cs:78–81`, файлы в composer) | Перетаскивание заголовка группы; порядок хранить в project prefs рядом с pins (`ProjectPrefs()`) | M | P3 |
| Относительное время в строке обновляется само | ChatGPT iOS 1.2026.237 «simpler date sections»; CLI #47321 (системный формат часов) | **Have**: `SessionItemViewModel.When` и `Relative()` (`SessionItemViewModel.cs:34–93`), пересчёт раз в минуту; полная дата в tooltip в текущей культуре | — | — | — |
| Точка статуса строки (работает / ждёт ответа / новый ответ / ошибка) с подсказкой | iOS 1.2026.181 «Needs input status»; 26.727 Activity view | **Have**: `session-dot`, `session-hand`, `StatusText` (`MainWindow.axaml:286–292`) | — | — | — |
| Действия строки по наведению (pin, ⋯) вместо времени, видны и с клавиатуры | Codex sidebar hover | **Have**: `session-actions` (`MainWindow.axaml:297–312`), `Controls/SessionRowActions.cs` (focus-visible / flyout-open) | — | — | — |
| Ширина сайдбара: перетаскивание, двойной клик сбрасывает, значение сохраняется | Codex app 26.318 (ресайз дерева в процентах) | **Have**: `SidebarGrip` с `ClickCount == 2` (`MainWindow.axaml.cs:439–442`), `SaveSidebarWidth` | — | — | — |
| Esc в поиске сессий сначала очищает запрос | CLI #50503 (Enter принимает, Esc отменяет поиск) | **Have**: `MainWindow.Escape.cs:27–30` | — | — | — |

## 2. Transcript: строки и сообщения

| Микро-взаимодействие | Где у них (источник) | У нас (статус + доказательство) | Что сделать | Effort | P |
|---|---|---|---|---|---|
| Контекстное меню у пути к файлу в ответе: Open, Open in editor, Reveal, Copy path, Add to message | Codex app 26.206 «file-reference action to reveal files in your OS file manager»; CLI #49564 (путь копируется простым текстом) | **Missing**: у path-ссылки в `MarkdownView` есть только курсор-рука и tooltip-превью (`MarkdownView.cs:615–617`, `Controls/PathLinks.cs:113–115`); правый клик открывает меню всего ответа | `ContextMenus.ForPath(target)` с командами `FilesViewModel` (`RevealInFileManagerCommand`, `CopyPathCommand`, `AddToMessageCommand`, редакторы как в `ProjectMenuButton`) | S | P1 |
| http-ссылка: «Open in Preview» и ⌘-клик для внешнего браузера | Codex app 26.506 «opening modified external links outside the in-app browser»; [browser](https://learn.chatgpt.com/docs/browser): «open a URL from the chat» | **Partial**: `ContextMenus.ForLink` (`ContextMenus.cs:35–37`) — Open link (системный браузер) и Copy link | Добавить пункт «Open in Preview» (`PreviewViewModel`); обычный клик по localhost открывать в Preview, ⌘/Ctrl-клик — снаружи | S | P2 |
| Меню картинки: Copy image, Save as…, Show in Finder | iOS 1.2026.139 «save or copy rendered images»; Codex app 26.429 «zoom and download controls to the image lightbox» | **Partial**: в `ImageMenu` (`MainWindow.axaml:91–102`) только «Open»; Copy есть лишь кнопкой во viewer (`ImageViewer.axaml.cs:27,48–53`) | Добавить Copy (тот же `SetBitmapAsync`), Save as… (`SaveFilePickerAsync`) и Reveal для картинок с путём | S | P2 |
| Просмотрщик: скачать / сохранить, зум колесом и щипком | Codex app 26.429 | **Partial**: есть только переключение fit ↔ actual (`ToggleImageZoom`, `MainViewModel.Images.cs:82`), ←/→, Space, Esc, клик по фону закрывает | Кнопка Save; ⌘ + колесо и pinch — шаги 25–400 % | M | P3 |
| «Copied» во viewer не локализован | i18n-согласованность | **Bug**: `ImageViewer.axaml.cs:52` `vm.ViewerNote = "Copied"` без `Loc.T` | `Loc.T("Copied")` | S | P2 |
| Меню у карточки инструмента: Copy command, Copy output, Open file, Reveal, Expand all | CLI #49564; Codex app 26.206 | **Missing**: у шаблона `ToolRowViewModel` (`MainWindow.axaml:562+`) нет `ContextFlyout`, правый клик попадает в пустоту | Flyout на `StackPanel.tool` с командами строки; для файловых инструментов — меню пути из пункта выше | S | P1 |
| «Copy error / Copy details» у ошибки ответа и notice | Codex app 26.313 «resume and thread error toasts with clearer details» | **Missing**: в `ReplyError` и `NoticeRowViewModel` (`MainWindow.axaml:421–428, 524–532`) только `SelectableTextBlock`; копировать можно, только выделив текст вручную | Иконка копирования по наведению и пункт меню; копировать полный текст, включая скрытый «Show details» | S | P2 |
| Развернуть / свернуть все карточки разом (Alt/Option-клик по шеврону) | Codex web 2025-06-13 «option-click a code diff header to expand/collapse all»; iOS 1.2026.160 «expand or collapse all diffs» | **Missing**: у `IsExpanded` в `ToolRowViewModel` и групп переключение только по одной карточке | Alt-клик по `tool-row` / `diff-collapse` применяет состояние ко всем строкам хода (или ко всему чату) | S | P2 |
| Позиция прокрутки своя у каждого чата | Codex app 26.406 «Preserved thread scroll position per conversation» | **Have**: `OpenSession.ScrollAnchor`, `ChatScrollController.Save/Restore` (`ChatScrollController.cs:77–98`) | — | — | — |
| Ответ рендерится по мере стрима, не дожидаясь перевода строки | CLI #45135 | **Have**: `MarkdownView` перерисовывает последний блок раз в кадр (`MarkdownView.cs:22–26, 127`) | — | — | — |
| Строка конца хода: длительность, «Stopped after…», без префикса «done» | CLI #48807, #46067, #48830 | **Have**: `TurnEndRowViewModel.Line` (`RowViewModels.cs:698–705`), «Interrupted» (`RowViewModels.cs:229`) | — | — | — |
| Длинное сообщение пользователя складывается, «Show more» | iOS 1.2026.251 «compact preview and expand on tap» | **Have**: `FoldLabel` / `IsLong` (`MainWindow.axaml:493`) | — | — | — |
| Цветной квадратик рядом с `#rrggbb` в inline-коде | Codex app 26.602 «hex color swatches» | **Missing**: grep `swatch` пуст | В `MarkdownView.InlineOf` для `CodeInline` вида `#rgb` / `#rrggbb` / `rgb()` ставить 10×10 `Border` перед текстом | S | P3 |
| Предупреждение о смене модели (fallback / downgrade) | Codex app 26.217 «warning when the selected model is downgraded» | **Partial**: `ConversationState.cs:359` пишет Info-notice `"Continued with {model}."` — без локализации и не уровня warning | Notice уровня warning, через `Loc` (ключ в App), с названием выбранной и фактической модели | S | P2 |
| Shift-клик расширяет выделение | CLI #47414 | **Have** в `CodeView` (`CodeView.cs:310`); **Unclear** в `SelectableTextBlock` сообщений (поведение Avalonia по умолчанию в коде не проверено) | Проверить вручную; если не работает — обработать Shift в `PointerPressed` | S | P3 |
| Курсор-рука и hover-цвет у ссылки | CLI #48827 | **Have**: `HyperlinkButton.md-link:pointerover` (`Styles.Session.axaml:845–850`); рука — из стиля Fluent `HyperlinkButton` [INFERENCE: в нашем коде не задаётся] | — | — | — |

## 3. Карточки инструментов и diff

| Микро-взаимодействие | Где у них (источник) | У нас (статус + доказательство) | Что сделать | Effort | P |
|---|---|---|---|---|---|
| Клик по фону заголовка diff сворачивает / разворачивает | [code-review](https://learn.chatgpt.com/docs/code-review): «Clicking the file name background expands or collapses the diff» | **Partial**: сворачивает только кнопка `diff-collapse` (`DiffView.cs:300–308`), клик по пути открывает Files pane | `Tapped` на `diff-header` (кроме path-кнопки) вызывает `collapse()`; курсор-рука на фоне | S | P2 |
| ⌘-клик по строке diff → открыть во внешнем редакторе на этой строке | code-review: «Clicking a single line while holding Command opens the line in your chosen editor» | **Partial**: двойной клик открывает Files pane на строке (`DiffView.cs:360`) | ⌘/Ctrl-клик → `FileOpeners` с `:line` в редакторе проекта | S | P3 |
| «+» по наведению на строку diff → комментарий | code-review: «Hover over the line… select the + button» | **Have** в Files pane (`FilesPane.axaml:359–378`, Enter добавляет комментарий в сообщение); **Missing** в diff-карточке транскрипта (`DiffView`) | Тот же «+» в `DiffView.Build`; переиспользовать `MainViewModel.AddCodeComment` | M | P3 |
| Меню diff-карточки: Copy patch, Copy path, Open in editor, Reveal | Codex 2025-05-22 «options to copy git patches»; 26.401 «review file tree open menu» | **Missing**: у `diff-card` нет `ContextFlyout` | Собрать unified-патч из `FileChange` в `ContextMenus.Menu` | S | P2 |
| Действия по наведению на строке инструмента (Copy) | консистентность: у пользовательских и ассистентских строк hover-действия есть (`row-action`, `MainWindow.axaml:458–468, 534–540`) | **Missing** у `tool-row` | Иконка Copy (команда или вывод) справа, как `row-action` | S | P3 |
| Остановленная команда не выглядит запущенной | Codex app 26.226 | **Have**: `IsInterrupted`, пунктирный круг (`MainWindow.axaml:582`; `RowViewModels.cs:402`) | — | — | — |
| «… N more lines» в diff-карточке не локализовано | i18n-согласованность | **Bug**: `DiffView.cs:240` `$"… {rows.Count - MaxRows} more lines"` | `Loc.F` (как «… +{0} lines» в `ToolRowViewModel`) | S | P3 |
| Липкие заголовки консоли и diff, tooltip только у обрезанного текста | Codex app 26.401 «sticky review… header overlap» | **Have**: `StickyHeader`, `TrimmedToolTip` (`MainWindow.axaml:574, 627`) | — | — | — |

## 4. Composer

| Микро-взаимодействие | Где у них (источник) | У нас (статус + доказательство) | Что сделать | Effort | P |
|---|---|---|---|---|---|
| Очередь: редактировать любой элемент кликом, менять порядок перетаскиванием | Codex app 26.217 «drag-and-drop to reorder queued messages»; [prompting](https://learn.chatgpt.com/docs/prompting): «edit, reorder, send, or delete» | **Partial**: × удаляет, ↑ в пустом поле редактирует только последний (`EditLastQueued`, `MainWindow.axaml.cs:904`; `QueuedList` `MainWindow.axaml:1255–1284`) | Клик по `queued-text` возвращает элемент в поле; DnD внутри `QueuedList` (порядок — через `queue_update`, если omp его отдаёт, иначе клиентский) | M | P2 |
| Длинный элемент очереди раскрывается на месте | iOS 1.2026.251 «Long queued prompts show a compact preview and expand on tap» | **Partial**: одна строка и tooltip (`MaxLines="1"`, `MainWindow.axaml:1276`) | Шеврон «показать полностью», `MaxLines` 1 ↔ 6 | S | P3 |
| Multi-line ответ отправляется по ⌘/Ctrl+Enter | Codex app 26.609 «Cmd+Enter and Ctrl+Enter as shortcuts for submitting custom approval feedback» | **Partial**: поле «Deny and say why» однострочное, Enter отправляет (`MainWindow.axaml:1123–1128`); у editor-ответа (`AcceptsReturn`, `MainWindow.axaml:1176–1178`) шортката отправки нет | `AcceptsReturn` для feedback и `KeyBinding` ⌘/Ctrl+Enter → `SendFeedbackCommand` / `SubmitCommand`; подсказка в placeholder | S | P2 |
| Подсказки шорткатов в нотации платформы (⌥ / ⇧ / ⌃ на macOS) | CLI #49804, #49136 | **Partial**: лист шорткатов форматируется правильно (`KeyboardShortcuts.Keys`), но tooltip захардкожены: «(1 or Alt+A)» (`MainWindow.axaml:1089`), «Shift+Tab switches» (`:1668, :1707`), «Ctrl/⌘+Shift+S» (`PreviewPanel.axaml`), «Ctrl+`» | Подставлять текст через `KeyboardShortcuts.Text` (как `Steer`/`CommentOnPage`) | S | P2 |
| Шорткат открытия выбора модели (⌃⇧M) и недавних моделей | commands: Open model picker ⌃⇧M, recent models ⌃⌘M | **Missing**: сам пикер с клавиатурой работает (фильтр, стрелки, Enter, Esc — `MainWindow.axaml.cs:86–96, 779–813`), но шортката открытия нет | `KeyBinding` → открыть `ModelPopup`; учесть, что у нас ⌘⇧M занят device mode (`MainWindow.axaml.cs:632`) | S | P2 |
| Подсказка при перетаскивании: «Drop to attach N files» | Codex app 26.205 «Attach or drop any file type» | **Partial**: только подсветка рамки `ComposerBox.dragover` (`Styles.Composer.axaml:19–22`, `MainWindow.axaml.cs:975–980`) | Оверлей с текстом и числом файлов; при неподходящем типе — `DragDropEffects.None` и красная рамка | S | P3 |
| Захардкоженные тексты: ошибка буфера, заголовки диалогов выбора | i18n-согласованность | **Bug**: `"Could not read the clipboard: "` (`MainWindow.axaml.cs:961`), `"Attach images"` (`:995`), `"Open a project folder"` (`:861`), `"Save diagnostics"` (`:849`) | Пропустить через `Loc.T` | S | P2 |
| Черновик своего у каждого чата (текст, вложения, комментарии) | Codex app 26.506 «Preserved in-progress message edits across thread switches»; 26.908 «Keep unfinished comments… when you switch chats» | **Have**: `OpenSession.ComposerText` / `HasNoDraft` с `CodeComments` (`OpenSession.cs:54`) | — | — | — |
| Подсказка «· Esc to stop» прямо над composer | CLI #47096 | **Have**: `ActivityLine` (`MainWindow.axaml:1045–1054`) | — | — | — |
| Фокус возвращается в composer после выбора / диктовки / drop | iOS 1.2026.267 «keep the keyboard open after sending» | **Have**: `FocusComposerRequested`, `RestoreLostFocus`, `ModelPopup.Closed` (`MainWindow.axaml.cs:89–95, 519–524, 762–767, 988`) | — | — | — |
| Утверждение по Enter, отклонение по Esc | commands: Approve ⏎, Decline Esc | **Partial**: 1–9, Alt+A / Alt+D, Esc = «Dismiss» (ответа нет, а не Deny; `MainWindow.axaml.cs:1042–1049`) | Решить семантику Esc; при фокусе на карточке подсвечивать Allow как действие по умолчанию для Enter | S | P3 |

## 5. Панели (Files, Plan, Tasks, Terminal, Preview)

| Микро-взаимодействие | Где у них (источник) | У нас (статус + доказательство) | Что сделать | Effort | P |
|---|---|---|---|---|---|
| Preview с клавиатуры: ⌘R reload, ⌘L адресная строка, ⌘←/→ назад / вперёд, ⌘⇧C copy URL, ⌘T новая вкладка | commands (Workspace and browser) | **Missing**: в `PreviewPanel.axaml` из шорткатов только Enter в адресной строке (`:85–87`); кнопки Back / Forward / Reload есть | `KeyBindings` на `PreviewPanel` (работают только пока фокус в панели) | S | P2 |
| ⌘W сначала закрывает активную вкладку (preview / терминал), потом окно | commands: «Close current tab or window ⌘W» | **Partial**: ⌘W — всегда `Close` окна (`MainWindow.Menus.cs:55`) | Если фокус в Preview или Terminal — `CloseTabCommand` / `CloseTerminalCommand` | S | P2 |
| Reopen closed tab ⌘⇧T | commands | **Missing**; конфликт: у нас ⌘⇧T = Background tasks (`MainWindow.axaml.cs:636`) | Стек закрытых URL в `PreviewViewModel`; клавишу выбрать без конфликта | M | P3 |
| Меню вкладки: Close, Close others (⌘⌥W), Duplicate, Copy URL, Reload | commands: Close other tabs ⌘⌥W | **Missing**: у вкладок Preview и Terminal нет `ContextFlyout` | `ContextMenus.Menu` на элементе вкладки | S | P2 |
| Порядок вкладок перетаскиванием | Codex app 26.409 «drag-and-drop tab reordering»; 26.609 «Browser tab dragging» | **Missing** | DnD в `TabStripPanel` / `TabStripRow` | M | P3 |
| Ширина вкладок не меняется, пока закрываешь их мышью | ChatGPT desktop 26.908 «Keep tab widths… stable as you close browser tabs» | **Missing**: `TabStripPanel` (`TabStripPanel.cs:7–13`) пересчитывает ширины после каждого закрытия, и × уезжает из-под курсора | Фиксировать `_widths`, пока указатель над полосой; отпускать на `PointerExited` | S | P3 |
| Вкладки терминала как у Preview: переименование двойным кликом, меню, общий стиль | Codex app 26.410 «terminal tabs per thread»; CLI #50216 (переименование) | **Partial**: горизонтальный `StackPanel` из link-кнопок и × (`MainWindow.axaml:814–833`), не `TabStripPanel` | Перевести на `TabStripPanel` + меню + переименование | S | P3 |
| Files: «Open in ‹editor›» прямо в меню строки | Codex app 26.204 (Zed, TextMate); code-review: «Clicking a file name… opens in your chosen editor» | **Partial**: в меню строки есть Open / Add to message / Open in default app / Reveal / Copy path / Copy relative (`FilesPane.axaml:16–63`); редакторы — только в меню проекта (`ProjectMenuButton.axaml:56–60`) | Подменю редакторов из `ProjectMenuButton` в `FileMenu` | S | P3 |
| Files: «+» по наведению для комментария, Revert hunk, Revert file с подтверждением | code-review (Inline comments, Staging and reverting) | **Have**: `FilesPane.axaml:317–378` | — | — | — |
| Tasks: свой цвет или identicon у каждого субагента | Codex app 26.527 «stable identicons for background subagents»; CLI #44857 | **Missing**: в `TasksPane.axaml` у строки только заголовок и текст | Цвет из хэша id (из палитры токенов) — точка или аватар в строке и в упоминаниях | S | P3 |
| Пустые состояния панелей с действием и подсказкой | Codex app 26.506 «Hooks settings reachable… before configured» | **Have**: Terminal (`MainWindow.axaml:861–873`), Files (`FilesPane.axaml:198–200`), Plan (`PlanPane.axaml:56–58`), Tasks (`TasksPane.axaml:16–21`) | — | — | — |

## 6. Settings

| Микро-взаимодействие | Где у них (источник) | У нас (статус + доказательство) | Что сделать | Effort | P |
|---|---|---|---|---|---|
| Уведомления: Never / Only in background / Always, отдельные переключатели для вопросов и разрешений | [notifications](https://learn.chatgpt.com/docs/notifications), [settings](https://learn.chatgpt.com/docs/reference/settings) | **Partial**: один `NotificationsEnabled` (`MainViewModel.Attention.cs:18`, `MainViewModel.Settings.cs:358`) | Перечисление режима + два bool; проверять в `CheckAttention` | S | P2 |
| Prevent sleep while running (macOS / Linux) | settings: General | **Missing**: grep `caffeinate`, `IOPMAssertion`, `SetThreadExecutionState` пуст | Пока хоть один чат в `Running`: macOS — `IOPMAssertionCreateWithName`, Windows — `SetThreadExecutionState`, Linux — `systemd-inhibit` / D-Bus; переключатель в General | M | P2 |
| Поиск в листе шорткатов, в том числе по нажатию клавиш | Codex app 26.527 «keypress search and a reset-all action» | **Partial**: `ShortcutsSheet` — статический список из `KeyboardShortcuts.For` | Фильтр по названию и режим «нажмите сочетание»; настройка самих клавиш — в роадмапе | S | P3 |
| Подтверждение выхода из аккаунта | Codex app 26.409 «logout confirmation dialog» | **Have**: `AskSignOutCommand` → подтверждение (`MainWindow.axaml:1926–1953`) | — | — | — |
| Сброс позиции питомца, «Reset to original» | ChatGPT desktop 26.908 «Reset a pet to its default size» | **Have** (позиция и арт): `PetsSettings.axaml:66, 198`; **Unclear** — размер | — | — | — |

## 7. Окно и ОС

| Микро-взаимодействие | Где у них (источник) | У нас (статус + доказательство) | Что сделать | Effort | P |
|---|---|---|---|---|---|
| Размер, позиция и maximized окна восстанавливаются при запуске | ожидание платформы (из брифа); у Codex — состояние у каждого окна (26.324 per-window zoom, 26.406 unread across windows) | **Missing**: `Width="1100" Height="760" WindowStartupLocation="CenterScreen"` (`MainWindow.axaml:15–17`), нигде не сохраняется | Сохранять `Position` / `ClientSize` / `WindowState` в options при закрытии; при запуске проверять, что окно попадает на `Screens` | S | P1 |
| Ширины боковой панели, Preview и терминала переживают перезапуск | Codex app 26.318 (ресайз в процентах), 26.406 «right-panel reset» fix | **Partial**: сохраняется только сайдбар; `_previewWidth` / `_sidePaneWidth` / `_terminalWidth` живут в памяти (`MainWindow.axaml.cs:166–175`) | Писать в options в `KeepDraggedWidths` (как `SaveSidebarWidth`) | S | P2 |
| Меню Help: Keyboard Shortcuts, Report a problem, What's new, Docs | Codex app 26.410 «Help menu feedback entry»; Codex 2025-06-03 «link to this changelog» | **Missing**: в `MenuBar` (`MainWindow.Menus.cs:28–63`) только File / Edit / View / Window | Пункт Help с `ToggleShortcutsCommand`, диагностикой (`DiagnosticsBundle`), ссылками | S | P2 |
| Меню View с панелями и их шорткатами, Enter Full Screen (⌃⌘F / F11) | commands: Toggle full screen ⌘⌃F / F11 | **Partial**: View = Sessions, Terminal, Events, без жестов (`MainWindow.Menus.cs:43–48`) | Добавить Files / Plan / Tasks / Preview с `Gesture` и полноэкранный режим | S | P3 |
| Заголовок окна = имя чата и «●», когда нужен ответ | Codex 2025-05-22 «page titles to task names» | **Have**: `WindowTitle` (`MainViewModel.Attention.cs:20`) | — | — | — |
| Клик по уведомлению открывает нужный чат | iOS 1.2026.132 (открытие задачи из уведомления) | **Have**: `AttentionRequested(title, msg, open)`; Windows — мигание кнопки на панели задач (`Platform/Notifications.cs:61–97`) | — | — | — |
| Брендированный экран загрузки при старте | Codex app 26.318 «branded loading shimmer while the app starts» | **Partial**: строка статуса «Starting…» (`MainViewModel.cs:116`) | Логотип с одним ступенчатым `Pulse` до `Ready` | M | P3 |

## 8. Motion и анимация

| Микро-взаимодействие | Где у них (источник) | У нас (статус + доказательство) | Что сделать | Effort | P |
|---|---|---|---|---|---|
| Раскрытие и сворачивание (thinking, tool, группы, diff) с переходом и поворотом шеврона | iOS 1.2026.272 / 267 «Smoother… transitions»; Codex app 26.415 «Improved thread and tool rendering» | **Missing**: в Views нет `Transitions`, кроме switch и pet (`Styles.axaml:512–519`, `Styles.Pets.axaml:15–16`) | `TransformOperationsTransition` 120 мс на `RenderTransform` у `.chevron`; появление тела — `Opacity` 0 → 1 за 100 мс (разовое) | S | P3 |
| Тост (`SessionCard` brief) въезжает и тает | Codex app 26.313 (тосты) | **Missing**: `SessionCard` появляется и исчезает мгновенно | `Opacity` + `TranslateTransform` Y 8 → 0 за 150 мс | S | P3 |
| Кнопка «Jump to latest» плавно появляется и исчезает | шаблон «scroll-to-bottom» из брифа; Codex app 26.227 (прокрутка) | **Have** (кнопка: `MainWindow.axaml:788`, `MainWindow.ChatScroll.cs:22`), **Missing** (переход) | `Opacity` 0 ↔ 1 за 120 мс вместо `IsVisible` | S | P3 |
| Живой статус переливается (shimmer), а не просто мигает | CLI #48757 «Match TUI status shimmer timing to desktop headers» | **Partial**: опасити-пульс `Pulse.Low` на «Thinking…» и точках (`Pulse.cs:10–23`) | Градиентная маска, сдвигаемая тем же ступенчатым таймером `Pulse` (≤20 fps) | M | P3 |
| Счётчик +N −M анимируется при изменении | Codex app 26.602 «animated diff stat alignment» | **Missing**: статичный текст (`DiffView.cs:295–299`, `MainWindow.axaml:609–612`) | Отсчёт за 300 мс на изменение (разовый) | S | P3 |
| Переход цвета фона при наведении на строки и кнопки | общий desktop-паттерн; Codex 26.506 «hover fixes» | **Missing**: у `:pointerover` нет `BrushTransition` | `BrushTransition` 80 мс на `Background` у `menu-item`, `session`, `tool-row` | S | P3 |
| Спиннер инструмента ступенчатый и дешёвый | CLI #50112 «Centralize TUI loading glyphs and frame scheduling» | **Have**: `Pulse.Spin` (`Pulse.cs:43–45`) | — | — | — |

## 9. Обратная связь и состояния

| Микро-взаимодействие | Где у них (источник) | У нас (статус + доказательство) | Что сделать | Effort | P |
|---|---|---|---|---|---|
| Тост не гаснет под курсором, несколько тостов не перетирают друг друга | Codex app 26.313 «resume and thread error toasts with clearer details» | **Partial**: `ShowBrief` — фиксированный таймер на 4 с, без паузы при наведении; новая карточка заменяет старую (`MainViewModel.Session.cs:92–122`) | Ставить таймер на паузу на `PointerEntered`; короткая очередь из ≤2 brief | S | P2 |
| Undo для обратимых действий + ⌘Z «Undo last app action» | commands: Undo last app action ⌘Z / Redo ⌘⇧Z | **Partial**: Undo есть только у «Remove from sidebar» (`MainViewModel.Sessions.cs:629–643`); Edit → Undo работает только с текстом (`MainWindow.Menus.cs:68–76`) | Стек app-действий (pin / unpin, rename, удаление из очереди, закрытие вкладки); ⌘Z, когда фокус не в `TextBox` | M | P2 |
| Состояние «загружается разговор» при открытии чата | Codex app 26.506 «showing model loading while a thread resumes»; iOS 1.2026.230 | **Unclear**: явного состояния загрузки в транскрипте нет (`OpeningFile` используется только для дедупликации, `MainViewModel.OpenSessions.cs:51, 287`) | Скелетон из 3 строк или «Opening…», пока `OpeningFile != null` | S | P2 |
| Скелетоны вместо «Loading…» в списках | Codex 2025-05-22 «Polished how new tasks look while loading» | **Partial**: везде текст («Loading files…», «Loading plugins from omp…» и т. п.) | Серые плашки с единым `Pulse` | M | P3 |
| Пустые состояния с действием | Codex app 26.506 | **Have**: Terminal, Files, Plan, Tasks, Connectors, Plugins, Permissions (см. grep `No …`) | — | — | — |
| «Not now» вместо молчаливого отказа (rename / delete во время хода) | CLI #50764 (`/archive` во время хода) | **Have**: `ShowBrief("Not now", …)` (`MainViewModel.Sessions.cs:678, 739`) | — | — | — |
| Русские числа: настоящие формы множественного числа | CLI #47329 (единообразие формулировок) | **Partial**: `Loc.Plural` знает только one / other (`Loc.cs:37–38`); в ru.json обход через «инструментов: {0}» | Правила CLDR для ru (one / few / many) в `Loc.Plural` | S | P3 |

## 10. Клавиатура и фокус

| Микро-взаимодействие | Где у них (источник) | У нас (статус + доказательство) | Что сделать | Effort | P |
|---|---|---|---|---|---|
| Esc закрывает то, что сверху (меню → viewer → лист → диалог → настройки → поиск или панель → стоп) | CLI #47320, #50503 | **Have**: `MainWindow.OnKeyDown` (`MainWindow.axaml.cs:1018–1072`), `EscapeStaysLocal` | — | — | — |
| Видимые focus rings | Codex app 26.608 «keyboard navigation» | **Have**: `:focus-visible` (`Styles.axaml:79–91, 537`) | — | — | — |
| Цифры в карточке выбора срабатывают сразу | CLI #44744 | **Have**: `OnDigitForDialog` (`MainWindow.axaml.cs:1004–1016`) | — | — | — |
| Ctrl+Tab / Ctrl+⇧Tab по чатам | commands: Next / Previous chat | **Have**: `MainWindow.axaml.cs:67–73`, `CycleSession` | — | — | — |
| Копирование с клавиатуры: ⌘⇧C рабочая папка, ⌘⌥⇧C путь разговора, ⌘⌥L ссылка на чат | commands (Workspace) | **Partial**: только через меню (`CopySessionFileCommand`, `CopyProjectPathCommand`; `SessionTitle.axaml:28–38`) | `KeyBinding` + строки в листе шорткатов (⌘⇧C у нас свободен) | S | P3 |
| Ctrl+Insert копирует в `CodeView` (Windows / Linux) | CLI #50215 | **Partial**: `CodeView.OnKeyDown` — только Cmd/Ctrl+C и A (`CodeView.cs:390–393`); `TextBox` и `SelectableTextBlock` берут `HotkeyConfiguration` [INFERENCE] | Проверять `HotkeyConfiguration.Copy` вместо `Key.C` | S | P3 |

## 11. Буфер обмена и DnD

| Микро-взаимодействие | Где у них (источник) | У нас (статус + доказательство) | Что сделать | Effort | P |
|---|---|---|---|---|---|
| ✓ после копирования сообщения (а не только code block) | консистентность: у нас code block меняет иконку на `IconCheck` на 1,5 с (`MarkdownView.cs:816–817`), копирование ID сессии показывает brief | **Partial**: `CopyUserMessage` (`MainViewModel.Transcript.cs:28–31`), `CopyTextCommand` / `CopyCommand` ответа (`RowViewModels.cs:220`, hover-кнопка `MainWindow.axaml:536–539`) — без всякой обратной связи | Тот же приём, что у code block: иконка → check на 1,5 с у `row-action`; в меню — короткий brief «Copied» | S | P1 |
| Copy path копирует простой текст (обычный и относительный) | CLI #49564 | **Have**: `FilesViewModel.CopyPath` / `CopyRelativePath` (`FilesViewModel.cs:862–872`) | — | — | — |
| Copy ответа: текстом и как Markdown | CLI #47896 / #49089 | **Have**: `ReplyMenu` (`MainWindow.axaml:45–70`); rich HTML — в роадмапе | — | — | — |
| Вставка: файлы и картинки становятся вложениями, длинный текст — чипом | Codex app 26.417 «non-image file pasting» | **Have**: `PasteAsync` (`MainWindow.axaml.cs:940–963`) | — | — | — |
| Linux: primary selection, вставка средней кнопкой | CLI #49112 | **Missing**: в коде нет | Для X11 — `PRIMARY` при выделении и вставка по средней кнопке в composer | M | P3 |
| Файлы, брошенные в любое место окна, идут в composer с подсветкой | Codex app 26.205 | **Have**: `MainWindow.axaml.cs:77–81, 975–989` | (текстовая подсказка — см. Composer) | — | — |
| Перетаскивание для изменения порядка: очередь, вкладки, проекты | Codex 26.217 / 26.409 / 26.609 | **Missing** (см. Composer, Panes, Sidebar) | — | — | — |

---

## Top 20 quick wins (effort S, по ценности)

1. **Восстанавливать размер, позицию и maximized окна** — `MainWindow.axaml:15–17` → options (P1).
2. **Меню у пути к файлу в ответе** (Open / Open in editor / Reveal / Copy path / Add to message) — `MarkdownView` path link, `PathLinks` (P1).
3. **Контекстное меню карточки инструмента** (Copy command / output / Open / Reveal) — шаблон `ToolRowViewModel` (P1).
4. **Двойной клик по строке сайдбара → rename** — `DoubleTapped` → `RenameSessionItemCommand` (P1).
5. **✓ после копирования сообщения**, как у code block — `row-action` Copy и `ReplyMenu` (P1).
6. **Локализовать захардкоженные строки**: `"Copied"` (`ImageViewer.axaml.cs:52`), ошибка буфера и заголовки диалогов выбора (`MainWindow.axaml.cs:849/861/961/995`), «… N more lines» (`DiffView.cs:240`), «Continued with…» (`ConversationState.cs:359`) (P2).
7. **Меню картинки: Copy image / Save as… / Reveal** — `ImageMenu` (P2).
8. **После удаления открывать соседний чат**, а не новый пустой — `MainViewModel.Session.cs:579` (P2).
9. **Пауза тоста под курсором и очередь из 2 brief** — `ShowBrief` / `ShowCard` (P2).
10. **⌘W закрывает активную вкладку Preview / Terminal до окна** — `MainWindow.Menus.cs:55` (P2).
11. **Шорткаты Preview**: ⌘R, ⌘L, ⌘←/→, ⌘⇧C, ⌘T — `PreviewPanel` (P2).
12. **Клик по фону заголовка diff сворачивает его** — `DiffView.Header` (P2).
13. **Alt-клик по шеврону сворачивает или разворачивает все карточки хода** (P2).
14. **⌘/Ctrl+Enter отправляет многострочный ответ и «say why»** — `FeedbackBox`, editor-ответ (P2).
15. **Сохранять ширины Preview, side pane и терминала** — `KeepDraggedWidths` → options (P2).
16. **Режимы уведомлений Never / Background / Always + переключатели вопросов и разрешений** — `NotificationsEnabled` (P2).
17. **Меню Help** (Shortcuts, Report a problem, What's new) — `MainWindow.Menus.cs` (P2).
18. **Mark as unread / Mark all as read** (⌘⇧U, ⇧Esc) — `SessionRowMenu`, `OpenSession.Unread` (P2).
19. **«Copy error» у ошибки ответа и notice** — `ReplyError`, `NoticeRowViewModel` (P2).
20. **Подсказки клавиш в нотации платформы в tooltip** (⌥A, ⇧Tab, ⌃`) через `KeyboardShortcuts.Text` (P2).

Следующие S-кандидаты: Reveal / Open terminal в `SessionRowMenu`; «Open in Preview» в меню ссылки; меню вкладок (Close others, Copy URL); шорткат выбора модели; шорткаты Rename / Pin; колонка шорткатов в контекстных меню; ширина вкладок не прыгает при закрытии.
