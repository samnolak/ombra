# Micro-UX: Claude Code (Desktop + VS Code + CLI) против Ombra

**Источники.** `/tmp/cl/claude-full.md` (CHANGELOG Claude Code, 2.1.296 → 0.2.21): все 369 записей `[VSCode]` и записи `VSCode:`/`IDE:`; записи CLI про спиннер, копирование, ссылки, hover, уведомления, статус, скролл и DnD, которые переносятся в GUI (330 строк отфильтровано вручную). Документация: <https://code.claude.com/docs/en/desktop>, <https://code.claude.com/docs/en/vs-code>, <https://code.claude.com/docs/en/fullscreen>, <https://code.claude.com/docs/en/interactive-mode> (индекс: <https://code.claude.com/docs/llms.txt>). Ссылки ниже в формате «VSC 2.1.290» означают запись `[VSCode]` этой версии в CHANGELOG, «CLI 2.1.x» — запись CLI, «desktop.md §…» и «vs-code.md §…» — раздел страницы документации.

**Как проверяли наш код.** Пути даны относительно `avalonia/src/Ombra.App`. Каждый статус подтверждён grep или чтением кода. Если код поведение не доказывает (IME, гонки hover), стоит **Unclear** и указано, что проверить руками.

**Чего здесь нет.** Крупные функции из `docs/PARITY_ROADMAP.md` не повторяем: find в чате, палитра, история промптов, черновики на диск, архив и автоархив, фильтры по статусу, «открыта в другом месте», ⌘1–9 и ⌘[ ⌘], сворачиваемые и пользовательские группы, fork и чекпоинты, agent map со Stop, Jobs Stop, Focus view, время сообщений, закладки, Mermaid и LaTeX, огромные таблицы, видимый href ссылки, пофайловый revert, вкладки файлов, бейдж Dock, reduced motion и live regions, масштаб UI, поиск по настройкам, ошибка списка сессий вместо пустоты. Если микро-деталь является частью такой функции, она не попала сюда.

Статусы: **Have** — есть; **Partial** — есть частично; **Missing** — нет; **Unclear** — по коду не определить. Effort: S — до ~0,5 дня, M — 1–2 дня, L — больше.

---

## 1. Sidebar

| Микро-взаимодействие | У них (источник) | У нас | Что сделать | Effort | P |
|---|---|---|---|---|---|
| Действия строки появляются только при hover, клавиатурном фокусе и открытом меню | VSC 2.1.261 (Archive в строке); desktop.md §Work in parallel («hover… click the archive icon») | **Have**: `Styles.Workspace.axaml:104-111` (`:pointerover`, `:focus-visible`, `:flyout-open`); пока видны действия, мета скрыта и заголовок не уезжает под них (`:118-123`) | — | — | — |
| ПКМ по строке сессии: Mark as unread, Open in (редактор/файловый менеджер), Copy ID | VSC 2.1.257 (Mark Session as Unread); desktop.md §Continue in another surface («Open in» из меню строки); VSC 2.1.261 (Archive в ПКМ) | **Partial**: `Views/Sidebar/SessionRowMenu.axaml` — только Rename, Pin, Copy path, Delete…. Copy ID есть лишь в меню заголовка (`MainViewModel.Session.cs:430`), Reveal — лишь у проекта (`MainWindow.axaml:~195-203`) | Добавить в `SessionRowMenu`: Mark as unread/read, Reveal in Finder/Explorer, Open in terminal, Copy session ID | S | P2 |
| Точка статуса: working (пульсирует), needs input (рука), unread, error; tooltip со статусом | VSC 2.0.73 (бейджи blue/orange), VSC 2.1.261 (unread dot) | **Have**: `MainWindow.axaml:286-292`, `Styles.Workspace.axaml:134-162` (для unread есть кольцо ради контраста 3:1) | — | — | — |
| Относительное время в строке обновляется само и считается от последнего сообщения, а не от последнего открытия | VSC 2.1.275 («showing when a session was last reopened… instead of last message») | **Have**: `SessionItemViewModel.When/Relative`, минутный тик `EnsureAgeTimer` (`MainViewModel.Sessions.cs:~380`), источник `LastMessageAt` | — | — | — |
| Первый Esc в поиске очищает его, второй уводит фокус | fullscreen/CLI-поиск; Mac search field | **Have**: `MainWindow.Escape.cs:25-31` | — | — | — |
| Стрелки ↑/↓ по списку сессий, Enter открывает, F2 — rename, Delete — удаление | VSC 2.1.280 («arrow-key navigation in the session list stopping after archiving») | **Missing**: строки — `Button` внутри `ItemsControl` внутри `ListBox` (`MainWindow.axaml:264-316`), обработчика стрелок нет, только Tab | `KeyboardNavigation.TabNavigation="Once"` на список плюс directional-фокус по кнопкам сессий; Enter → Open, F2 → Rename, Delete → Delete… | S–M | P2 |
| Мультивыбор ⌘/Shift-клик и массовое действие | VSC 2.1.229 | **Missing**: нет ни выделения, ни bulk-команд | Нужен только для bulk Delete/Pin/Move; делать после архива | M | P3 |
| Индикатор загрузки списка при первом скане (не пустой сайдбар) | VSC 2.1.32 («spinner when loading past conversations list»); VSC 2.1.269 (список не прыгает, когда шапка появляется позже) | **Missing**: есть только `No sessions match` (`MainWindow.axaml:322`); до первого скана сайдбар пуст | 3–4 строки-скелета или «Loading sessions…» до первого `Rescan`; высота зарезервирована, чтобы список не прыгал | S | P2 |
| Ширину сайдбара тянут, двойной клик сбрасывает её, значение сохраняется | Desktop §Arrange («drag a pane edge») | **Have**: `MainWindow.axaml.cs:434-458`, `OmpRuntimeOptions.SidebarWidth` | — | — | — |
| ПКМ по заголовку проекта: New session, Reveal, Copy path, Remove; hover-кнопки «+» и «−» | Desktop §Open in | **Have**: `MainWindow.axaml:~180-238` (ContextFlyout и group-action) | — | — | — |
| Tooltip у строки: полный заголовок, папка, время | VSC 2.1.274 (имена не прыгают при поиске) | **Have**: `MainWindow.axaml:276`, `SessionItemViewModel.Tooltip` | — | — | — |
| Ctrl+Tab / Ctrl+Shift+Tab переключают сессию | desktop.md §Keyboard shortcuts | **Have**: `MainWindow.axaml.cs:67-73` | — | — | — |
| Удаление можно отменить (Undo-тост вместо безвозвратного удаления) | VSC 2.1.257 (Delete заменён на Archive, «hid a session with no way to restore it») | **Partial**: подтверждение «Delete…», затем brief-карточка «Session deleted» без Undo (`MainViewModel.Session.cs:579`) | Пока нет архива: 5 с Undo (файл переносить в корзину по таймеру) | M | P2 |

## 2. Transcript: строки и сообщения

| Микро-взаимодействие | У них (источник) | У нас | Что сделать | Effort | P |
|---|---|---|---|---|---|
| Copy у ответа при hover | VSC 2.1.277 (Copy response); vs-code.md §Copy a response | **Have**: `MainWindow.axaml:534-540`, `694-697`; показ по `:pointerover`/`:focus-within` (`Styles.Session.axaml:189-193`) | — | — | — |
| ✓ после копирования **на всех** кнопках Copy | VSC 2.0.64 (copy на code blocks), CLI 2.1.113 («copied N chars» toast) | **Partial**: ✓ на 1,5 с есть только у code block (`Controls/MarkdownView.cs:812-817`); Copy у ответа, у user-сообщения и у turn-end молчат (`RowViewModels.cs:215-220` → `MainWindow.axaml.cs:750-753`) | Один helper «замени иконку на IconCheck на 1,5 с» для всех `Button.row-action` с Copy | S | **P1** |
| Hover на своём сообщении: Rewind и Copy | vs-code.md §Rewind («Hover over any message to reveal the rewind button») | **Have**: `MainWindow.axaml:456-468`, меню `UserMessageMenu` (`:23-44`) | — | — | — |
| ПКМ по ответу: Copy, Copy as Markdown, Retry | VSC 2.1.277 `/copy`, CLI 2.1.59 (picker «code block vs full») | **Have**: `MainWindow.axaml:45-70` | Добавить «Copy last code block», если в ответе ≥1 блок (аналог picker `/copy`) | S | P3 |
| Hover по пути в тексте: видно, какой это файл | VSC 2.1.290 (hover по file pill показывает путь от корня проекта) | **Have, лучше**: peek-превью содержимого, миниатюра картинки, курсор Hand (`Controls/PathLinks.cs:113-133`, `MarkdownView.cs:592-617`) | — | — | — |
| **ПКМ по пути в чате**: Attach as context, Open in <editor>, Show in Finder, Copy path; для папки — Open in terminal | desktop.md §Open files in other apps; §Run commands («right-click a folder in the chat to choose Open in terminal») | **Missing**: `ContextFlyout` получают только http-ссылки (`MarkdownView.cs:749` → `ContextMenus.ForLink`); у `PathRun` и у кнопки пути в строке инструмента (`MainWindow.axaml:595-599`) меню нет, хотя у дерева Files оно полное (`Views/Panes/FilesPane.axaml:16-60`) | `ContextMenus.ForPath(PathTarget)`: Open in Files, Add to message, Open in default app/editor (`Services/FileOpeners.cs`), Reveal, Copy path, Copy relative path, для папки — Open in terminal. Подключить к `PathRun`, `tool-path`, `turn-file` и строкам DiffStack | S–M | **P1** |
| Ссылки на пути с пробелами, скобками, не-ASCII | VSC 2.1.284, VSC 2.1.296 (Windows `C:\…`, `file:///C:/…`) | **Partial**: `PathLinks.LooksLikePath` отбрасывает любой пробел (`PathLinks.cs:56`), поэтому `My Docs/a.ts` в inline-code не становится ссылкой; `%20` в markdown-ссылке декодируется (`MarkdownView.cs:715`) | Внутри inline-code и target markdown-ссылки пробелы допускать (файловую систему проверяет `ResolvePath`) | S | P3 |
| Обычный клик по внешней ссылке открывает встроенный браузер, ⌘/Ctrl-клик — системный; при первом клике спрашивают | desktop.md §Browse external sites | **Partial**: http(s) всегда уходит в системный браузер (`ContextMenus.OpenLink`), localhost в Preview из чата не открывается; есть только меню Open/Copy link | localhost и `file://*.html` открывать в Preview, ⌘-клик → система; в меню ссылки добавить «Open in preview» | S | P2 |
| Hover-стиль ссылки | CLI 2.1.141 (цвет ссылок под тему) | **Have**: `HyperlinkButton.md-link:pointerover` (`Styles.Session.axaml:845-848`) | — | — | — |
| «Thinking… 4s» вживую, затем «Thought for 4s» | VSC 2.1.79, VSC 2.1.225 | **Have**: `RowViewModels.cs:199-210`, toggle `MainWindow.axaml:508-521` | — | — | — |
| Одной командой развернуть или свернуть **все** thinking-блоки | vs-code.md §Extended thinking («press Ctrl+O to expand or collapse every thinking block») | **Missing**: toggle есть только у отдельного блока | Alt-клик по chevron применяет ко всем блокам; пункт во View | S | P3 |
| Строка активности: глагол, elapsed, «· Esc to stop»; высота зарезервирована, поэтому нет layout jitter | CLI 2.1.31 («reduced layout jitter when spinner appears»); vs-code.md (spinnerVerbs) | **Have**: `MainWindow.axaml:1042-1054` (`ActivitySlot Height=20`), `MainViewModel.cs:603-631` | — | — | — |
| Долгое мышление видно: цвет «теплеет» через ~10 с, после 45 с текст «still/deep in thought» | CLI 2.1.141, CLI 2.1.271, CLI 2.1.116 | **Missing**: строка всегда одного цвета | Класс `.long` после 10 с thinking (тон warning) и текст «still thinking» после 45 с | S | P3 |
| Сообщение, отправленное во время хода, стоит «в очереди» до момента, когда агент его возьмёт | VSC 2.1.275, CLI 2.1.281 | **Have**: `QueuedList` с меткой Queued/Steer (`MainWindow.axaml:~1257-1280`), ↑ правит последнее | — | — | — |
| Маркер прерванного хода | VSC 2.1.285 (note under restored message) | **Have**: «Interrupted» (`RowViewModels.cs:229`), пунктирный круг у инструмента (`MainWindow.axaml:582`) | — | — | — |
| Компакция — сворачиваемая карточка «Compacted» с summary внутри | VSC 2.1.69 | **Partial**: разделитель «Conversation compacted · X → Y tokens» (`RowViewModels.cs:814-821`), summary не раскрыть | Разделитель с chevron и текстом summary, если omp его отдаёт | S–M | P3 |
| Своё сообщение показано как набрано (нумерация, переносы) | CLI 2.1.274 («3. 2. 1.» не перенумеровывается), CLI 2.1.281 | **Have**: plain `SelectableTextBlock` (`MainWindow.axaml:485`) и fold длинных | — | — | — |
| Картинка в сообщении: клик открывает viewer, ПКМ даёт Copy image, Save as…, Reveal | CLI 2.0.73 (клик по `[Image #N]`), CLI 2.1.196 (⌘-клик → Reveal) | **Partial**: viewer есть (`MainWindow.axaml:472-484`), но `ImageMenu` содержит только «Open» (`:91-102`) | Добавить в `ImageMenu` Copy image, Save as…, Reveal in Finder | S | P2 |
| Кнопка «к последнему» показывает **счётчик новых** («↓ 3 new») | fullscreen.md §Auto-follow («shows a count such as 3 new messages») | **Partial**: кнопка только с иконкой (`MainWindow.axaml:788-791`), показ по `IsFollowing` (`MainWindow.ChatScroll.cs:22`) | Пока `!IsFollowing`, считать добавленные строки и показывать «↓ N new»; при пустом счётчике — иконка | S | P2 |
| Стрим не тянет вниз, если читатель прокрутил вверх; при отправке скролл вниз | VSC 2.1.275 (тянуло вниз; добавлен `scrollToBottomOnSend`) | **Have**: `ChatScrollController.cs:44-64,121`, `ScrollToLatestRequested` (`MainViewModel.cs:141`). Отключить прыжок при отправке нельзя (**Missing**) | Настройку не заводить, пока никто не попросит | — | P3 |
| Строка инструмента целиком — кнопка: chevron при hover, tooltip только при обрезанном тексте | CLI 2.1.286 (клик между словами не раскрывал) | **Have**: `MainWindow.axaml:572-574` (`TrimmedToolTip`), `Styles.Session.axaml:304-334` | — | — | — |
| Частичный ответ сохраняется при обрыве, ошибка и Retry идут под ним | CLI 2.1.179 | **Have**: `MainWindow.axaml:522-532` (Text, ErrorText и Retry в одной строке) | — | — | — |
| RTL (иврит, арабский) в сообщениях и composer | VSC 2.1.267, VSC 2.1.69, VSC ≈2.1.180 | **Missing**: `FlowDirection` нигде не определяется, кроме жёсткого LTR в `CodeView.cs:185,194` | Абзацу с первым сильным RTL-символом ставить `FlowDirection.RightToLeft` (MdText, user bubble) | S | P3 |
| Итог хода «Worked for 1m 05s» и список изменённых файлов с +/− | CLI 2.1.0 (spinnerVerbs, past tense) | **Have**: `MainWindow.axaml:679-720` | — | — | — |

## 3. Карточки инструментов, diff, запросы

| Микро-взаимодействие | У них (источник) | У нас | Что сделать | Effort | P |
|---|---|---|---|---|---|
| **Copy команды и Copy вывода** у bash-карточки | VSC 2.0.64 («copy-to-clipboard button on code blocks and bash tool inputs») | **Missing**: команда и вывод — `SelectableTextBlock` (`MainWindow.axaml:641-643`), но у строки инструмента нет ни кнопки Copy, ни ContextFlyout | Hover-Copy в `card-head` консоли; ContextFlyout строки: Copy command, Copy output, Open file, Expand/Collapse | S | **P1** |
| «… +N lines» раскрывает весь вывод | CLI ctrl+o | **Have**: `tool-more` → `ShowAllCommand` (`MainWindow.axaml:644,653`) | — | — | — |
| Чип причины сбоя («exit 127») | — (у них нет) | **Have, лучше**: `MainWindow.axaml:600-607` | — | — | — |
| `+12 −1` на строке изменения | desktop.md §Review changes | **Have**: `MainWindow.axaml:608-612` | — | — | — |
| ANSI-коды в выводе shell убраны или отрендерены | VSC ≈2.1.170 («stripped ANSI escape codes from displayed shell output») | **Unclear/Partial**: `AnsiRegex` вызывается только в `PreviewViewModel.cs:30,626` (поиск URL dev-сервера); `ConsoleOutput` строки инструмента не чистится. Чистит ли omp, не проверено | Прогнать `ls --color=always` или `npm test`. Если ESC видны — strip (минимум) в `ToolRowViewModel.ConsoleOutput` | S | P2 |
| Читаемое имя MCP-инструмента | CLI 2.1.29x (MCP-tool по server и readable name) | **Have**: `DisplayNameOf` «tool (server)» (`RowViewModels.cs:307-322`) | — | — | — |
| Diff в чате следует теме | VSC 2.1.267 | **Have**: `DiffView.cs:179,207,423` (пересборка на `ActualThemeVariant`) | — | — | — |
| Комментарий к строке diff: кнопка при hover, Enter добавляет | desktop.md §Review changes («click any line… Cmd+Enter submit all») | **Have** в Files/Review (`FilesPane.axaml:359-372`, `Styles.Files.axaml:230`); в DiffStack транскрипта **Missing** | В DiffStack чата та же hover-кнопка «comment» → `AddCodeComment` | S | P3 |
| Кнопки одобрения называют правило: «Don't ask again for commands starting with npm test»; scope session/project/always | CLI 2.1.0, VSC 2.1.3 | **Have**: `DialogViewModel.cs:132-147` | — | — | — |
| «Deny and say why» — отказ с комментарием | interactive-mode.md (Tab на Yes/No открывает поле комментария) | **Have**: `StartFeedback`, клавиша 3 (`DialogViewModel.cs:151-168`) | — | — | — |
| Цифры 1–9 выбирают ответ, но не когда фокус в тексте | desktop.md (`1`–`9`) | **Have**: `MainWindow.axaml.cs:34-36`, `DialogViewModel.TryPick` | — | — | — |
| Двойной клик не отвечает на следующий запрос, введённый текст не переезжает | VSC 2.1.261 | **Have**: guard `_answered` (`DialogViewModel.cs:17,122-123`); на каждый запрос новая VM | — | — | — |
| Новый запрос не крадёт клавиатурный фокус | VSC 2.1.290 | **Have (by design)**: карточка не фокусируется сама; Esc уходит карточке только при фокусе внутри неё (`MainWindow.axaml.cs:1042-1048`) | — | — | — |
| **Свернуть карточку запроса**, чтобы прочитать разговор за ней | VSC 2.1.261 («fold button on permission and question prompts») | **Missing**: `DialogHost` (`MainWindow.axaml:1055+`) не сворачивается, а высокая карточка с длинной командой закрывает ленту | Кнопка «–» сворачивает в полосу «omp ждёт: Bash · Развернуть», цифры 1–9 продолжают работать | S | P2 |
| Метка «Recommended» у опции вопроса | VSC 2.1.286 (option previews) | **Have**: `DialogViewModel.cs:206-208` | — | — | — |
| Обратный отсчёт у запроса с дедлайном | CLI 2.1.287 (запросы с countdown) | **Have**: `HasDeadline` (`DialogViewModel.cs:110-112`) | — | — | — |

## 4. Composer

| Микро-взаимодействие | У них (источник) | У нас | Что сделать | Effort | P |
|---|---|---|---|---|---|
| Длинная вставка помечена как paste (>800 символов или >2 переносов) | VSC 2.1.280 | **Have**: chip (`MainViewModel.Composer.cs:150`, `PastedTextViewModel`) | — | — | — |
| **Невидимые Unicode-символы** вырезаются при вставке и отправке, с уведомлением «Removed N invisible characters» | VSC 2.1.280; vs-code.md §Paste text | **Missing**: `PasteAsync` вставляет текст как есть (`MainWindow.axaml.cs:956-957`), `Send` тоже не фильтрует | Фильтр U+200B–200F, 202A–202E, 2060–2064, FEFF, U+E0000–E007F; уведомление в `ComposerMessage`. Это ещё и защита от prompt injection | S | **P1** |
| «Pasting…», пока картинка читается из буфера | CLI 2.1.132; CLI 2.1.128 (не зависает на ошибке) | **Missing**: async `PasteAsync` идёт без индикации (`MainWindow.axaml.cs:940-963`). Ошибка буфера показывается честно (`:959-961`, **Have**) | Если чтение длится >150 мс, показывать `ComposerMessage = "Pasting…"` | S | P3 |
| Drop файла: подсветка цели, drop в любом месте окна, фокус возвращается в поле | CLI/IDE 2.0.8; vs-code.md (Shift-drag) | **Have**: класс `dragover` у `ComposerBox`, `OnDrop` → `Composer.Focus()` (`MainWindow.axaml.cs:975-989`) | — | — | — |
| Вставка файлов из Finder прикрепляет их | CLI 2.1.0 | **Have**: `TryGetFilesAsync` (`MainWindow.axaml.cs:945-949`) | — | — | — |
| Chip вложения: ×, ПКМ Open/Remove, ⌘-клик → Reveal | CLI 2.1.196 | **Partial**: `AttachmentMenu` содержит Open и Remove (`MainWindow.axaml:71-90`); Reveal нет | Добавить «Reveal in Finder» в `AttachmentMenu` | S | P3 |
| Отправка одной картинки без текста | VSC 2.1.275 | **Have**: `HasContent` учитывает `Attachments` (`MainViewModel.Composer.cs:148`) | — | — | — |
| Slash/@-меню: ↑↓, Tab/Shift+Tab, Enter берёт подсвеченный пункт, выбранный пункт прокручивается в видимость, Esc закрывает меню, а не останавливает ход | VSC 2.1.285 (Enter запускал fuzzy-пункт; Esc останавливал ход) | **Have**: `MainWindow.axaml.cs:870-895`, `ShowSelectedSuggestion` (`:922-928`) | — | — | — |
| Мышь, оставленная над списком, не перехватывает выбор при вводе | VSC 2.1.94 | **Unclear**: `CommandList` — ListBox, hover-выделение не проверено | Проверить руками: курсор над меню, набор и стрелки. При дефекте игнорировать `PointerMoved` без реального движения | S | P3 |
| IME: Enter и Esc внутри композиции не отправляют и не останавливают | IDE 2.0.5 (IME Enter), VSC ≈2.1.178 (Esc у CJK IME отменял задачу) | **Unclear**: в `OnComposerKeyDown` (`MainWindow.axaml.cs:867-920`) нет проверки composition | Проверить на macOS (Kotoeri, Pinyin) и Windows (MS IME); при дефекте пропускать Enter/Esc, пока у `TextInputMethodClient` есть preedit | S | P2 |
| Плейсхолдер меняется по состоянию и подсказывает клавиши | — | **Have, лучше**: `ComposerHint` (`MainViewModel.cs:125-133`) | — | — | — |
| ⌘/Ctrl+Enter для отправки (настройка) | vs-code.md `useCtrlEnterToSend` | **Have**: `SendWithModifier` (`KeyboardShortcuts.cs:53-57`) | — | — | — |
| Кольцо контекста: popover с разбивкой и «Compact now», цвета 70/85 % | desktop.md §Check usage | **Have**: `UsageViewModel.cs:23-27,52` | — | — | — |
| Шорткаты открытия меню модели, режима и effort | desktop.md (⌘⇧I модель, ⌘⇧M режим, ⌘⇧E effort) | **Partial**: Shift+Tab циклит режим (`MainWindow.axaml.cs:897-903`); у меню модели и thinking шортката нет; ⌘⇧M занят Device mode (`:632`) | ⌘⇧I → `OpenModelMenu`, ⌘⇧E → ThinkingBox; добавить в `KeyboardShortcuts` | S | P2 |
| Esc-Esc на пустом поле открывает Rewind | VSC 2.1.83; interactive-mode.md | **Missing**: в цепочке Esc (`MainWindow.axaml.cs:1022-1061`) двойного Esc нет, Rewind есть только в меню | Второй Esc ≤600 мс при Ready и пустом поле → `RewindCommand` | S | P3 |
| Undo/Redo в поле, в том числе после paste | CLI 2.1.116 (undo), VSC 2.1.282 | **Partial**: Undo/Redo TextBox и меню Edit работают (`MainWindow.Menus.cs:35-36,75`); удалённый chip вставки или вложения не восстанавливается | Undo-тост «Attachment removed · Undo» на 4 с | S | P3 |
| Диктовка: живой уровень и честная ошибка при тишине | VSC 2.1.141 («No audio detected») | **Have**: waveform `DictationLevels` и «No speech recognized.» (`MainViewModel.Dictation.cs:83,186`) | — | — | — |

## 5. Панели (Files, Plan, Tasks, Terminal, Preview)

| Микро-взаимодействие | У них (источник) | У нас | Что сделать | Effort | P |
|---|---|---|---|---|---|
| Закрыть панель с фокусом: ⌘\ | desktop.md §Arrange | **Missing**: нет ни биндинга, ни строки в `KeyboardShortcuts.cs:89-98`; `ClosePane` существует (`MainViewModel.Panes.cs:~70`) | ⌘\ закрывает панель с фокусом (Files, Plan, Tasks, Preview, Terminal) | S | P2 |
| Шорткаты панелей | desktop.md (⌘⇧D diff, ⌘⇧B browser, Ctrl+\`) | **Have**: ⌘⇧F/P/T/B, Ctrl+\` (`MainWindow.axaml.cs:628-636`) | — | — | — |
| Ширина панелей переживает перезапуск | desktop.md §Arrange | **Partial**: ширины Preview, Side pane, Terminal и Events живут только в памяти (`MainWindow.axaml.cs:166-176`); сохраняется лишь `SidebarWidth` (`OmpRuntimeOptions.cs:62`) | Сохранять четыре ширины в options по `DragCompleted` | S | P2 |
| Терминал: «+ Shell» в шапке, закрытие вкладки с tooltip о последствиях | desktop.md §Run commands | **Have**: `MainWindow.axaml:828-852` | «Open in terminal» из ПКМ по папке в чате — см. §2 | — | — |
| Дерево Files: ПКМ Open, Add to message, Open in default app, Reveal, Copy path, Copy relative path | desktop.md §Open files in other apps | **Have**: `FilesPane.axaml:16-60` | — | — | — |
| Состояния «Loading files…» и «Opening…» | CLI 2.1.267 (/diff не мигает «0 files changed») | **Have**: `FilesPane.axaml:183,411` | Показывать только после ~200 мс, чтобы не мигало; проверить вручную | S | P3 |
| Меню ⋮ браузера: Clear browsing data, «Open links in built-in browser» | desktop.md §Preview your app («Keep cookies», «Clear browsing data» в ⋮) | **Missing**: в шапке `PreviewPanel.axaml` только Back/Forward/Reload/адрес/Open in browser/Device/Annotate/Close | ⋮ с «Clear browsing data» и переключателем «открывать ссылки из чата здесь» (см. §2) | S–M | P2 |

## 6. Settings

| Микро-взаимодействие | У них (источник) | У нас | Что сделать | Effort | P |
|---|---|---|---|---|---|
| Переключатель: «on» в цвете акцента темы, focus ring, плавный ход | VSC 2.1.271 (on = цвет кнопки темы) | **Have**: `Styles.axaml:500-539` (переход 120 мс, `GuiAccent`, `GuiFocusRing`) | — | — | — |
| Ошибка загрузки списка с «Try again» | VSC 2.1.275 (Retry в MCP dialog) | **Have**: `ConnectorsSettings.axaml:131`, `PluginsSettings.axaml:60`, `SshSettingsViewModel.cs:110` | — | — | — |
| Ошибка чтения settings-файла: «Open settings file» и «Copy error» | VSC 2.1.271, VSC 2.1.94 (баннер parse error) | **Partial**: текст «Couldn't read omp's settings: …» (`ViewModels/WorkspaceConfig.cs:22-33` и др.), кнопок нет | Под текстом ошибки кнопки Open file и Copy error | S | P3 |
| Все строки и тумблеры доступны с клавиатуры, фокус виден | VSC 2.1.274 | **Have**: `:focus-visible` для Button, ToggleButton, ComboBox, ListBoxItem, CheckBox и switch (`Styles.axaml:79-90,537`) | — | — | — |

## 7. Окно и ОС

| Микро-взаимодействие | У них (источник) | У нас | Что сделать | Effort | P |
|---|---|---|---|---|---|
| OS-уведомление, только если пользователь не смотрит на эту сессию; клик открывает её | desktop.md §Work in parallel | **Have**: `MainViewModel.Attention.cs:47-87` (в заголовке «· ChatTitle», клик → `ShowFromNotification`) | — | — | — |
| Заголовок окна = имя сессии, «●» при новостях | CLI 2.1.6 / 2.1.79 (title), CLI 2.1.47 (/rename → title) | **Have**: `WindowTitle` (`MainViewModel.Attention.cs:20`) | — | — | — |
| **Размер, позиция и maximized окна восстанавливаются** | стандарт desktop; Claude Desktop | **Missing**: в `OmpRuntimeOptions` (`Ombra.ClientCore/OmpRuntimeOptions.cs`) нет bounds; `SetWindowState` (`MainViewModel.cs:148`) ничего не сохраняет | Сохранять Position, Size и WindowState при закрытии; при старте проверять, что окно попадает на существующий монитор | S | **P1** |
| ⌘W | desktop.md: ⌘W «Close session» | **Partial**: ⌘W закрывает окно (`MainWindow.Menus.cs:55`) | Оставить нативное поведение (одно окно = приложение), но в shortcut sheet явно писать «Close window» | S | P3 |
| Фоновые процессы на Windows не мигают консолью | VSC 2.1.271 | **Have**: `CreateNoWindow` в `Services/ProjectFiles.cs:435`, `FileOpeners.cs`, `UpdateInstaller.cs`, `RuntimePack.cs` | — | — | — |
| Каретка мигает только в активном окне | — | **Have, лучше**: `MainWindow.axaml.cs:103-128` | — | — | — |

## 8. Motion и анимация

| Микро-взаимодействие | У них (источник) | У нас | Что сделать | Effort | P |
|---|---|---|---|---|---|
| Пульс и спиннер без лишних перерисовок | CLI 2.1.2x (изоляция 50 мс цикла), CLI 1.0.83 (shimmer) | **Have**: общий дешёвый `Controls/Pulse.cs` вместо style-анимаций (`Styles.Session.axaml:219`). Shimmer нет — сознательно | — | — | — |
| Поворот chevron при раскрытии | общий паттерн (Claude Desktop) | **Partial**: поворот мгновенный (`Styles.Session.axaml:319-334`, `Styles.axaml:649-653`, `Styles.Files.axaml:78-82`) | `TransformOperationsTransition` 120 мс на `Icon.chevron`, отключать при reduced motion | S | P3 |
| Brief-тост («Session ID copied») плавно появляется и исчезает | CLI 2.1.89 (invalidate), Claude toasts | **Partial**: карточка показывается и через 4 с убирается без перехода (`MainViewModel.Session.cs:113-122`); правило «одна за раз» есть (**Have**) | Opacity и translateY за 150 мс на `SessionCardHost` | S | P3 |
| Hover-тон отличается от «текущей» строки | CLI 2.1.281 | **Have**: `Button.session.current` = Fill2, hover — отдельный тон (`Styles.Workspace.axaml:95-97`) | — | — | — |

## 9. Обратная связь и состояния

| Микро-взаимодействие | У них (источник) | У нас | Что сделать | Effort | P |
|---|---|---|---|---|---|
| **Единая обратная связь после копирования** | CLI 2.1.113 («copied N chars», корректный счёт эмодзи), CLI 2.1.277 (notice после drag-select) | **Partial**: ✓ у code block; brief-карточка для ID и path (`MainViewModel.Session.cs:430,438`, `Sessions.cs:661,710`); Copy сообщения, ссылки (`ContextMenus.CopyText`) и path из Files проходят молча | Один сервис `Copy(text, what)`: ✓ на кнопке-источнике или brief «Copied path»/«Copied 1 234 characters» (`Loc.Plural` уже есть, `Localization/Loc.cs:37`) | S | **P1** |
| Ошибка копирования показывается, а не проглатывается | CLI 2.1.2xx (copy «says how… instead of always claiming success») | **Missing**: `CopyToClipboard` молча ничего не делает, если окно неактивно (`MainWindow.axaml.cs:752`); результат `SetTextAsync` отброшен (`_ =`) | `await` и `catch` → `ComposerMessage` «Couldn't copy: …» | S | P2 |
| Retry у упавшего ответа; уведомления «Retrying (1 of 3)…» | CLI 2.1.105, CLI 2.1.198 | **Have**: `MainWindow.axaml:523-531`, `NoticeRowViewModel` (`MainViewModel.cs:472`) | — | — | — |
| Одна карточка за раз, новая заменяет старую | CLI 2.1.89 | **Have**: `SessionCardViewModel.cs:29-32` | — | — | — |
| Пустые состояния: «No sessions match», «Start a dev server and its address shows up here» | VSC 2.1.287 («Remote server is not connected» вместо «No web sessions yet») | **Have**: `MainWindow.axaml:322`, `PreviewViewModel.cs:188` | — | — | — |

## 10. Клавиатура и фокус

| Микро-взаимодействие | У них (источник) | У нас | Что сделать | Effort | P |
|---|---|---|---|---|---|
| ⌘/ — лист шорткатов | desktop.md | **Have**: `Controls/ShortcutsSheet`, `KeyboardShortcuts.cs` | Дописать новые шорткаты (⌘\, ⌘⇧I/E, focus composer) | S | P3 |
| **Фокус в поле сообщения из любого места** | vs-code.md: `Cmd+Esc` «Focus Input» | **Partial**: Esc из других TextBox возвращает в composer (`MainWindow.Escape.cs:32-35`), но из дерева Files, списка сессий или Preview горячей клавиши нет | ⌘L (или ⌘Esc) → `Composer.Focus()` с кареткой в конце | S | P2 |
| Фокус после действий не теряется: выбор модели → composer; Esc → chip; rename → composer; drop → composer; новый чат → composer; карточка исчезла → composer | VSC 2.1.274 (Open in New Tab без фокуса), VSC 2.1.295 (фокус прыгал между view) | **Have, сильно**: `RestoreLostFocus` (`MainWindow.axaml.cs:755-767`), `:84-95`, `:828-843`, `:988`, `:519-524` | — | — | — |
| Видимые focus rings | VSC 2.1.269 | **Have**: `Styles.axaml:79-90` | — | — | — |
| Пикер модели: type-to-filter, Enter берёт первое совпадение, стрелки и ←/→ для провайдеров | CLI 2.1.287 (Enter открывает лучшее совпадение) | **Have**: `MainWindow.axaml.cs:84-96,773-816` | — | — | — |
| Приоритет Esc: меню → viewer → sheet → запрос → settings → локальное поле → Stop | VSC 2.1.285, VSC 2.1.286 | **Have**: `MainWindow.axaml.cs:1022-1061`, `MainWindow.Escape.cs` | — | — | — |
| Подсказка шортката при hover | desktop.md (keyboard hints) | **Have**: `sidebar-shortcut` при hover/focus (`Styles.Workspace.axaml:33`), шорткаты в tooltip Preview (`PreviewPanel.axaml:111,162`) | Проверить, что у кнопок Files, Plan, Tasks и Terminal в шапке шорткат тоже есть в tooltip | S | P3 |
| «Focus last message»: фокус в ленту с клавиатуры | VSC 2.1.268 | **Partial**: ListBox «Conversation» достижим Tab (`MainWindow.axaml:440`), отдельной команды нет | ⌘⇧↑ (или пункт меню) → фокус на последний ListBoxItem | S | P3 |

## 11. Clipboard и DnD

| Микро-взаимодействие | У них (источник) | У нас | Что сделать | Effort | P |
|---|---|---|---|---|---|
| Ответ копируется как plain или как Markdown | CLI 2.1.198 (/btw «c to copy» raw markdown) | **Have**: `RowViewModels.cs:215-220`, `ReplyMenu` | Проверить, что таблицы в plain-копии выровнены (CLI 2.1.113) | S | P3 |
| Code block копируется точно, без отступов списка и хвостовых пробелов | CLI 2.1.128, CLI 2.1.136 | **Have**: копируется сырой `_code` (`MarkdownView.cs:814-815`) | — | — | — |
| Copy всей беседы как текст или Markdown в буфер | VSC 2.1.280 («Export conversation… copy or save as plain text») | **Partial**: `/export` в HTML-файл и Share (`MainViewModel.Session.cs:77`); копирования в буфер нет | Пункт «Copy conversation as Markdown» в меню заголовка | S | P3 |
| Выделенный текст: своё меню Copy, иначе меню контейнера | — | **Have**: правило в `Controls/ContextMenus.cs` (класс summary) | — | — | — |
| Картинка: Copy image, Save, Reveal | CLI 2.0.73, CLI 2.1.196 | **Missing** — см. §2 (`ImageMenu`) | — | — | — |

---

## Top 20 quick wins (S, самые заметные)

1. **Меню по ПКМ для пути в чате** (`ContextMenus.ForPath`: Open in Files, Add to message, Open in editor, Reveal, Copy path и relative path, Open in terminal для папки) на `PathRun`, `tool-path`, `turn-file` и DiffStack. Пункты переиспользовать из `FilesPane.axaml:16-60`. P1. *(S–M)*
2. **✓ после Copy на всех кнопках Copy** (сообщение, ответ, turn-end), как у code block (`MarkdownView.cs:812-817`). P1.
3. **Единый сервис копирования с обратной связью** («Copied path», «Copied 1 234 characters» через `Loc.Plural`) и честной ошибкой вместо молчаливого `_ = SetTextAsync` и пропуска при `!IsActive`. P1/P2.
4. **Copy команды и Copy вывода** в bash-карточке (hover в `card-head`) и ContextFlyout у строки инструмента. P1.
5. **Восстановление размера, позиции и maximized окна** (`OmpRuntimeOptions`) с проверкой, что окно попадает на монитор. P1.
6. **Вырезать невидимые Unicode-символы** при вставке и отправке с уведомлением «Removed N invisible characters». P1, это ещё и защита от prompt injection.
7. **Свернуть карточку запроса** в однострочную полосу, чтобы прочитать ленту под ней. P2.
8. **«↓ N new» на кнопке Jump to latest.** P2.
9. **Стрелки ↑/↓, Enter, F2 и Delete в списке сессий.** P2. *(S–M)*
10. **Скелет или «Loading sessions…»** до первого скана с зарезервированной высотой. P2.
11. **ПКМ по строке сессии:** Mark as unread/read, Reveal, Open in terminal, Copy session ID. P2.
12. **ПКМ по картинке:** Copy image, Save as…, Reveal in Finder; Reveal и для chip вложения. P2.
13. **Ссылки:** localhost и HTML-файлы открываются в Preview, ⌘-клик — в системном браузере; в меню ссылки «Open in preview». P2.
14. **⌘\ закрывает панель с фокусом**; ⌘⇧I открывает меню модели, ⌘⇧E — thinking; всё добавить в shortcut sheet. P2.
15. **⌘L (⌘Esc) переводит фокус в поле сообщения** из любого места. P2.
16. **Сохранять ширины Preview, Side pane, Terminal и Events** между запусками, как уже сохраняется `SidebarWidth`. P2.
17. **Проверить и вычистить ANSI** в `ConsoleOutput` строк инструментов. P2.
18. **Проверить IME руками** (Enter и Esc во время композиции не отправляют и не останавливают ход); при дефекте поставить guard. P2.
19. **Плавный chevron (120 мс) и fade у brief-тоста**, с отключением при reduced motion. P3.
20. **Esc-Esc на пустом поле → Rewind**; «Pasting…» при медленном чтении буфера. P3.

### Где мы уже лучше Claude Code (не трогать)
- Peek-превью файла и картинки при hover по пути (`PathLinks`) против их «путь в tooltip».
- Плейсхолдер с подсказками клавиш по состоянию (`ComposerHint`).
- Чип причины сбоя «exit 127» на строке инструмента.
- Цепочка возврата фокуса (`RestoreLostFocus`, model menu → composer/chip).
- Каретка не мигает в неактивном окне; общий дешёвый `Pulse`.
- «Deny and say why» и правила с понятной формулировкой и scope в одной карточке.
