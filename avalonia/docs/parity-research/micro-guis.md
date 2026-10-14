# Микро-UX: что подсмотреть в других GUI (pi-gui, nornzach, piggy, OMPiUI, Orbit, dylansantwani, PiPilot)

Источники: клоны в `/tmp/guis/*`. Основной — **pi-gui** (`apps/desktop/src`, 902 коммита, HEAD 2026-10-01). Пути у них даны от корня репо; у нас — от `avalonia/src/Ombra.App/`.
Крупные фичи из `PARITY_ROADMAP.md` (⌘1–9, палитра, find, архив, fork, quote выделения, reduced motion, dock-бейдж, время сообщений по hover) здесь не повторяются; если у пункта есть микро-сторона, стоит ссылка «→ роадмап §…».

Статусы: **Have** — есть; **Partial** — есть частично; **Missing** — нет; **Unclear** — в коде не нашёл, наверняка утверждать нельзя.

## Главное

1. **У нас почти нет переходов.** Во всём приложении две `Transitions`: тумблер (`Views/Styles.axaml:512-519`, 120 мс) и `PetPerch` (`Views/Styles.Pets.axaml:15-16`). Наведение, раскрытие, шевроны, тосты, кнопка «к последнему» включаются мгновенно. У всех референсов есть короткие токены: pi-gui `--motion-fast 0.12s / base 0.15s / slow 0.24s`, ease-out `cubic-bezier(0,0,0.2,1)` (`apps/desktop/src/styles/tokens.css:73-78`); dylansantwani 120/200/320 мс, `cubic-bezier(0.16,1,0.3,1)` (`src/renderer/src/styles/global.css:76-80`); PiPilot 120/200/340 мс (`src/styles/globals.css:109-116`); nornzach 80/140/220/320 мс. В DESIGN_SYSTEM.md запрещены *бесконечные* style-анимации, потому что на macOS они дорого стоят (`DESIGN_SYSTEM.md:227-230`). На однократный переход 120–150 мс этот довод не распространяется: кадры рисуются только пока он идёт.
2. **Обратная связь при копировании непоследовательна.** У блока кода иконка на 1,5 с меняется на ✓ (`Controls/MarkdownView.cs:816-817`). Кнопки копирования сообщения и ответа (`Views/MainWindow.axaml:464, 536, 694`), «Copy path» и «Copy link» в меню ничего не показывают. У всех референсов ✓ держится 1,0–2,0 с.
3. **Части элементов не хватает меню по правому клику.** Меню есть у сообщения, ответа, блока кода, ссылки, картинки, вложения, строки сессии, заголовка проекта, строки файла и TextBox. Его нет у карточки инструмента, ссылки на путь в тексте, строки diff, вкладок терминала и preview, строк Plan и Tasks, строки в очереди.
4. **Сделано лучше, чем у них:** Undo при скрытии проекта, лестница Esc, tooltip только у обрезанной строки (`TrimmedToolTip`), «Worked for · N tools · files», Copy и Copy as Markdown, просмотрщик картинок с ←/→/Space/Copy, вставка длинного текста чипом.

---

## Sidebar

| Микро-взаимодействие | Где у них | У нас | Что сделать | Effort | P |
|---|---|---|---|---|---|
| При наведении время строки сменяется кнопками pin/archive: opacity и `scale(0.92→1)` за 150 мс ease-out; то же при `:focus-within` | pi-gui `styles/sidebar.css:740-851` | **Partial**: подмена есть (`Controls/SessionRowActions.cs`, класс `actions-shown`; `Styles.Workspace.axaml`), но мгновенная | `DoubleTransition Opacity 0:0:0.12` на `.session-action` и на возраст строки | S | P3 |
| Индикаторы строки: running — кольцо 11 px, 900 мс linear; failed — красная точка 8 px; unseen — точка цвета accent | pi-gui `sidebar.css:654-680` | **Have**: `Ellipse.session-dot.running/unread/error` (`Styles.Workspace.axaml:153-160`), пульс через `ctl:Pulse` | — | — | — |
| Меню строки по правому клику: Rename (подсказка ⇧⌘R), Pin, Archive (⇧⌘A), **Mark as read** (только у непрочитанных), Add scheduled task, **Copy session ID**. Шапка, ПКМ и ⌘K собираются из одного списка | pi-gui `features/threads/thread-actions.tsx:14-125` | **Partial**: Rename, Pin, Copy path, Delete (`Views/Sidebar/SessionRowMenu.axaml`). Mark as read и Copy session ID нет (`MarkRead` в коде не встречается) | Добавить «Mark as read» (виден при `unread`), «Copy session ID», «Show in Finder»; справа — колонка с горячей клавишей, как в `SessionTitle.axaml` | S | P2 |
| Одно определение действий для меню шапки и меню строки | pi-gui `thread-actions.tsx:14-18` («cannot drift apart») | **Partial**: в шапке (`Views/Session/SessionTitle.axaml:21-116`) и в строке (`SessionRowMenu.axaml`) разные наборы | Общий список `MenuAction` с условиями видимости, из которого строятся оба меню | M | P3 |
| Меню nornzach: Open / Open in new tab / Open in new window / Rename / Delete | nornzach `components/layout/Sidebar.tsx:1079-1138` | **Partial** (см. выше) | Multi-window → роадмап §1.3 | — | — |
| Переименование в строке: autofocus и select-all, Esc отменяет, кнопки Save и Cancel | pi-gui `sidebar.tsx:1697-1727` | **Have**: `RenameBox.Focus(); SelectAll()` (`Views/MainWindow.axaml.cs:565`), Esc и Enter (`:834-842`); проект — `Views/Sidebar/ProjectNameBox.cs:26` | — | — | — |
| Меню проекта: Open in Finder, New/Remove worktree, Edit name, Remove (`confirm`: «This will not delete any files») | pi-gui `sidebar.tsx:838-895`, `hooks/use-workspace-menu.tsx:143-157` | **Have**, и даже лучше: New session here, Rename, Pin, Reveal, Copy path, Remove + **Undo** (`MainWindow.axaml:175-220`; `MainViewModel.Sessions.cs:629-644`) | Добавить «New worktree here» (у нас он есть в `BranchChip`) | S | P3 |
| Относительное время now/5m/3h/2d/1w/4mo/1y | pi-gui `lib/string-utils.ts:9-33` | **Have** + пересчёт раз в минуту (`MainViewModel.Sessions.cs:378-395`) | — | — | — |
| Пока зажат ⌘, вместо времени показываются бейджи 1–9. Они остаются после нажатия цифры, пока ⌘ держат; гаснут при движении мыши без ⌘ и при blur | pi-gui `ui/modifier-hints.ts:18-84`, `sidebar.tsx:1634-1641`; коммиты 6340eb0, f5ce9a4, 721c714 | **Missing** | Делать вместе с ⌘1–9 (→ роадмап §1.3). Класс `shortcut-hints` на окне по KeyDown/KeyUp ⌘ и по Deactivated | S (после ⌘1–9) | P2 |
| Ручка ширины: 4 px; при hover, focus и drag — accent-линия inset 2 px; на время drag весь shell получает `col-resize` и `user-select:none`; двойной клик сбрасывает; ←/→ шаг 20 px; Home/End — min/max; `role=separator` | pi-gui `ui/pane-resize-handle.tsx:60-112`, `sidebar.css:979-1001` | **Partial**: курсор `SizeWestEast`, двойной клик = сброс, ширина сохраняется (`MainWindow.axaml:328-331`; `MainWindow.axaml.cs:439-473`). С клавиатуры не управляется | Focusable grip + ←/→ ±20, Home/End | S | P3 |
| Tooltip у кнопки строки: текст и `<kbd>` с клавишами (⇧⌘A), 120 мс fade и translateY(-2px) | pi-gui `sidebar.tsx:1658-1675`, `styles/topbar.css:82-125` | **Partial**: клавиши пишутся в скобках («Settings (⌘,)», `MainViewModel.Sessions.cs:43-47`) | Шаблон ToolTip с keycap-чипом (`Border.tag`) | S | P3 |
| Ctrl+Tab: оверлей-переключатель появляется через 150 мс удержания, порядок MRU, сохраняется в localStorage | pi-gui `hooks/use-thread-switcher.ts:5-6, 73-74`, `thread-switcher-order.ts:3-5` | **Partial**: Ctrl+Tab перебирает сессии в порядке сайдбара, без оверлея (`MainViewModel.Sessions.cs:713-725`) | MRU-порядок; оверлей, если Ctrl держат дольше 150 мс | M | P3 |
| Поиск в сайдбаре: первый Esc очищает, второй уходит | pi-gui `app/secondary-surface.tsx:111-115` | **Have** (`Views/MainWindow.Escape.cs:25-31`) | — | — | — |
| Pinned-строки перетаскиваются: курсор grab/grabbing, порог 5 px, источник с opacity 0.3, у перетаскиваемой строки shadow; после drop порядок применяется оптимистично, без возврата на старое место | pi-gui `sidebar.tsx:237, 1480-1487`, `sidebar.css:604-614`; коммиты f1b76b8, 3e1827e | **Missing**: в сайдбаре нет DragDrop (grep `DragDrop` находит только composer) | Перетаскивание pinned и проектов; порог 4 px, как у `PetStage.DragThreshold` | M | P3 |
| Если перетащить папку на сайдбар, она добавляется как проект; overlay 150 мс ease-out | OMPiUI `FolderProjectDropOverlay.tsx:18` | **Missing**: drop с окна уходит только в composer (`MainWindow.axaml.cs:77-81, 975-988`) | Если drop над `Sidebar` и это папка, вызывать `AddProjectAsync(path)` с подсветкой группы | S | P2 |
| Узкое окно: сайдбар превращается в drawer поверх контента, с тенью | pi-gui `sidebar.css:959-976` | **Have** (`Sidebar.Classes "drawer"`, `SidebarScrim`) | — | — | — |

## Транскрипт: строки и сообщения

| Микро-взаимодействие | Где у них | У нас | Что сделать | Effort | P |
|---|---|---|---|---|---|
| Кнопки сообщения по hover: opacity и `translateY(-2px→0)` за 120 мс; то же при `:focus-within`. Кнопки стоят в потоке, поэтому не перекрывают строку ниже | pi-gui `styles/timeline.css:163-183`; nornzach `MessageBubble.tsx:525` (150 мс); piggy `styles.css:363` (80 мс) | **Partial**: показ при pointerover и focus-within есть (`Styles.Session.axaml:189-194`), но мгновенный | `DoubleTransition Opacity 0:0:0.12` на `Button.row-action` | S | P3 |
| **✓ после копирования сообщения**: 1400 мс, плюс sr-only «Copied»; при ошибке видно «Copy failed» | PiPilot `components/chat/MessageContextMenu.tsx:32-42, 65`; nornzach `MessageBubble.tsx:363` (1400 мс); piggy `MessageView.tsx:433` (1200 мс) | **Missing**: `row-action` Copy (`MainWindow.axaml:464, 536, 694`) и `CopyCommand` (`ViewModels/RowViewModels.cs:216-220`) никак не отзываются. Внутри приложения это расходится с блоком кода (1,5 с) | Общий attached-behavior `CopyFeedback`: иконка становится `IconCheck` на 1,5 с, как в `MarkdownView.cs:816` | S | **P1** |
| Меню сообщения зависит от того, что под курсором: ссылка — Open/Copy link; код — Copy code; затем Copy selection или Copy message, Quote, Ask side question; отдельной группой — Edit, Fork, Export | PiPilot `MessageContextMenu.tsx:89-122` | **Partial**: у ответа Copy / Copy as Markdown / Retry, у своего сообщения Copy / Rewind (`MainWindow.axaml:23-70`); ссылки и код — отдельные меню (`Controls/ContextMenus.cs:31-37`) | Если внутри сообщения есть выделение, первым пунктом — «Copy selection»; «Quote» → роадмап §1.1; «Export message as Markdown» | M | P2 |
| Отключённое действие объясняет причину: «Fork is available when the run finishes». Строка действий остаётся и во время стрима, чтобы не сдвигать раскладку | pi-gui `features/conversation/timeline-item.tsx:215-243` | **Unclear**: у `RewindToCommand` во время хода нет tooltip с причиной | Кнопка Disabled с tooltip, объясняющим, почему она недоступна | S | P3 |
| Новая строка появляется с `timeline-enter`: fade и translateY(4px) за 240 мс; у виртуализированных строк этого нет | pi-gui `timeline.css:97-110`; dylansantwani `.message` fade-up 320 мс `cubic-bezier(0.16,1,0.3,1)` (`global.css:456`) | **Missing** | Только для строк, пришедших вживую (не для истории): Opacity 0→1 за 150 мс. Отключать при reduced motion | M | P3 |
| Skeleton при открытии треда: 4 строки разной ширины, shimmer 1,4 с ease-in-out, sr-only «Loading transcript…» | pi-gui `conversation-timeline.tsx:215-235`, `timeline.css:618-630` | **Unclear/Missing**: в `Views/*` нет skeleton и «Loading» для транскрипта (grep находит только `ProvidersLoading`) | Серые полосы на время гидрации сессии, показывать с задержкой 150 мс (см. «Feedback») | M | P2 |
| Ошибка гидрации: «Couldn't load this thread / The selected conversation couldn't be restored», кнопка Retry → «Retrying…» | pi-gui `conversation-timeline.tsx:238-262` | **Unclear**: ошибки приходят notice-строками | Пустому транскрипту с ошибкой — карточку с Retry | S | P2 |
| Пустой тред: «Start the conversation / Send a prompt below to begin this session.» | pi-gui `conversation-timeline.tsx:264-274` | **Have**, и богаче: greeting, проект, стартовые промпты (`MainWindow.axaml:738-770`) | — | — | — |
| «New activity below» — подпись появляется, только когда внизу прибыло новое, sticky bottom 12 px. nornzach: кнопка уезжает вниз с `translate-y-12 opacity-0` | pi-gui `conversation-timeline.tsx:197-206`, `timeline.css:80-83`, `hooks/use-timeline-viewport.ts:77, 293`; nornzach `ChatStream.tsx:689-691` | **Partial**: круглая ↓ 32 px, видна, когда не следуем за низом (`MainWindow.ChatScroll.cs:22`; `MainWindow.axaml:788-791`). Новое ли что-то внизу — не показывает; появляется мгновенно | Точка или подпись «New» на кнопке, если `Rows` выросли с тех пор, как следование отключилось; Opacity-переход 150 мс | S | P2 |
| Тред открывается сразу внизу, без smooth-скролла; для каждого треда помнится позиция чтения | pi-gui коммиты 1e15529, 4284708; `timeline.css:44-51` | **Have** (`OpenSession.ScrollAnchor`, `ViewModels/OpenSession.cs:42-43`; `ChatScrollController`) | — | — | — |
| Порог «у низа»: nornzach — 24 px; OMPiUI показывает индикатор при расстоянии > max(400 px, высота окна) | nornzach `lib/chat-stream-utils.ts:45`; OMPiUI `ChatArea.tsx:508` | **Have** (своя логика в `Views/ChatScrollController.cs`) | Кнопку показывать только дальше чем через ~1 экран, а не на любом отрыве | S | P3 |
| «Worked for 1m 12s» разделителем; живое «Working for 12s», тик раз в 1 с | pi-gui `timeline-item.tsx:482-503`, `hooks/use-running-label.ts:12-37` | **Have**, и богаче: «Worked for · N tools · N files» (`RowViewModels.cs:659-703`), «Thinking… 4s» (`:199-203`) | — | — | — |
| Ссылка на файл в тексте: underline при hover и focus-visible, открывается на нужной строке | pi-gui `timeline.css:833-849`, `features/conversation/workspace-file-line.ts` | **Have**: `PathLinks` с tooltip «…at this line» (`Controls/PathLinks.cs:112-116`) | — | — | — |
| **ПКМ по ссылке на путь** | PiPilot `TerminalLinks.tsx:40-42` (Open external / Open internal / Copy) | **Missing**: в `PathLinks.cs` и `FileLink.cs` нет `ContextFlyout` | Взять пункты `FileMenu` из `Views/Panes/FilesPane.axaml:17-60`: Open, Add to message, Open in default app, Reveal, Copy path, Copy relative path | S | **P1** |
| Меню ссылки: Open / **Open internally** / Copy | PiPilot `TerminalLinks.tsx:40-42` | **Partial**: Open link и Copy link (`ContextMenus.cs:35-37`) | Пункт «Open in Browser preview» (preview pane у нас уже есть) | S | P2 |
| Блок кода: copy по hover, ✓ на 1,0–1,5 с | dylansantwani `lib/markdown.tsx` (1500 мс); nornzach `CodeBlock.tsx:141` (1400); piggy `CodeBlock.tsx:62` (1000) | **Have** (`MarkdownView.cs:801-817`; `Styles.Session.axaml:830`) | В ПКМ блока кода добавить «Insert into message» | S | P3 |
| Подсветка строки таблицы при hover | OMPiUI `index.css:739` (`tbody tr:hover > td`) | **Missing** (`Styles.Session.axaml:870+`: только рамка) | Фон `GuiFill1` у строки при pointerover | S | P3 |
| Миниатюра: открывает viewer; Esc и клик по фону закрывают; фокус возвращается на миниатюру; курсоры zoom-in/zoom-out, blur 6 px | pi-gui `image-attachment-thumb.tsx:28-36, 53-96`, `styles/main.css:1303-1323` | **Partial**: Esc, ←/→, Space, клик по фону, Copy (`Controls/ImageViewer.axaml.cs:20-52`), фокус на Close (`:59`). Возвращается ли фокус на миниатюру — Unclear (`RestoreLostFocus` отправляет его в composer, `MainWindow.axaml.cs:765-767`) | Запоминать инициатора и возвращать фокус ему | S | P3 |
| Выделение → плашка «Add to Chat ⌘L» с keycap-подсказкой; если выделение заняло несколько абзацев, оно ограничивается одним сообщением | pi-gui `annotations/annotation-selection.tsx:38-46, 130, 288-291`; коммит bee3918 | **Missing** → роадмап §1.1 «Выделение → Quote/Ask». Микро-сторона: хоткей ⌘L и keycap на плашке | — | — | — |
| Shimmer у «Thinking»: nornzach 2,2 с linear, OMPiUI и PiPilot 2,4 с | nornzach `styles/components.css:85`; OMPiUI `index.css:1056` | **Have** (своё): `Pulse` — opacity 1,1 с в каждую сторону (`Controls/Pulse.cs:10-41`), осознанно, ради экономии CPU | Не менять | — | — |
| Мигающая каретка в конце стримящегося ответа: 1,1 с ease-in-out | nornzach `styles/components.css:28`; dylansantwani `@keyframes caret-blink` (`global.css:1333`) | **Missing** | Статичный блок `▍` в конце текста или `ctl:Pulse` (без style-анимации). Убирать на `message_end` | S | P3 |

## Карточки инструментов и diff

| Микро-взаимодействие | Где у них | У нас | Что сделать | Effort | P |
|---|---|---|---|---|---|
| **Copy в раскрытой карточке**: у write-инструмента копируется diff, у остальных — input и output через `\n\n` | pi-gui `timeline-item.tsx:287-292, 370-391, 465-474` | **Missing**: в шаблоне `ToolRowViewModel` (`MainWindow.axaml:562-676`) копирования нет | Иконка Copy в `card-head` (справа, видна по hover), с ✓-фидбеком | S | **P1** |
| **ПКМ по карточке инструмента** | ни у кого нет; ближайший аналог — PiPilot Copy в меню сообщения | **Missing** | Copy command / Copy output / Copy path / Open in Files / Expand-Collapse all in turn | S | **P1** |
| Иконка «View in changes» рядом с write-инструментом | pi-gui `timeline-item.tsx:327-340` | **Have**: `diff-path-link` и `tool-path` открывают Files (`Controls/DiffView.cs:276-279`; `Styles.Files.axaml:287`) | — | — | — |
| Глиф по типу инструмента (diff/terminal/file/spark); статусная точка пульсирует 1,4 с, пока инструмент работает | pi-gui `timeline-item.tsx:297-325, 406-417`, `timeline.css:465-470` | **Have**: метки running/done/failed/interrupted (`MainWindow.axaml:579-582`) и глагол по состоянию | — | — | — |
| Шеврон поворачивается за 150 мс ease-out | pi-gui `timeline.css:393`, `sidebar.css:913` | **Partial**: поворот на 90° мгновенный (`Styles.Session.axaml:319-334`, `Styles.Panes.axaml:96-102`, `Styles.Files.axaml:79-84`) | `TransformOperationsTransition RenderTransform 0:0:0.15` в одном стиле `ctl|Icon.chevron` | S | P3 |
| Шеврон виден только при hover | Codex, pi-gui | **Have** (`Styles.Session.axaml:319-327`) | — | — | — |
| Раскрытие тела: OMPiUI — grid-rows 0fr→1fr за 300 мс ease-out; dylansantwani — fade-up тела 200 мс | OMPiUI `AttachmentItem.tsx:122`; dylansantwani `global.css:698` | **Missing** | Только opacity тела, 120 мс (анимировать высоту не нужно: дорого и сбивает якорь скролла) | S | P3 |
| Статистика diff +N −M в заголовке | pi-gui `timeline-item.tsx:316-321` | **Have** | — | — | — |
| Ошибка инструмента показывается прямо в строке | pi-gui `timeline-item.tsx:284, 315` | **Have** («why it failed ("exit 127")», `MainWindow.axaml:563-565`) | — | — | — |
| Tooltip только у обрезанной строки | — | **Have** (`ctl:TrimmedToolTip`, `MainWindow.axaml:574`) | — | — | — |
| В карточке изменений хода показано 5 файлов; при «Show more» фокус уходит на первый открывшийся файл, потому что кнопка исчезает | pi-gui `turn-changes-card.tsx:5, 80-85` | **Partial**: список с переключателем есть (`Styles.Session.axaml:561-596`); куда уходит фокус — Unclear | После раскрытия переводить фокус на первый новый `Button.turn-file` | S | P3 |
| ПКМ по строке diff | нет ни у кого | **Missing** (`DiffView.cs`) | Copy line / Copy hunk / Open at line / Comment on line (последнее уже есть в Files, `FileViewerViewModel.Review.cs:44`) | M | P3 |

## Composer

| Микро-взаимодействие | Где у них | У нас | Что сделать | Effort | P |
|---|---|---|---|---|---|
| Drop-зона: счётчик глубины dragenter/leave; во время drag — focus-ring, accent-tint фон и подпись «Drop images or files to attach» | pi-gui `composer-surface.tsx:131-187`, `main.css:817-826` | **Partial**: только рамка 2 px `GuiFocus` (`Styles.Composer.axaml:19-22`; `MainWindow.axaml.cs:975-980`) | Добавить tint `GuiFill1` и подпись. Если тащат не файлы, `DragEffects=None` (уже так) | S | P2 |
| × у вложения появляется по hover за 120 мс | pi-gui `main.css:1291-1295` | **Partial**: у чипа вложения есть ПКМ Open/Remove (`MainWindow.axaml:72-90`); видна ли кнопка × — Unclear | — | — | — |
| Enter во время хода ставит follow-up, ⌘/Ctrl+Enter — steer; подпись подстраивается под ОС | pi-gui `hooks/use-session-composer.tsx:353-361`; коммит e45cea9 | **Have**: Enter — очередь, ⌥Enter — steer (`ViewModels/KeyboardShortcuts.cs:50, 76-77`) | — | — | — |
| Очередь: у каждого пункта Edit / Remove / **Steer now** | pi-gui `composer-surface.tsx:210-217` | **Partial**: × Remove, ↑ — редактировать последний (`MainWindow.axaml:1257-1281`; `KeyboardShortcuts.cs:78`) | Карандаш у каждого пункта; «Send now» → роадмап §1.2 | S | P2 |
| Колесо мыши над slash- и @-меню не прокручивает транскрипт | pi-gui `composer-surface.tsx:273, 290, 355` | **Unclear** | Проверить и при необходимости обработать `PointerWheelChanged` в попапе | S | P3 |
| IME: Enter и Tab во время композиции не отправляют | OMPiUI `InputBox.tsx:907`; nornzach `Modal.tsx:100-102` | → роадмап 2.1 #23 (Risk) | — | — | — |
| Фокус в composer после выбора сессии | pi-gui коммит 90b8a34 | **Have** (`FocusComposerRequested`, `MainWindow.axaml.cs:519-524`) | — | — | — |
| Кольцо контекста рядом с моделью: карточка по hover или focus, Esc закрывает, обратный отсчёт тикает раз в 1 с только пока карточка открыта; заполнение анимируется 150 мс | pi-gui `conversation/context-meter.tsx:12-50, 159-173`, `context-meter.css:23, 49` | **Have** (`Views/Session/UsageIndicator.axaml`); анимации заполнения нет | — | — | P3 |
| Нажатие кнопки: Stop — `active:scale-90`; Button — `active:translate-y-px` | PiPilot `components/chat/Composer.tsx`; nornzach `common/Button.tsx:56` | **Missing**: у `:pressed` меняется только фон | `Button.send:pressed` → `RenderTransform scale(0.94)` | S | P3 |
| Фокус-кольцо поля: 3 px glow | nornzach `common/Input.tsx:23`; pi-gui `--focus-ring 0 0 0 3px` (`tokens.css:101-104`) | **Have**: `GuiFocusRing` (`Styles.axaml:79-91`) | — | — | — |

## Панели (Files, Plan, Tasks, Terminal, Preview)

| Микро-взаимодействие | Где у них | У нас | Что сделать | Effort | P |
|---|---|---|---|---|---|
| Вкладки боковой панели: roving tabindex ←/→/Home/End; tooltip «Files (⌘⇧F)»; бейджи цифр, пока зажат модификатор; после закрытия фокус уходит на соседнюю вкладку | pi-gui `features/workbench/workbench.tsx:109-195` | **Partial**: tooltip с клавишей (`MainViewModel.Panes.cs:84-88`), ViewsMenu с галочками; стрелками не переключается | ←/→ по иконкам панелей в шапке | S | P3 |
| Пустые состояния: «Loading files...», «No indexed files», «No matching files»; Review: «No changes», «Loading diff…», «Select a file from the explorer.», «Binary or directory preview is not available.» | pi-gui `file-explorer.tsx:55-63`, `diff-panel.tsx:535-616`, `file-editor-pane.tsx:241-255` | **Have**: «Loading files…» и `TreeMessage` (`Views/Panes/FilesPane.axaml:183-184`); Plan — «No plan yet» с объяснением (`PlanPane.axaml:54-58`) | — | — | — |
| Фильтр файлов: первый Esc очищает | pi-gui | **Have** (`MainWindow.Escape.cs:10-13`) | — | — | — |
| «Copy file» (содержимое) в шапке viewer | pi-gui `file-editor-pane.tsx:163-173` | **Missing**: в меню только Copy path и relative path (`FilesPane.axaml:49-60, 263-273`) | «Copy contents» в меню viewer | S | P3 |
| Состояние панели (показ дерева) переживает перезапуск | pi-gui `diff-panel.tsx:761-773` (`TREE_VISIBLE_KEY`) | **Partial**: ширины side pane, preview и terminal хранятся только в полях окна (`MainWindow.axaml.cs:166-175`); сохраняется лишь `SidebarWidth` (`ClientCore/OmpRuntimeOptions.cs:61-62`) | Сохранять `_sidePaneWidth/_previewWidth/_terminalWidth` рядом с `SidebarWidth` | S | P2 |
| ПКМ в терминале: Copy selection / Copy all / Paste; у ссылки в терминале — Open external / Open internal / Copy | PiPilot `inspector/RealTerminalPanel.tsx:544-547`, `TerminalLinks.tsx:40-42` | **Missing** (grep `ContextFlyout` по терминалу ничего не находит) | Меню на поверхности терминала + ссылки | S | P2 |
| ПКМ по вкладке: Close left/right/all, split | nornzach `components/layout/TabBar.tsx:449-528` | **Missing** (вкладки терминала и preview) | Close / Close others / Close to the right; Copy URL (preview) | S | P3 |
| Терминал: New и Restart с tooltip, ошибка показывается inline | pi-gui `terminal-panel.tsx:327-388` | **Have** (вкладки терминала) | — | — | — |

## Settings

| Микро-взаимодействие | Где у них | У нас | Что сделать | Effort | P |
|---|---|---|---|---|---|
| Switch: track — фон за 120 мс, knob — transform за 120 мс | pi-gui `main.css:2080-2095` | **Have**: 120 мс, `CubicEaseOut` (`Styles.axaml:512-519`) | — | — | — |
| Строка, найденная поиском по настройкам, вспыхивает: 1,8 с ease-out | OMPiUI `index.css:147` | → роадмап §1.8 (поиск по настройкам) | Когда поиск появится, подсвечивать найденное 1,8 с | — | — |
| Тост с итогом проверки соединения («connected, N tools»), debounce 350 и 500 мс | Orbit `app/mcp_ui.rs:34-37, 199-218` | **Have**: `ConnectorsSettingsFeedback.axaml` (✓ notice) | — | — | — |
| «Show more» в длинном списке (8 провайдеров, 6 ресурсов) | pi-gui `settings-providers-section.tsx:18`, `extensions/resource-list.tsx:18` | **Unclear** | — | — | P3 |

## Окно и ОС

| Микро-взаимодействие | Где у них | У нас | Что сделать | Effort | P |
|---|---|---|---|---|---|
| **Позиция и размер окна восстанавливаются**: normal bounds, если окно было развёрнуто, флаг maximized, прижатие к видимым мониторам (отключённый монитор → основной), debounce записи 250 мс | PiPilot `src/main/windows/window-state.ts:8-23, 52-53, 138-160`; nornzach `src/main/window.ts:94-141, 438-442` (`restoreWithinDisplays`) | **Missing**: в `OmpRuntimeOptions` нет полей окна; grep `Window(Width\|Height\|X\|Y\|Bounds)` ничего не находит | `WindowPlacement {X,Y,W,H,Maximized}` в settings; запись по `PositionChanged` и `SizeChanged` с debounce 250 мс; при старте прижимать к `Screens.All` | S | **P1** |
| Уведомления: «Agent finished responding», текст ошибки, «нужен ответ»; одно уведомление на сессию (предыдущее снимается); клик открывает тред | pi-gui `electron/platform/notification-manager.ts:149-174, 314-320` | **Partial**: на macOS `osascript display notification` — клик игнорируется, уведомления не заменяются (`Platform/Notifications.cs:57-58`); на Windows кнопка в панели задач мигает | macOS: `UNUserNotificationCenter` через helper либо `NSUserNotification` с `identifier` = сессия, клик → `OpenSession`. Windows toast → роадмап §1.8 | M | P2 |
| Точка непрочитанного гаснет, когда окно снова в фокусе на этой сессии | pi-gui коммит 0f704d5 | **Unclear** | — | — | P3 |
| Dock- и taskbar-бейдж | — | → роадмап §1.8 | — | — | — |
| Минимальный размер окна и адаптация под узкое окно | pi-gui `electron/main.ts:468-476` (560×600); коммит e55f96e | **Have** (`IsNarrow`, `FitPanels`, `MainWindow.axaml.cs:166-206`) | — | — | — |

## Движение и анимация

| Микро-взаимодействие | Где у них | У нас | Что сделать | Effort | P |
|---|---|---|---|---|---|
| **Токены движения** | pi-gui `tokens.css:73-78` (0.12/0.15/0.24 с, `cubic-bezier(0,0,0.2,1)`); dylansantwani `global.css:76-80`; PiPilot `globals.css:109-116`; piggy `tokens.css:72-75` (0.1/0.2/0.3 с) | **Missing** в `Views/Tokens.axaml` | `GuiMotionFast=0:0:0.12`, `GuiMotionBase=0:0:0.15`, easing `CubicEaseOut`; правило в DESIGN_SYSTEM: «только однократные переходы ≤150 мс: opacity, rotate, translate; без layout-анимаций» | S | P2 |
| Reduced motion отключает всё глобально (`animation-duration:0.001ms`) | pi-gui `styles/base.css:271-281`, `timeline.css:881-891`; Orbit `app/helpers.rs:146` | → роадмап §1.8; переходы из токенов должны выключаться одним флагом | — | — | — |
| Тост или notice появляется: translateY(4px) и fade за 150 мс; nornzach — 320 мс, translateY(10px) и scale(.98), уход 160 мс | pi-gui `main.css:2868-2880`; nornzach `stores/toast.ts:34-35`, `Toast.tsx:40` | **Missing**: `SessionCard` появляется мгновенно (`MainViewModel.Session.cs:92-102`) | Opacity и `TranslateTransform.Y` 4→0 за 150 мс у `SessionCardHost` | S | P3 |
| Модалка: scale-in (nornzach — 80 мс; dylansantwani — 320 мс), фон затемняется за 200 мс | nornzach `styles/global.css:236`; dylansantwani `global.css:885-897` | **Missing** (диалоги и viewer появляются мгновенно) | Только opacity 120 мс у фона viewer и диалога | S | P3 |
| Спиннер 900 мс linear; refresh-глиф 700 мс | pi-gui `sidebar.css:665`; Orbit `app/helpers.rs:151, 190` | **Have** (`Pulse.Spin`, `Pulse.cs:43-45`) | — | — | — |
| Сайдбар выезжает и уезжает за 300 мс ease-out | OMPiUI `Sidebar.tsx:330` | **Missing** (мгновенно) | Не делать: layout-анимация дорогая, и Codex её тоже не делает | — | — |
| Индикатор загрузки появляется с задержкой 150 мс (fade-in 200 мс), чтобы не мелькал | OMPiUI `index.css:1027`; `MIN_LOADING_MS` (`ChatArea.tsx:726`) | **Unclear** | Правило для всех «Reading…/Loading…»: показывать через 150 мс | S | P2 |

## Feedback и состояния

| Микро-взаимодействие | Где у них | У нас | Что сделать | Effort | P |
|---|---|---|---|---|---|
| Время жизни тоста: 5 с (nornzach) или 6 с (dylansantwani, Orbit); одинаковые тосты склеиваются со счётчиком; ошибка — `role=alert`, остальные — `status` | nornzach `stores/toast.ts:27, 50-57`, `Toast.tsx:27-42`; Orbit `app.rs:112` | **Partial**: `ShowBrief` держится 4 с, новая карточка заменяет предыдущую (`MainViewModel.Session.cs:113-121`) | Пауза таймера, пока курсор над карточкой; повтор того же текста продлевает показ, а не мигает | S | P3 |
| Undo для действий без потерь | — (у pi-gui `window.confirm`) | **Have**: HideProject → Undo (`MainViewModel.Sessions.cs:629-644`) | Распространить на Unpin и Remove queued message | S | P3 |
| Оптимистичное обновление | pi-gui коммит 3e1827e (reorder) | **Have** для Send и Stop (`OpenSession.OptimisticAfterVersion`, `OpenSession.cs:40-41`) | — | — | — |
| ✓ после переименования: 1400 мс | Orbit `app.rs:143` (`RENAME_FEEDBACK`) | **Missing** | Короткий ✓ в строке сессии после commit rename | S | P3 |
| «Copy failed» при ошибке буфера обмена | PiPilot `MessageContextMenu.tsx:42, 65` | **Missing**: `CopyTextRequested` не сообщает об ошибке | `ShowBrief` с текстом ошибки | S | P3 |
| Защита от повторного открытия меню в течение 200 мс после закрытия | Orbit `app/composer_ops.rs:510, 619, 680`, `pickers.rs:42` | **Partial**: только у питомца (`PetStage.axaml.cs:33`, 250 мс) | Проверить кнопки с flyout (модель, ⋯): клик, который закрывает, не должен открывать снова | S | P3 |
| Короткое подтверждение копирования: «Session ID copied» и т. п. | pi-gui — нет | **Have** (`ShowBrief`, `MainViewModel.Session.cs:430, 438`) | — | — | — |

## Клавиатура и фокус

| Микро-взаимодействие | Где у них | У нас | Что сделать | Effort | P |
|---|---|---|---|---|---|
| Лестница Esc: lightbox → palette → autocomplete → settings → popovers → stop | Orbit `app/session.rs:401` | **Have** (`MainWindow.Escape.cs`; `ContextMenus.CloseOpen`, `MainWindow.axaml.cs:1022`) | — | — | — |
| Модалка держит фокус внутри (Tab trap) и возвращает его открывшему элементу; общий стек модалок | pi-gui `fork-modal.tsx:31-40`, `ui/dialog-focus.ts`; коммиты 020fec3, aa24b44 | **Partial**: `RestoreLostFocus` возвращает фокус в composer (`MainWindow.axaml.cs:765-767`); в меню модели — на кнопку (`:91`) | Запоминать `FocusManager.GetFocusedElement()` при открытии viewer, карточки и меню и возвращать его | S | P2 |
| ⌘/Ctrl+Enter отправляет диалог (confirm, input, editor) | pi-gui `extension-session-ui.tsx:208-211` | **Unclear**: 1–9 выбирают ответ (`MainWindow.axaml.cs:1007-1011`) | ⌘↵ = primary в `SessionCard` и в вопросе | S | P2 |
| Стрелки в списках с переходом по кругу, Home/End, `scrollIntoView(nearest)` | piggy `CommandPalette.tsx:87-97`; nornzach `CommandPalette.tsx:549-599` | **Have** для модели и suggestions | — | — | — |
| Дерево: ←/→ сворачивают и разворачивают, ↑/↓ двигают | pi-gui `tree-modal.tsx:225-264` | **Unclear** (дерево Files) | ←/→ в `ListBox.file-list` | S | P3 |
| Подпись клавиши steer зависит от ОС | pi-gui e45cea9 | **Have** (`KeyboardShortcuts.Steer`) | — | — | — |
| Пока зажат ⌘, видны цифры на сессиях и вкладках | pi-gui `modifier-hints.ts` | см. Sidebar | — | — | — |

## Буфер обмена и drag & drop

| Микро-взаимодействие | Где у них | У нас | Что сделать | Effort | P |
|---|---|---|---|---|---|
| Copy session ID из меню строки | pi-gui `thread-actions.tsx:117-122`, `hooks/use-thread-actions.ts:153-156` | **Partial**: только в меню шапки (`SessionTitle.axaml:28`) | Добавить в `SessionRowMenu` | S | P2 |
| Что копирует карточка инструмента: diff или input+output | pi-gui `timeline-item.tsx:287-292` | **Missing** | см. «Карточки инструментов» | — | — |
| Copy selection / Copy message: если внутри сообщения есть выделение, копируется оно | PiPilot `MessageContextMenu.tsx:74-97` | **Partial**: при выделении срабатывает собственный Copy у `SelectableTextBlock` (`ContextMenus.cs:24-27, 155-162`), но выделение не выходит за абзац (→ роадмап §1.1) | — | — | — |
| Вставка картинок по ⌘V; длинный текст становится чипом | pi-gui `composer-attachments.ts:25` | **Have** (`MainWindow.axaml.cs:937-940`) | — | — | — |
| Drop файлов в любое место окна уходит в composer | — | **Have** (`MainWindow.axaml.cs:77-81`) | — | — | — |
| Drop папки на сайдбар добавляет проект | OMPiUI | **Missing** (см. Sidebar) | — | — | — |
| Copy image в viewer со статусом «Copied» | — | **Have** (`ImageViewer.axaml.cs:48-52`) | — | — | — |
| Перетащить сессию в новое окно | pi-heao-gui v1.3.0 | → роадмап §1.3 (multi-window) | — | — | — |

---

## Top 20 quick wins (S, по ценности)

1. **✓ при копировании сообщения и ответа** (`row-action` Copy): тот же `IconCheck` на 1,5 с, что у блока кода. Один attached-behavior на все Copy-кнопки. P1.
2. **Copy в карточке инструмента**: diff или command+output, иконка в `card-head`, с ✓. P1.
3. **ПКМ по карточке инструмента**: Copy command / Copy output / Copy path / Open in Files. P1.
4. **ПКМ по ссылке на путь в тексте** (`PathLinks`): взять пункты `FileMenu` из FilesPane. P1.
5. **Восстанавливать положение и размер окна**: normal bounds + maximized, прижатие к мониторам, debounce 250 мс. P1.
6. **Токены движения** `GuiMotionFast 120 мс / Base 150 мс, CubicEaseOut` и правило в DESIGN_SYSTEM. P2.
7. **Меню строки сессии**: Mark as read (у непрочитанных), Copy session ID, Show in Finder, колонка с хоткеем. P2.
8. **Сохранять ширины side pane, preview и terminal** рядом с `SidebarWidth`. P2.
9. **Drop-зона composer**: tint-фон и подпись «Drop images or files to attach». P2.
10. **Кнопка «к последнему»** с точкой или подписью «New», если внизу прибыло новое, и fade 150 мс. P2.
11. **Drop папки на сайдбар** → «Add project». P2.
12. **ПКМ в терминале**: Copy selection / Copy all / Paste; у ссылок — Open / Copy. P2.
13. **Возврат фокуса инициатору** после viewer, карточки и меню (а не всегда в composer). P2.
14. **Плавный показ кнопок по hover** (opacity 120 мс): `row-action`, `session-action`, copy блока кода. P3.
15. **Шевроны поворачиваются за 150 мс** — одним стилем для всех `Icon.chevron`. P3.
16. **ПКМ по ссылке**: «Open in Browser preview». P2.
17. **Карандаш у каждого сообщения в очереди** (сейчас только ↑ на последнем). P2.
18. **Ручка сайдбара с клавиатуры**: ←/→ ±20 px, Home/End. P3.
19. **Пауза тоста под курсором**, повтор того же тоста продлевает показ, «Copy failed» при ошибке. P3.
20. **ПКМ по вкладкам терминала и preview**: Close / Close others / Close to the right / Copy URL. P3.

Дальше по ценности: ⌘↵ = primary в карточках и вопросах, подсветка строки таблицы, «Insert into message» у блока кода, «Copy contents» в viewer, нажатая Send с `scale(0.94)`, ✓ после rename.
