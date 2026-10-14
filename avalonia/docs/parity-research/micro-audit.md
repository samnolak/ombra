# Micro-UX аудит Ombra (только свой UI)

Источник: `avalonia/src/Ombra.App` (все `.axaml`, `Controls/*`, `Views/*`, `ViewModels/*`), `avalonia/docs/USER_GUIDE.md`, `avalonia/docs/design/DESIGN_SYSTEM.md`.
Колонка «Ожидается по конвенции» описывает норму Codex desktop, Claude Code desktop и macOS HIG и коротко объясняет, почему это норма. Если ссылки на changelog нет, пункт основан на конвенции, а не на найденной версии.
Статусы: **Have**, **Partial**, **Missing**, **Unclear**. Все доказательства указаны как `файл:строка` или символ. Пункты из `PARITY_ROADMAP.md` (поиск, палитра, reduced motion как фича, archive и т.п.) здесь не повторяются; если они касаются микро-деталей, есть только короткая отсылка.

---

## 0. Сводная матрица интерактивных элементов

Обозначения: ✓ есть, ✗ нет, — неприменимо. Колонки: КМ — контекстное меню, Hover, Pressed, Focus — кольцо `:focus-visible`, Tip — тултип, Hand — курсор-рука, Клав. — доступ с клавиатуры.

| Элемент | КМ | Hover | Pressed | Focus | Tip | Hand | Клав. | Несогласованность |
|---|---|---|---|---|---|---|---|---|
| Строка сессии `Button.session` | ✓ `SessionRowMenu` (MainWindow.axaml:269-273) | ✓ Fill1Hover на всей строке (Styles.Workspace.axaml:87) | ✗ (нет в списке `:pressed`, Styles.axaml:163) | ✓ (Styles.axaml:79) | ✓ `Tooltip` | ✗ | только Tab, без ↑/↓ (`ListBox.sessions ListBoxItem Focusable=False`, Styles.Workspace.axaml:46-49) | В Files-дереве стрелки работают, в сайдбаре нет |
| Заголовок проекта `Grid.group-header` | ✓ 6 пунктов (MainWindow.axaml:173-221) | только показ кнопок ⊖/+ (Styles.Workspace.axaml:70-75), фона нет | — | ✗ (не фокусируется) | ✓ путь | ✗ | ✗ нельзя свернуть или открыть меню | Строки сессий подсвечиваются, заголовок проекта нет |
| Заголовок «Chats» | ✓ 1 пункт (243-255) | только «+» | — | ✗ | ✗ (у проекта есть) | ✗ | ✗ | Тултип есть у проекта, у Chats нет |
| `sidebar-action` (New chat, Settings, Update) | — | ✓ | ✗ | ✓ + хоткей проявляется на focus (Styles.Workspace.axaml:33) | ✓ | ✗ | ✓ | |
| Иконки-тогглы в шапке (`Button.icon`) | — | ✓ | ✓ | ✓ | ✓ с шорткатом (`FilesTip`…) | ✗ | ✓ | |
| Пузырь пользователя `Border.user-bubble` | ✓ Copy / Rewind (23-44) | действия по hover/focus-within (Styles.Session.axaml:189-194) | — | — | ✓ на действиях | ✗ | ✓ focus-within | |
| Ответ ассистента `StackPanel.assistant` | ✓ Copy / Copy as MD / Retry (45-70) | Copy только в футере хода (534-540) | — | — | ✓ | ✗ | — | |
| Мысли (`thinking-toggle`, `thinking-body`) | ✗ своего нет, всплывает ReplyMenu, «Copy thinking» нет | ✓ цвет (Styles.Session.axaml:204) | ✗ | ✓ | ✗ | ✗ | ✓ | |
| Строка инструмента `ToggleButton.tool-row` | ✗ | ✓ (Styles.Session.axaml:316) | Fluent по умолчанию | ✓ | ✓ только если обрезано (`TrimmedToolTip`, 574) | ✗ | ✓ Space/Enter | У кода и ссылок КМ есть, у инструмента нет |
| Путь в строке инструмента `Button.tool-path` | ✗ | ✓ подчёркивание (Styles.Files.axaml:292-295) | =hover | ✓ | ✓ «Open in the Files pane» | ✓ (Styles.Files.axaml:287) | ✓ | В Files-панели у того же файла 6 пунктов КМ (FilesPane.axaml:16-64), здесь ни одного |
| Группа «Explored…» | ✗ | ✓ | — | ✓ | ✗ | ✗ | ✓ | |
| Шапка консоли/вывода `Button.card-header` | ✗ | ✓ | =hover (Styles.Session.axaml:445) | ✓ | ✓ «Collapse» | ✓ (443) | ✓ | Нет Copy output/command |
| Карточка диффа (`DiffView`) | ✗ | у строк нет hover | — | — | ✓ путь (DiffView.cs:277,283) | ✓ путь и collapse | двойной клик по строке открывает файл (DiffView.cs:360) | Нет Copy patch / Copy path |
| Файлы хода `Button.turn-file` | ✗ | ✓ (Styles.Session.axaml:594) | =hover | ✓ | ✓ | ✓ (592) | ✓ | |
| Code block | ✓ Copy code (MarkdownView.cs:832) | ✓ Copy по hover (Styles.Session.axaml:830) | ✓ (icon) | ✓ focus-within | ✓ | ✗ | ✓ | |
| Ссылка `md-link` | ✓ Open/Copy (MarkdownView.cs:749) | ✓ (HyperlinkButton) | — | ✓ | ✗ URL не виден | ✓ Fluent HyperlinkButton [INFERENCE] | ✓ | Нет «Open in Preview», хотя браузер встроен |
| Путь в тексте (`PathLinks`) | ✗ (правый клик даёт ReplyMenu) | ✓ подчёркивание и peek-тултип (MarkdownView.cs:610-617) | — | ✗ | ✓ превью | ✓ (615) | ✗ | |
| Миниатюра картинки `Button.image-thumb` | ✓ только «Open» (91-102) | ✓ рамка | — | ✓ | ✓ | ✓ (Styles.Images.axaml:28) | ✓ | Copy есть в просмотрщике (ImageViewer.axaml.cs:51), в меню миниатюры нет |
| Чип вложения `Border.attachment` | ✓ Open/Remove (71-90) | ✗ | — | — | ✓ имя | — | × | |
| Чип вставленного текста `paste-chip` | ✗ | ✗ | — | — | ✓ превью (1292) | — | × | Несогласованно с чипом вложения |
| Чип код-комментария | ✗, нет правки по клику | ✗ | — | — | ✓ | — | × | Чип комментария к странице правится по клику (`BeginEditCommand`, 1339) |
| Сообщение в очереди | ✗ | ✗ | — | — | ✓ | — | × и ↑ | |
| Чипы композера (`chip ghost`) | — | ✓ | ✓ (Styles.axaml:163) | ✓ | ✓ (у режима есть Shift+Tab) | ✗ | ✓ | |
| Send/Stop | — | ✓ | ✓ | ✓ | ✓ с клавишей | ✗ | ✓ HotKey ⌘↵ (MainWindow.axaml.cs:620) | |
| Варианты ответа диалога `Button.option` | — | Fluent | Fluent | ✓ | ✗ | ✗ | ✓ цифры 1–9 | |
| Вкладка терминала `Border.tab` | ✗ | ✗ (стиль только `.selected`, Styles.axaml) | ✗ | ✓ (кнопка) | ✗ у заголовка | ✗ | ✗ средний клик | У вкладок Preview hover есть (`Border.preview-tab Button.flat`) |
| Вкладка Preview | ✗ | ✓ | ✓ | ✓ | ✓ | ✗ | ✗ средний клик, ⌘W | |
| Строка Files | ✓ 6 пунктов | ✓ | ✓ | ✓ | ✓ | ✗ | ✓ ↑↓→← Enter (FilesPane.axaml.cs) | Эталон для остальных списков |
| Навигация настроек `settings-nav` | ✗ | ✓ (Styles.Workspace.axaml:218) | ✗ | ✓ | ✗ | ✗ | Tab, без ↑/↓ | |
| Сплиттеры панелей `GridSplitter` | — | ✗ линия статична (Styles.axaml:723-731) | — | не фокусируются (by design) | ✗ | Unclear | ✗ двойной клик | У сайдбара 6 px, подсветка и dbl-click (MainWindow.axaml:328; Styles.Workspace.axaml:168; MainWindow.axaml.cs:440-443). У панелей `Width="1"` (MainWindow.axaml:797,804,809,879) |
| Switch | — | ✓ | — | ✓ | — | ✓ | ✓ | Единственный контрол с переходом |
| Segmented | — | ✓ текст | ✗ | ✓ | ✓ | ✗ | ✓ | |
| TextBox | ✓ Cut/Copy/Paste/Select all (Styles.axaml:459-468) | ✓ | — | ✓ синяя рамка | — | I-beam | ✓ | |
| Notice-строка | ✗ | — | — | — | ✗ | — | — | Нельзя скопировать уведомление целиком |
| Event log (F12) | ✗ | — | — | ✓ ListBoxItem | ✗ | — | ✓ | Строки — `TextBlock` (MainWindow.axaml:896), копировать нельзя вообще |

---

## Sidebar

| Микро-взаимодействие | Ожидается по конвенции | Наше (статус + доказательство) | Что сделать | Effort | Prio |
|---|---|---|---|---|---|
| ↑/↓ по списку чатов | Codex и Claude: фокус в списке, стрелки ходят по чатам, Enter открывает. HIG: source list с клавиатурной навигацией | **Missing**: `ListBoxItem` групп `Focusable=False` (Styles.Workspace.axaml:46-49), сессии — отдельные `Button`, Tab заходит в каждую строку и ещё в pin и ⋯ (по 3 стопа на строку, 297-311) | Обработчик ↑/↓/Home/End на `SessionList` с переходом между `Button.session`; pin/⋯ сделать `IsTabStop=False` (они уже в КМ) | M | P2 |
| Hover-фон заголовка проекта | Строка, по которой можно кликнуть правой кнопкой, подсвечивается как соседние строки | **Missing**: у `Grid.group-header` hover только раскрывает кнопки (Styles.Workspace.axaml:70-75) | `Grid.group-header:pointerover` → лёгкий Fill1 или вторичный цвет текста | S | P3 |
| Свернуть или развернуть проект | Codex: группы проектов складываются по клику на заголовок. Финдер и source list: шеврон | **Missing**: ни в `SessionGroupViewModel`, ни в стилях нет Collapse/IsExpanded | Шеврон и `IsCollapsed` на группе, хранить в настройках как `PinnedProjects` | M | P2 |
| Тултип у заголовка «Chats» | Если у одного заголовка есть тултип, он ожидается и у соседнего | **Missing**: тултип есть у проекта (171), у Chats нет (242) | Тултип «Чаты без проекта — папка в данных приложения» | S | P3 |
| Пустой результат поиска с действием | macOS search field: «No results» и способ сбросить | **Partial**: «No sessions match» (322) без кнопки, сбрасывает только Esc (MainWindow.Escape.cs:25-29) | Ссылка «Clear search» в пустом состоянии и подсветка совпавших букв в заголовках | S | P3 |
| Первый запуск (пусто) | Пустой список объясняет, что делать | **Partial**: `ShowNoSessionMatch` срабатывает только при фильтре (MainViewModel.Sessions.cs:52) | Пустое состояние «Нет сессий — New chat / Add project» | S | P3 |
| Скелетон при первом скане каталога | Codex и Claude показывают серые строки, пока список грузится | **Unclear**: индикатора загрузки каталога не нашёл | Если первый скан дольше 200 мс, показывать 5 строк-скелетонов (Pulse) | S | P3 |
| Переименование двойным кликом | Codex и Finder: двойной клик или Enter переименовывают на месте | **Partial**: Rename через меню открывает сессию и правит имя в шапке (USER_GUIDE.md:97). У проекта переименование на месте есть (`ProjectNameBox`, Enter/Esc/blur, ProjectNameBox.cs:19-33) | `DoubleTapped` на строке → `RenameCommand` | S | P3 |
| Переименование: Enter, Esc, blur, SelectAll | Стандарт для полей на месте | **Have**: шапка (MainWindow.axaml.cs:82, 564-565, 828-843) и проект (ProjectNameBox.cs:19-33) | — | — | — |
| Hover-действия pin и ⋯ вместо возраста | Codex и Claude | **Have**: Styles.Workspace.axaml:104-123, видны и при `:focus-visible`, и при `:flyout-open` | Добавить fade 100 мс (см. Motion) | S | P3 |
| Относительное время «5m» обновляется | Возраст не должен застывать | **Have**: таймер раз в минуту (MainViewModel.Sessions.cs:378-390) | Тултип с абсолютной датой на `session-age` | S | P3 |
| Ресайз сайдбара | HIG split view: курсор ↔, подсветка, двойной клик сбрасывает | **Have**: 6 px, `SizeWestEast`, подсветка, dbl-click сброс (MainWindow.axaml:328-331; Styles.Workspace.axaml:164-170; MainWindow.axaml.cs:440-443) | — | — | — |
| Перетаскивание порядка закреплённых проектов | Codex и Claude: drag-reorder pinned | **Missing**: drag-кода нет (grep `DoDragDrop` пуст) | Drag по заголовку закреплённого проекта, порядок в `PinnedProjects` | M | P3 |
| Drop папки на сайдбар = Add project | Codex: перетащить папку, чтобы добавить проект | **Missing**: любой drop идёт в композер (MainWindow.axaml.cs:78-81, 982-989) | Drop каталога над `Sidebar` → `AddProject` с подсветкой сайдбара | S | P3 |

## Transcript rows & messages

| Микро-взаимодействие | Ожидается по конвенции | Наше | Что сделать | Effort | Prio |
|---|---|---|---|---|---|
| Ввод с клавиатуры уходит в композер (type-to-focus) | Claude desktop и ChatGPT: набор, когда фокус в разговоре, попадает в поле ввода | **Missing**: клик в разговоре забирает фокус (ChatScrollController.cs:56-60), а `TextInputEvent` на окне только глушит цифры диалога (MainWindow.axaml.cs:37) | В туннеле `TextInput`: если фокус в `Transcript`/сайдбаре и нет выделения → `Composer.Focus()` и передать символ | S | P1 |
| Контекстное меню сообщения пользователя | Codex и Claude: Copy, Edit/Rewind | **Have**: (23-44) | — | — | — |
| Меню ответа: Copy, Copy as Markdown, Retry | Claude | **Have**: (45-70) | — | — | — |
| Copy мыслей | Claude: у развёрнутых мыслей есть копирование | **Missing**: `thinking-body` — `SelectableTextBlock` без меню, правый клик даёт ReplyMenu без мыслей (518-520) | Пункт «Copy thinking» в ReplyMenu при `HasThinking` | S | P3 |
| Notice (ошибка или предупреждение): Copy | Ошибку нужно уметь скопировать в баг-репорт одним действием | **Partial**: текст выделяемый (425), но меню нет | `ContextFlyout` с Copy на `Border.notice` | S | P3 |
| Тултип со временем сообщения | Claude VSCode 2.1.284/2.1.290: время у промптов | **Missing** (уже в parity research, claude-a.md:13) | Минимум — тултип с временем на пузыре и футере хода | S | P3 |
| Кнопка «Show more» в длинном пузыре | Свернуть или развернуть длинное сообщение | **Have**: `FoldLabel` (493-494) | Курсор-рука, как у `tool-more` (см. Hand) | S | P3 |
| Выделение через несколько сообщений | Claude и ChatGPT: можно выделить текст через границы сообщений | **Missing** (каждая строка — отдельный `SelectableTextBlock`) | Ограничение Avalonia, только кастомный selection layer | L | P3 |
| Hover-действия ряда | Claude: Copy и Rewind на hover и focus-within | **Have**: Styles.Session.axaml:189-194 | Fade (Motion) | S | P3 |
| Ответ с ошибкой и Retry | Claude | **Have**: (524-532) | — | — | — |
| Sticky-заголовок мыслей и консоли | Codex | **Have**: `StickyHeader` (509, 627) | — | — | — |
| Маркер компакции | Codex: разделитель «Compacted» | **Have**: (729-733) | — | — | — |

## Tool & diff cards

| Микро-взаимодействие | Ожидается по конвенции | Наше | Что сделать | Effort | Prio |
|---|---|---|---|---|---|
| КМ строки инструмента: Copy command, Copy output, Open file, Copy path | Codex и Claude: у вызова инструмента есть Copy. У нас КМ есть у кода и ссылок, у инструментов нет — несогласованность | **Missing**: у `ToolRowViewModel` нет Copy-команд (RowViewModels.cs:613-618 — только ShowAll и Collapse); `ContextFlyout` в шаблоне (562-678) отсутствует | `ContextMenus.Menu(...)` или XAML-Flyout: Copy command, Copy output, Open in Files, Copy path | S | P1 |
| Кнопка Copy в шапке консоли или вывода | Codex: copy-иконка у блоков вывода, как у code block | **Missing**: в `card-header` только Collapse (628-636, 663-671) | Иконка Copy с ✓ (переиспользовать логику MarkdownView.cs:812-818) | S | P2 |
| КМ пути в строке инструмента, файлов хода, шапки диффа | Тот же файл в панели Files имеет Open, Add to message, Open in default app, Reveal, Copy path, Copy relative (FilesPane.axaml:16-64) | **Missing**: `tool-path` (595-599), `turn-file` (703-716), `diff-path-link` (DiffView.cs:276-281) без меню | Общий `ContextMenus.ForPath(fullPath)` с теми же 5–6 действиями | M | P1 |
| КМ пути в тексте ответа (`PathLinks`) | Как выше | **Missing**: hover-peek и клик есть (MarkdownView.cs:604-617), правый клик даёт ReplyMenu | `ContextMenus.ForPath` на hovered run | M | P2 |
| Copy patch / Copy diff | Codex: копирование диффа | **Missing** (DiffView.cs: нет Copy) | Пункт в КМ карточки: Copy patch (unified) | S | P3 |
| Hover строки диффа | GitHub и Codex: строка подсвечивается, двойной клик открывает | **Partial**: двойной клик есть (DiffView.cs:360), подсветки строки нет | `Grid.diff-row:pointerover` с лёгкой заливкой | S | P3 |
| Тултип на сжатой строке инструмента | Показывать полный текст, только если он обрезан | **Have**: `TrimmedToolTip.IsEnabled` (574) | — | — | — |
| Шеврон по hover и focus | Codex | **Have**: Styles.Session.axaml:325-327 | Поворот с переходом (Motion) | S | P2 |
| «… +N lines» | Codex | **Have**: `tool-more` с Hand (Styles.Session.axaml:524) | — | — | — |

## Composer

| Микро-взаимодействие | Ожидается по конвенции | Наше | Что сделать | Effort | Prio |
|---|---|---|---|---|---|
| ↑ в пустом поле вызывает прошлые промпты | Claude Code CLI и Codex CLI: ↑/↓ листают историю промптов | **Partial**: ↑ только возвращает последний сообщение из очереди (`EditLastQueued`, MainWindow.axaml.cs:904-907); истории нет (grep `PromptHistory` пуст) | История отправленных промптов на сессию, ↑/↓ при каретке в начале или конце | M | P2 |
| Черновик переживает перезапуск | Codex и Claude: черновик чата не теряется | **Partial**: черновик хранится на открытый чат в памяти (`OpenSession.ComposerText` и др., OpenSession.cs:45-54), на диск не пишется | Сохранять `ComposerText` (без картинок) по session file с дебаунсом | M | P2 |
| КМ и превью чипа «Pasted text» | Claude: чип вставки раскрывается или просматривается | **Partial**: только тултип и × (1292-1301) | Клик показывает превью в Flyout; КМ: Expand into text / Copy / Remove | S | P2 |
| Правка чипа код-комментария | Как у чипа комментария к странице (`BeginEditCommand`, 1339-1371) | **Missing**: только × (1312-1321) | Тот же Popup-редактор | M | P2 |
| КМ сообщения в очереди: Edit / Steer now / Remove | Claude Code: отредактировать или отправить сейчас | **Partial**: × и ↑ (1257-1284) | `ContextFlyout`: Edit, Send now (Steer), Remove | S | P2 |
| Undo удаления чипа (×) | ⌘Z или Undo-тост сразу после × | **Missing** | Хранить последний удалённый чип 5 с; Undo на brief-карточке | S | P3 |
| Подсветка drop-зоны | Claude и Codex: оверлей «Drop to attach» | **Partial**: синяя рамка 2 px на композере (Styles.Composer.axaml:19-22), текста нет | Подсказка «Отпустите, чтобы приложить» поверх композера | S | P3 |
| Фокус-рамка композера | Claude: рамка карточки усиливается при фокусе | **Have**: `:focus-within` → BorderStrong (Styles.Composer.axaml:16-18) | — | — | — |
| Slash и @-меню с клавиатуры | Claude Code | **Have**: ↑↓, Tab, Shift+Tab, Enter, Esc (MainWindow.axaml.cs:870-895) | — | — | — |
| Модель-меню: фокус, стрелки, возврат фокуса | Claude | **Have**: фокус в фильтр, ↑↓→←, Esc возвращает фокус на чип, выбор — в композер (MainWindow.axaml.cs:86-95, 773-816) | — | — | — |
| Shift+Tab — режим разрешений | Claude Code | **Have**: (897-903) | — | — | — |
| Ошибка вставки | Сказать, почему вставка не удалась | **Have**: `ComposerMessage` (MainWindow.axaml.cs:959-962; 1403-1406) | — | — | — |
| Плейсхолдер в одну строку с … | — | **Have**: Styles.axaml (Composer placeholder NoWrap) | — | — | — |

## Panes (Files, Plan, Tasks, Terminal, Preview)

| Микро-взаимодействие | Ожидается по конвенции | Наше | Что сделать | Effort | Prio |
|---|---|---|---|---|---|
| Ширина зоны захвата сплиттера | HIG: зона захвата ≥ 5–8 px, даже если линия 1 px. Наш сайдбар — 6 px | **Partial**: у четырёх сплиттеров `Width="1"` (MainWindow.axaml:797,804,809,879); прозрачная полоса в шаблоне (Styles.axaml:723-731) зону не расширяет, потому что bounds = 1 px | `Width=7` и `Margin=-3,0`, как у `SidebarGrip` | S | P1 |
| Hover-подсветка сплиттера | Как у сайдбара: линия загорается `GuiFocus` | **Missing**: шаблон статичен (Styles.axaml:723-731) | `GridSplitter:pointerover` → Background `GuiFocus`, внутренняя линия 2 px | S | P1 |
| Двойной клик по сплиттеру сбрасывает ширину | Как у сайдбара (MainWindow.axaml.cs:440-443) | **Missing** | `DoubleTapped` → дефолт (`_previewWidth = 520` и т.д.) | S | P2 |
| Ширины панелей переживают перезапуск | Codex и Claude помнят ширины | **Partial**: `_previewWidth/_sidePaneWidth/_terminalWidth/_debugWidth` хранятся только в полях окна (MainWindow.axaml.cs:167-176); в `OmpRuntimeOptions` их нет (OmpRuntimeOptions.cs:36-80) | Поля `PaneWidths` в настройках по образцу `SidebarWidth` | S | P1 |
| Открытые панели после перезапуска | Codex: панели восстанавливаются | **Missing**: `IsTerminalOpen`, `ActivePane`, `IsPreviewOpen` не сохраняются | Сохранять набор открытых панелей | S | P2 |
| Files: КМ, стрелки, Go to file, Esc очищает | VS Code и Claude | **Have**: FilesPane.axaml:16-69, 94-105; FilesPane.axaml.cs:118-140 | — | — | — |
| Files: раскрытые папки помнятся | VS Code помнит дерево | **Partial**: в памяти, refresh сохраняет открытые узлы (FilesViewModel.cs:469-470), между перезапусками и проектами — нет | Сохранять раскрытые пути на проект | S | P3 |
| Files: перетаскивание файла наружу (в композер, Finder) | VS Code и Finder | **Missing**: `DoDragDrop` нигде нет | Drag строки → `DataFormat.File`, в композер как `@path` | M | P3 |
| Files: пустые и загрузочные состояния | — | **Have**: «Loading files…», «Searching…», «No project folder» с кнопкой (FilesPane.axaml:178-201) | Заменить текст на скелетон или спиннер, если ожидание > 300 мс | S | P3 |
| Терминал: hover вкладки | Вкладки ведут себя одинаково: у Preview hover есть | **Missing**: у `Border.tab` стиль только `.selected` (Styles.axaml `Border.tab`) | Hover Fill1 на `Border.tab` | S | P2 |
| Терминал: средний клик закрывает, ⌘W, КМ (Rename, Close others) | Браузеры, VS Code, iTerm | **Missing**: (MainWindow.axaml:819-834) | `PointerReleased` с MiddleButton → Close; КМ вкладки | S | P2 |
| Терминал: Copy, Paste, Clear в КМ, ⌘K | macOS Terminal: ⌘K очищает, КМ есть | **Missing**: КМ у `TerminalHost` нет, команды Clear нет | `ContextFlyout`: Copy, Paste, Clear, Select all | S | P2 |
| Терминал: фокус при открытии и выборе вкладки | Набор сразу уходит в shell | **Have**: `FocusTerminal` (MainWindow.axaml.cs:680-720) | — | — | — |
| Терминал: длинный заголовок вкладки | Обрезать и показывать тултип | **Missing**: у `TextBlock Title` нет `MaxWidth` и `TextTrimming`, тултипа нет (824) | MaxWidth 160, ellipsis, тултип | S | P3 |
| Preview: ⌘L адрес, ⌘R reload, ⌘[ / ⌘] назад и вперёд, ⌘W закрыть вкладку | Конвенции браузеров (Safari) | **Missing**: из клавиш есть только Enter в адресе (PreviewPanel.axaml:86) | `KeyBindings` на `PreviewPanel` (учесть перехват WebKit в `NativeInput`) | S | P2 |
| Preview: средний клик и КМ вкладки | Браузеры | **Missing**: (PreviewPanel.axaml:189-196) | Как у терминала | S | P3 |
| Preview: × вкладки по hover | Safari и Chrome: × на неактивных вкладках появляется по hover | **Partial**: × виден всегда (`IsVisible=CanClose`, 191) | Opacity 0 → 1 на hover или active, освобождает место под заголовок | S | P3 |
| Preview: загрузка, ошибка, крэш, пусто | — | **Have**: ProgressBar, error с Retry, крэш с Reload, пустое состояние (по отчёту scout: PreviewPanel.axaml:73-75, 231-273, 176-181) | — | — | — |
| Image viewer: Esc, ←/→, Space, Copy, Open | HIG Quick Look | **Have**: DESIGN_SYSTEM.md:119; ImageViewer.axaml.cs:48-52 | Copy image в КМ миниатюры (см. Clipboard) | S | P2 |
| Event log: Copy строки и всего лога, Clear | Лог для баг-репортов нужно копировать | **Missing**: `TextBlock` (MainWindow.axaml:896), КМ нет | `SelectableTextBlock` и КМ: Copy line, Copy all, Clear | S | P2 |
| Event log: пустое состояние | — | **Missing** (892-899) | «Событий пока нет» | S | P3 |
| Plan и Tasks: пустые состояния, свёртки | — | **Have**: по отчёту scout (PlanPane.axaml:37-44; TasksPane.axaml:14-22) | Переходы шеврона (Motion) | S | P3 |

## Settings

| Микро-взаимодействие | Ожидается по конвенции | Наше | Что сделать | Effort | Prio |
|---|---|---|---|---|---|
| Esc закрывает настройки, фокус возвращается | Claude: Esc / «Back to app» | **Have**: MainWindow.axaml.cs:1050-1055; фокус — в композер через `RestoreLostFocus` (585-586, 762-767) | — | — | — |
| Фокус при открытии настроек | HIG: фокус на текущий пункт навигации или первое поле | **Missing**: при открытии только скролл (588-593); `RestoreLostFocus` не находит видимый композер, фокус теряется | Фокус на `Button.settings-nav.current` | S | P2 |
| ↑/↓ по разделам | System Settings (macOS): стрелки в сайдбаре | **Missing**: `ItemsControl` из `Button` (1740-1755) | ↑/↓ меняют `SettingsCategory` | S | P3 |
| Последний раздел помнится | System Settings открывается на последнем разделе | **Partial**: только в сессии (`SettingsCategory`; в `OmpRuntimeOptions` поля нет) | Сохранять ключ раздела | S | P3 |
| Мгновенное применение без «Saved» | HIG: настройки применяются сразу, без подтверждения | **Have**: тема, уведомления, язык (MainViewModel.Settings.cs:325,353,358) | — | — | — |
| Явное сохранение рантайма: Unsaved, Discard | — | **Have**: (MainWindow.axaml:2033-2038) | — | — | — |
| Удаление провайдера, коннектора, правила | Codex: inline-подтверждение или Undo | **Partial**: inline-подтверждение (CustomProvidersSettings.axaml ~287-295, ConnectorsSettings.axaml ~277-286 по отчёту scout), Undo после удаления нет | Для правил разрешений (локальные данные) — Undo вместо подтверждения | S | P3 |
| Копирование путей в Settings (файл настроек, папки) | Рядом с путём — Copy или Reveal | **Partial**: `SelectableTextBlock` (2074) без кнопок | Иконки Copy (с ✓) и Reveal | S | P3 |
| Тултип у отключённых контролов: почему выключено | HIG: объяснять недоступность | **Missing**: `ToolTip.ShowOnDisabled` нигде не задан (grep пуст) | Глобально `ToolTip.ShowOnDisabled=True` для `Button`, `ToggleButton` и тексты причин | S | P3 |
| Прогресс обновления | — | **Have**: ProgressBar (2000-2001), статус (2002) | — | — | — |

## Window & OS

| Микро-взаимодействие | Ожидается по конвенции | Наше | Что сделать | Effort | Prio |
|---|---|---|---|---|---|
| Восстановление размера, позиции и Maximized окна | HIG и все desktop-приложения (Codex, Claude): окно открывается там, где было закрыто | **Missing**: жёстко 1100×760 `CenterScreen` (MainWindow.axaml:15-17); `SetWindowState` только в памяти (MainViewModel.cs:148-172); в `OmpRuntimeOptions` полей окна нет | Сохранять `Position/Width/Height/WindowState` при закрытии, при старте проверять, что окно на видимом экране | M | P1 |
| Видимость сайдбара после перезапуска | Codex и Claude помнят скрытый сайдбар | **Missing**: `IsSidebarVisible` не сохраняется (сохраняется только ширина, MainViewModel.Sessions.cs:82-86) | Поле `SidebarHidden` | S | P2 |
| Заголовок окна с маркером внимания | — | **Have**: «● » при `NeedsAttention` (MainViewModel.Attention.cs:20) | — | — | — |
| Бейдж на иконке в Dock: сколько чатов ждут | macOS-конвенция: Mail, Slack, Claude desktop показывают badge | **Missing**: grep `badge` пуст | `NSApp.dockTile.badgeLabel` через `MacObjC` = число ждущих и непрочитанных | S | P2 |
| Подпрыгивание иконки в Dock при вопросе в фоне | HIG: `requestUserAttention` | **Partial**: на Windows есть `FlashWindowEx` (Notifications.cs:61-78), на macOS — только notification | `[NSApp requestUserAttention:NSInformationalRequest]` | S | P2 |
| Звук уведомления | macOS: опциональный звук | **Missing**: `display notification … with title` без `sound name` (Notifications.cs:58) | Переключатель «Sound» в Settings → General | S | P3 |
| Клик по уведомлению открывает нужный чат | — | **Have**: MainWindow.axaml.cs:541-551 | — | — | — |
| Edit-меню в menu bar на любом поле | HIG | **Have**: MainWindow.Menus.cs:35-80 | — | — | — |
| Каретка перестаёт мигать в простое | GTK и macOS | **Have**: MainWindow.axaml.cs:107-110 | — | — | — |
| Последний чат открывается при старте | Codex и Claude открывают последний разговор | **Unclear**: при старте возобновляется проект (`ProjectToResume`, App.axaml.cs:80); что открывается — последняя сессия или новый чат — по коду однозначно не видно | Проверить и при необходимости открывать последнюю сессию | S | P2 |

## Motion & animation

**Что анимируется сейчас (полный перечень):**
- Switch: `BrushTransition` трека, 120 мс, linear; `ThicknessTransition` ручки, 120 мс, `CubicEaseOut` (Styles.axaml:512-519).
- PetPerch: `DoubleTransition` Opacity, 120 мс (Styles.Pets.axaml:15-16).
- `ctl:Pulse`: opacity 1→Low, синус, 1,1 с в каждую сторону, ≤30 fps (Pulse.cs:11-41). Где используется: `state-dot.running` 0.3 (Styles.axaml), `thinking-label.live` 0.45 (Styles.Session.axaml:221), `session-dot.running` 0.3 (Styles.Workspace.axaml:142), `session-hand` 0.55 (148). `Pulse.Spin` 0,9 с у `tool-mark.running` (Styles.Session.axaml:352).
- Питомцы: `PetLife` (EaseOutCubic-блендинг настроений), `PetView` по таймеру.
- По таймеру, без визуального перехода: ✓ на Copy в code block на 1,5 с (MarkdownView.cs:817), brief-карточка исчезает через 4 и 8 с (MainViewModel.Session.cs:117; Sessions.cs:649), elapsed каждую 1 с, возраст в сайдбаре каждые 60 с.
- Токенов движения (длительность, easing) в `Tokens.axaml` нет (grep `Duration|Motion|Easing` пуст), в `DESIGN_SYSTEM.md` про движение есть только раздел о Pulse (227-230).

| Микро-взаимодействие | Ожидается по конвенции | Наше | Что сделать | Effort | Prio |
|---|---|---|---|---|---|
| Токены движения | Единая шкала длительностей (Claude и Codex — около 100/150/200 мс, ease-out) | **Missing** | В Tokens.axaml: `GuiMotionFast` 0:0:0.1, `GuiMotionBase` 0:0:0.16, `GuiEaseOut`; описать в DESIGN_SYSTEM | S | P2 |
| Поворот шевронов | Codex и Claude: шеврон поворачивается за 150 мс | **Missing**: везде мгновенный `RenderTransform` — expander (Styles.axaml:625-630), todo-head (649-655), thinking-toggle (Styles.Session.axaml:208), tool-row (319), turn-files, pane-fold, tree-chevron | Один стиль `ctl|Icon.chevron` с `TransformOperationsTransition` | S | P2 |
| Раскрытие и сворачивание (мысли, вывод, дифф, файлы хода, todo, Explored) | Codex: плавное изменение высоты и fade 150–200 мс | **Missing**: всё через `IsVisible` (518, 625, 650, 657, 699, 1005) | Минимум — fade-in содержимого (Opacity 0→1, 120 мс); анимация высоты в виртуализированном `TranscriptPanel` дорогая | M (fade) / L (высота) | P3 |
| Появление карточек над композером (диалог, session card, recover, link) | Claude: вопрос выезжает и проявляется | **Missing**: `IsVisible` (914, 961, 989, 1041, 1055) — мгновенный скачок макета | Fade и translateY 8 px, 150 мс, на `Border.dialog`, `.banner`, `.session-card` | M | P2 |
| Brief-карточка как тост: появление и уход | Тосты выезжают и тают | **Missing**: SessionCard появляется и исчезает мгновенно | Fade 150 мс при входе и выходе | S | P2 |
| «Jump to latest»: появление | Claude: кнопка проявляется | **Missing**: `IsVisible` переключается мгновенно (788-791) | Opacity и translateY, 120 мс | S | P2 |
| Плавная прокрутка к последнему сообщению и End/Home | Claude и Codex: smooth scroll к низу | **Missing**: `SetCurrentValue(Offset)` мгновенно (ChatScrollController.cs:124-130) | Анимировать Offset 200 мс по кнопке и End (не при follow во время стрима) | M | P3 |
| Открытие и закрытие панелей, сайдбара, drawer | Codex: панели выезжают за 200 мс; drawer со скримом | **Missing**: колонки грида и `IsVisible` (797-810; scrim 111) | Сначала drawer и скрим (fade 150 мс), колонки потом | M | P3 |
| Hover-заливки кнопок и строк | Web-клиенты Codex и Claude: transition 100–150 мс (на macOS-native hover не принят — поэтому P3) | **Missing**: `BrushTransition` только у switch | `Transitions` на `ContentPresenter` для `Button.icon/flat/session/menu-item` (BrushTransition 100 мс) | S | P3 |
| Проявление действий ряда (row-action, session-action, group-action, md-copy) | Claude: действия плавно проявляются | **Missing**: Opacity 0/1 мгновенно (Styles.Session.axaml:189-194; Styles.Workspace.axaml:70-75, 104-111; 830) | `DoubleTransition` Opacity 100 мс | S | P3 |
| Переход сегмента (segmented) | macOS segmented: «пилюля» скользит | **Missing**: класс `.selected` меняется мгновенно | Мелочь, можно оставить | M | P3 |
| Переход «приветствие → разговор» (композер уходит вниз) | Claude: композер плавно уезжает | **Missing**: `UpdateEmptyLayout` мгновенно | Fade 150 мс у транскрипта (`behind-greeting` уже меняет Opacity — добавить `DoubleTransition`, MainWindow.axaml:442-445) | S | P3 |
| Reduced motion | Claude VSCode 2.1.296, Codex 0.156 | **Missing** (уже в PARITY_ROADMAP.md:187) | Микро-часть: один флаг `Motion.Reduced` (NSWorkspace) гасит `Pulse.Low`/`Spin` и все новые `Transitions` выше — делать вместе с токенами | S | P2 |

## Feedback & states

| Микро-взаимодействие | Ожидается по конвенции | Наше | Что сделать | Effort | Prio |
|---|---|---|---|---|---|
| ✓ на hover-кнопках Copy у сообщений | Claude: Copy → ✓ на 1,5–2 с везде, как у code block | **Partial**: ✓ есть только в code block (MarkdownView.cs:801-818). Copy у пузыря (464-467), у ответа (536-539) и в конце хода (694-697) молчат — `CopyUserMessage` (MainViewModel.Transcript.cs:28-31), `CopyCommand` (RowViewModels.cs:216) | Attached-поведение `ctl:CopiedFeedback` для `Button.row-action`: смена иконки на IconCheck на 1,5 с | S | P1 |
| Обратная связь на Copy из контекстных меню | После действия из меню нужен знак, что оно сработало (тост или бейдж) | **Partial**: brief-карточка есть у Copy path проекта и сессии, Session ID, Session file (MainViewModel.Sessions.cs:661,710; Session.cs:430,438). Молчат: Copy и Copy as MD (меню ответа), Copy (меню пузыря), Copy code и Copy link (ContextMenus.cs:92-95), Copy path и relative в Files (FilesViewModel.cs:864,871), Copy branch (BranchChipViewModel.cs:115-118), Copy ссылки входа (MainViewModel.cs:327-330) | Единый `MainViewModel.CopyText(text, what)` → маленький тост «Скопировано» (или brief-карточка); `ContextMenus.CopyText` перевести на него | S | P1 |
| Brief-карточка не исчезает под курсором | HIG и Material: автоскрытие ставится на паузу при hover или focus, особенно с Undo | **Missing**: `DispatcherTimer` 4 и 8 с идёт без паузы (MainViewModel.Session.cs:117-121; Sessions.cs:649-653) | Stop/Start таймера на `PointerEntered/Exited` и `GotFocus` у `SessionCardHost` | S | P1 |
| Несколько тостов подряд | Тосты встают в стек, а не затирают друг друга | **Partial**: один слот `SessionCard`, `ShowCard` заменяет предыдущую (MainViewModel.Session.cs:93-96) | Очередь brief-сообщений (не ask-карточек) | S | P3 |
| Undo после удаления или скрытия | Codex и Claude: «Removed · Undo» | **Partial**: Undo есть только при скрытии проекта без чатов (Sessions.cs:629-653). Нет: × у чипов, очереди, вложений; удаление правила разрешения | Undo для локальных действий (см. Composer и Settings) | S | P2 |
| Оптимистичный Send и Stop | — | **Have**: `OptimisticAfterVersion` (OpenSession.cs:40-41) | — | — | — |
| Индикатор, пока открывается или стартует сессия | Codex и Claude: спиннер или скелетон, пока разговор грузится | **Partial**: `StatusText = "Starting omp…"` считается (MainViewModel.cs:605), но `ActivityLine` виден только при `IsRunning` (MainWindow.axaml:1047) | Показывать activity-строку и в `Starting`; скелетон 3 строк, пока снапшот не пришёл | S | P2 |
| Скелетоны (сайдбар, модели, списки настроек) | Claude и Codex | **Missing**: везде текст «Loading…» (1542; Files 183; Settings по отчёту scout) | Общий `Border.skeleton` с `Pulse.Low` | S | P3 |
| Плюрализация по правилам языка | В ru три формы (1, 2–4, 5+) | **Partial**: `Loc.Plural` даёт только 2 формы (`n==1 ? one : other`, Loc.cs:37-38); в ru обходят через «ещё строк: {0}» (ru.json:1991) | CLDR-правила в `Loc.Plural` (`one/few/many/other` по культуре) и ключи вида `|few` | S | P2 |
| Числа: разряды | — | **Have**: `{0:N0}` (UsageIndicator и др.) | — | — | — |
| Обрезка текста с тултипом | — | **Have**: tool-row (`TrimmedToolTip`), сессия, вложение, модель. **Missing**: заголовок вкладки терминала (824) | См. Panes | S | P3 |
| Тултипы с шорткатами | Claude: подсказка с клавишей | **Have**: тогглы в шапке, Send, Stop, режим разрешений, Back to app (Esc), Close (F12). **Missing**: Jump to latest (End), Attach (+), Thinking-чип | Добавить клавиши в тексты | S | P3 |
| Задержка тултипов | ~500 мс на первый, мгновенно на соседние | **Have** (дефолты Avalonia: ShowDelay и BetweenShowDelay; переопределений нет) | — | — | — |
| Пустые состояния: терминал, Files, Plan, Tasks, Preview, приветствие | — | **Have**: 861-873; FilesPane.axaml:194-201; приветствие и стартеры (741-772) | — | — | — |

## Keyboard & focus

| Микро-взаимодействие | Ожидается по конвенции | Наше | Что сделать | Effort | Prio |
|---|---|---|---|---|---|
| Фокус после переключения чата | Codex и Claude: после клика по чату курсор стоит в поле ввода | **Missing**: `FocusComposerRequested` в `OpenSessions`/`Sessions` не вызывается (вызовы есть только в Chats.cs:66,94, Transcript.cs:23 и др.), фокус остаётся на кнопке строки в сайдбаре | В конце `ShowOpen`/`OpenSessionAsync` → `FocusComposerRequested` (если открыто кликом или Ctrl+Tab) | S | P1 |
| Фокус после отправки, после диалога, после смены режима | — | **Have**: `RestoreLostFocus` при `Phase` и `HasDialog` (MainWindow.axaml.cs:585-586, 762-767) | — | — | — |
| Фокус после выбора модели, dictation, drop, rename, ⌘/ | — | **Have**: 89-95, 583-584, 988, 835/841, Composer.cs:196 | — | — | — |
| Каскад Esc (меню → просмотрщик → лист шорткатов → диалог → настройки → поиск → стоп) | Claude | **Have**: MainWindow.axaml.cs:1018-1072; MainWindow.Escape.cs | Образцово | — | — |
| ⌘. — остановить (macOS: Cancel) | HIG: ⌘. отменяет операцию | **Missing**: стоп только по Esc (1061-1065) | `KeyBinding ⌘.` → `AbortCommand` | S | P3 |
| Ctrl+Tab между сессиями | — | **Have**: (MainWindow.axaml.cs:67-; KeyboardShortcuts.cs:63-64) | — | — | — |
| ⌘1…⌘9 — n-я сессия в сайдбаре | Codex и Slack: ⌘-цифра переключает | **Missing** (KeyboardShortcuts.cs:55-99) | Привязки к первым 9 строкам | S | P3 |
| PageUp/Down, Home/End в разговоре | Claude | **Have**: ChatScrollController.cs:186-219 | — | — | — |
| Фокус-кольцо только с клавиатуры | HIG | **Have**: Styles.axaml:76-91 | — | — | — |
| Невидимые кнопки ряда как Tab-стопы | Tab не должен застревать на невидимом | **Partial**: pin и ⋯ становятся видимыми на `:focus-visible` (Styles.Workspace.axaml:108), но это лишние стопы | `IsTabStop=False` для session-action (действия есть в КМ, его открывает Shift+F10) | S | P3 |
| Шорткаты на кнопках диалога (AccessText) | DESIGN_SYSTEM.md:324 | **Have**: _Allow, _Deny, _Yes, _No (1089-1170) | — | — | — |

## Clipboard & DnD

| Микро-взаимодействие | Ожидается по конвенции | Наше | Что сделать | Effort | Prio |
|---|---|---|---|---|---|
| Copy ответа: plain и Markdown | Claude | **Have**: два пункта (RowViewModels.cs:216-220) | — | — | — |
| Rich-копирование (HTML и RTF) ответа | Claude и ChatGPT кладут в буфер HTML, чтобы вставка в Docs и Notes сохраняла форматирование | **Missing**: только `SetTextAsync` (MainWindow.axaml.cs:750-753) | `DataObject` с `text/plain` и `text/html` (Markdig → HTML) | S | P3 |
| Copy image из меню миниатюры | Finder и Quick Look: КМ «Copy» | **Missing**: `ImageMenu` содержит только «Open» (91-102); копирование есть лишь в просмотрщике (ImageViewer.axaml.cs:48-52) | Пункты Copy image, Open in default app, Save as… в `ImageMenu` | S | P2 |
| Ссылка: URL виден до клика | Браузеры: URL в статус-баре или тултипе | **Missing**: у `InlineLink` тултипа нет (MarkdownView.cs:740-750) | `ToolTip.Tip = uri.AbsoluteUri` | S | P2 |
| Ссылка: «Open in Preview» | Встроенный браузер есть — ссылка должна в него открываться | **Missing**: `ForLink` = Open link и Copy link (ContextMenus.cs:35-37) | Третий пункт «Open in Preview» для http(s) | S | P2 |
| Вставка файлов, картинок, длинного текста | Claude | **Have**: `PasteAsync` (MainWindow.axaml.cs:940-963) | — | — | — |
| Drop файлов на окно → композер, с подсветкой | Claude и Codex | **Have**: 78-81, 975-989; Styles.Composer.axaml:19-22 | Текст-подсказка (см. Composer) | S | P3 |
| Drag-out (файлы из Files, картинки из сообщений) | Finder, VS Code | **Missing**: `DoDragDrop` не используется | См. Panes | M | P3 |
| Copy без форматирования кода (без ```) | Claude | **Have**: копируется `_code` (MarkdownView.cs:814-815) | — | — | — |
| Copy сообщения пользователя вместе с вложениями и вставками | Claude: копирует ровно отправленный текст | **Partial**: копируется `row.Text` (MainViewModel.Transcript.cs:30); куда попадает свёрнутый Pasted text — **Unclear** | Проверить и документировать | S | P3 |

---

## Top 20 quick wins (effort S, максимальная отдача)

1. **Сплиттеры панелей: зона захвата 7 px, подсветка на hover, двойной клик сбрасывает ширину** — повторить `SidebarGrip` (MainWindow.axaml:797,804,809,879; Styles.axaml:723-731). Сейчас 1 px: попасть трудно, панель кажется «не растягивается».
2. **Type-to-focus**: печать при фокусе в разговоре или сайдбаре уходит в композер (MainWindow.axaml.cs:37; ChatScrollController.cs:59).
3. **Фокус в композер после переключения чата** (кликом или Ctrl+Tab) — `FocusComposerRequested` в `MainViewModel.OpenSessions`.
4. **✓ на всех hover-кнопках Copy** (пузырь, ответ, конец хода) — как у code block (MarkdownView.cs:812-818).
5. **Единый «Скопировано» для всех Copy из меню** (ContextMenus.cs:92-95, FilesViewModel.cs:864-871, BranchChipViewModel.cs:115-118, CopyPendingUrl) через `ShowBrief` или тост.
6. **Пауза автоскрытия brief/Undo-карточки при hover и focus** (MainViewModel.Session.cs:117-121; Sessions.cs:649-653).
7. **КМ у строки инструмента**: Copy command, Copy output, Open in Files, Copy path (MainWindow.axaml:562-678).
8. **Сохранять ширины панелей** между запусками (MainWindow.axaml.cs:167-176 → `OmpRuntimeOptions`).
9. **Сохранять видимость сайдбара и открытые панели**.
10. **Тултип с URL у Markdown-ссылок и пункт «Open in Preview»** (MarkdownView.cs:740-750; ContextMenus.cs:35-37).
11. **`ImageMenu`: Copy image, Open in default app, Save as…** (MainWindow.axaml:91-102).
12. **Event log: выделяемые строки и КМ Copy line, Copy all, Clear** (MainWindow.axaml:892-899).
13. **Hover и средний клик у вкладок терминала и Preview, КМ вкладки, обрезка длинного заголовка** (MainWindow.axaml:819-834; PreviewPanel.axaml:189-196).
14. **Шорткаты Preview**: ⌘L, ⌘R, ⌘[ / ⌘], ⌘W (PreviewPanel.axaml:86).
15. **КМ у сообщения в очереди** (Edit, Send now, Remove) и **у чипа Pasted text** (Preview, Remove) — паритет с `AttachmentMenu` (1257-1301).
16. **Токены движения и переход поворота шевронов** — одно правило на все `Icon.chevron` (Styles.axaml:625-655; Styles.Session.axaml:208,319), сразу под будущий флаг reduced motion.
17. **Fade для Jump to latest, brief-карточки и действий ряда** (DoubleTransition 100–150 мс; MainWindow.axaml:788; Styles.Session.axaml:189-194; Styles.Workspace.axaml:104-111).
18. **Плюрализация по CLDR в `Loc.Plural`** (Loc.cs:37-38) — убирает обходы вроде «ещё строк: {0}» в ru.json.
19. **Бейдж в Dock с числом ждущих чатов и `requestUserAttention`** на macOS (Notifications.cs:58 vs 61-78).
20. **Activity-строка видна и в фазе `Starting`** (MainWindow.axaml:1047; MainViewModel.cs:605) — пользователь видит, что сессия открывается.

Следующие по ценности (M): восстановление размера и позиции окна (P1), общее `ContextMenus.ForPath` для путей в разговоре (P1), стрелки ↑/↓ в сайдбаре, история промптов на ↑, черновики на диске, анимация появления карточек над композером.
