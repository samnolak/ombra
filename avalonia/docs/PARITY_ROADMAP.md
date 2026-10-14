# Роадмап: паритет с Codex и Claude Code, их исправленные баги, оптимизации из других GUI

Снимок от 2026-10-13. Источники:
- [Codex / ChatGPT changelog](https://learn.chatgpt.com/docs/changelog): все записи с мая 2025 по CLI 0.162.1 (2026-10-09), desktop-приложение 26.2xx–26.9xx.
- [Claude Code changelog](https://code.claude.com/docs/en/changelog): `CHANGELOG.md` с 0.2.21 по 2.1.296 (2026-10-09), включая записи `[VSCode]` и Desktop.
- Другие GUI для pi: pi-gui, piggy, pi-desktop, pi-heao-gui, PIDesktop, PiPilot, Orbit.
- Другие GUI для omp: nornzach/oh-my-pi-gui (SkippyzNJiff отдаёт 404), OMPiUI, Ompcot, unkeyn, dylansantwani, GiorgioTassoni, iris-cat-dev. Плюс upstream `can1357/oh-my-pi` с 18.2.2 по 18.10.3.

Подробные таблицы по каждому источнику с доказательствами (файл + символ) лежат в [`parity-research/`](parity-research/).
Микро-UX (меню по правому клику, hover, анимации, обратная связь, фокус, запоминание состояния) — отдельный §6 и
отчёты `parity-research/micro-*.md`, ~370 пунктов.
Ниже — сводка без повторов, по направлениям и приоритетам:
- **P0** — безопасность или потеря данных;
- **P1** — стабильность, скорость, заметный разрыв в функциях;
- **P2** — паритет;
- **P3** — шлифовка.

Состояние кода: каждый пункт проверен grep/read. Ничего не запускалось. Пункты со звёздочкой (★) я перечитал в коде отдельно.

## Главное

1. ★ **Закреплённый omp 18.8.0 уже отдаёт по RPC больше, чем мы используем.** В `rpc-mode.ts` пина есть 23 команды, которых клиент не вызывает (список в §0). Из-за этого PARITY.md отправляет в терминал `/fork`, `/tree`, `/btw`, `/goal`, а ROADMAP ждёт апгрейда omp ради `set_event_filter`. Это самые дешёвые победы: обновлять omp не нужно.
2. ★ **Стриминг квадратичный.** omp 18.8.0 по умолчанию кладёт в каждый `message_update` весь накопленный ответ дважды. Клиент читает только `delta`, но `set_event_filter {messageUpdates:"delta"}` не шлёт. Исправление — одна команда после handshake.
3. ★ **История сессии грузится целиком** одним `get_messages` (`SessionController.cs:151`). `get_messages_page` не используется, хотя PARITY.md §4 пишет «Done». Сессия больше 64 MiB, например с base64-картинками, не откроется вообще.
4. **Тексты пользователя теряются:**
   - ★ очередь при крахе или рестарте omp (`ConversationState.ResetTranscript` → `_queued.Clear()`; `DropUndeliveredQueue` только пишет notice);
   - набранный ответ на вопрос, который omp отозвал;
   - черновики при выходе из приложения или его крахе (они живут только в памяти).
5. **Безопасность (P0):**
   - ★ невидимые и bidi-символы не нейтрализуются в карточке approval и в `ApprovalRule.IsPlainCommand` (`$HOME`, `~`, `\`, U+202E проходят);
   - ★ на Windows путь `\\host\share` в тексте агента резолвится и читается по наведению (`ProjectFiles.Resolve` → `File.Exists`), то есть утекает NTLM;
   - git clean/smudge-фильтры недоверенного репозитория не отключены;
   - macOS self-signed подпись без hardened runtime;
   - approval принимается цифрой без задержки после появления карточки.
6. ★ **Slash-команда, набранная во время хода, уходит модели как текст.** Во время Running `SubmitAsync` всегда делает `follow_up` (`MainViewModel.cs:241-246`). omp разбирает builtin-команды только в `prompt`; это слова агента, не перепроверено запуском.
7. **Windows-сироты:** без Job Object после краха GUI дерево omp остаётся жить (`OwnedProcesses.cs:20`).
8. **Самые заметные разрывы в функциях:**
   - Find в чате (⌘F);
   - полнотекстовый поиск по сессиям;
   - чекпоинты кода при Rewind;
   - Stop и Steer для субагентов;
   - `/btw`, Fork, Goal в GUI (RPC уже есть);
   - история промптов;
   - Git-панель (commit/push/PR);
   - архив сессий;
   - палитра команд ⌘K;
   - Mermaid и LaTeX.
9. Наш стек стрима и рендера (коалесинг ≤30 Гц, инкрементальный Markdown, виртуализация с запомненными высотами, backpressure, reaper сирот на macOS/Linux) уже лучше, чем у всех семи pi-клиентов и шести omp-клиентов. Слабые места — I/O вокруг сессий и жизненный цикл процессов на Windows.

---

## §0. Быстрые победы: RPC закреплённого omp 18.8.0, которые мы не вызываем

★ Проверено по `…/Ombra/runtimes/omp-18.8.0-bun-1.4.2/…/modes/rpc/rpc-mode.ts` и grep по `avalonia/src`.

| RPC (в пине) | Что даёт | Что сделать | P |
|---|---|---|---|
| `set_event_filter {messageUpdates:"delta"}` | линейный, а не O(n²) трафик и разбор JSON | слать после `negotiate_protocol`; `message_end` остаётся полным | P1 |
| `get_messages_page` | история страницами | хвост сразу, старое при прокрутке вверх; fallback на `get_messages` при `session_busy`/`stale_cursor` | P1 |
| `abort_and_restore_queue` | Stop возвращает очередь в composer | Stop при непустой очереди → эта команда, тексты и картинки в поле ввода | P1 |
| `promote_queued_message` | «Send now» для сообщения из очереди | кнопка на чипе очереди | P2 |
| `cancel_subagent`, `steer_subagent` | остановить или направить одного субагента | Stop / Message в Background tasks | P1 |
| `btw`, `btw_cancel`, `get_btw_history` (+ `btw_delta`/`btw_record`) | вопрос «в сторону» во время хода | выезжающая панель над composer | P2 |
| `fork {entryId}` | нативный fork | «Fork from here» на сообщении и в меню сессии | P2 |
| `get_tree` | дерево веток | диалог дерева (только итеративные обходы, см. B-класс у nornzach) | P3 |
| `goal` | goal mode без терминала | полоса цели над composer: objective, статус, elapsed, Pause/Resume/Edit | P2 |
| `abort_retry` | отмена авто-повтора | одна живая строка «Retrying in 0:42 · attempt 3/10 · Cancel» | P2 |
| `set_ask_dialog` | все вопросы `ask` одной формой, multi-select | форма со вкладками вместо цепочки карточек | P2 |
| `queue_update` (событие) + `get_state.queuedMessages` | очередь, которую ведёт omp | рисовать чипы по `queue_update`; локальный `Enqueue` — только оптимистичное эхо | P2 |
| `messageId` в `message_*` | дельта точно попадает в своё сообщение | словарь `messageId → row`; эвристики `_streamingAssistant` оставить как fallback | P2 |
| `set_todos` | правка плана | редактирование пунктов в Plan pane | P3 |
| `set_cache_warming` (+ `cache_warming_*`) | прогрев prompt cache | переключатель и индикатор «cache warm 3m» | P3 |
| `set_slow_mode`, `get_state.usageLimit` | /slow; стадия лимита без запуска CLI | тумблер; бейдж лимита из уже открытого соединения вместо `omp usage --json` | P3 |
| `set_steering_mode`, `set_follow_up_mode`, `set_interrupt_mode` | поведение очереди | Settings › General «Enter во время работы: в очередь / steer» | P3 |
| `predict_word`(+`_feedback`) | ghost-text в composer | за настройкой | P3 |
| `open_session` с `provider`/`modelId` | открыть сессию на другой модели | восстановление после «Could not restore model» (см. B-класс 9) | P1 |
| `get_entries`, `live_*`, `set_host_tools` | entries, голос, host-tools | `set_host_tools` → `terminal_read` (агент читает вкладку терминала, как Codex 26.311) | P2/P3 |

Документация: в PARITY.md §3–§5 исправить `get_messages_page` (Done → не используется), `/fork` («no RPC» → есть), `/tree`, `/btw`, `/goal`. В ROADMAP backlog убрать «controlled omp upgrade … `set_event_filter`». В PROJECT_STATE поправить «One omp process at a time»: в коде уже несколько.

---

## 1. Паритет по функциям

Для каждой функции: что делают они → что у нас → как сделать лучше. Источники и доказательства — в `parity-research/*.md`.

### 1.1 Транскрипт и чтение

| Функция | У них | У нас | Как сделать лучше | P |
|---|---|---|---|---|
| **Find в чате** ⌘F / ⌘G | Codex app 26.318/26.423, CLI 0.156–0.162; Claude `/` в transcript | Missing; ⌘⇧F занят Files | Искать по модели строк (`ConversationState` rows: текст, аргументы и вывод инструментов), а не по визуальному дереву — `TranscriptPanel` виртуализирован (у Codex был баг 26.423 именно с этим). Счётчик 3/17, Enter/⇧Enter, раскрывать свёрнутую группу только на время показа, якорный скролл через `ChatScrollController`. Префилл выделением. То же в CodeView и diff | P1 |
| **Выделение через блоки, rich copy** | Codex 0.156–0.162 (copy-on-select, таблицы, HTML+plain) | Partial: каждый абзац — отдельный `SelectableTextBlock` | Модель текста хода + выделение на уровне `TranscriptPanel`. В clipboard класть text/plain и text/html. Делать вместе с Find — у них общая основа | P2 |
| **Ссылки кликабельны везде** | Codex 0.162 (approval, вопросы, warnings, баннеры) | Только в ответах; Title/Message/Note карточек и notices — plain | Общий `LinkifiedText` (URL + `PathLinks`) | P2 |
| **Реальный адрес ссылки виден** | Claude 2.1.295–296 | Tooltip с URL нет | Tooltip/статус с полным href, IDN — в punycode, предупреждение «текст ≠ адрес» | P2 |
| **Mermaid, LaTeX** | Codex app/iOS, CLI 0.156–0.159 | Missing | Markdig `UseMathematics` + CSharpMath; Mermaid — рендер в offscreen WebView (движок preview уже есть), при ошибке — исходник с причиной | P2 |
| **Время сообщений** и модель у ответа | Claude VSCode 2.1.284/2.1.290; CLI 1.0.112 | Missing | время и модель по hover, разделитель дат; брать из `timestamp` и `message_end` | P3 |
| **Focus view** (все инструменты хода в одну строку) | Claude VSCode 2.1.221/2.1.269 | Partial: Explored-группы | Переключатель во View; ответ, todo и вопрос всегда видимы; живой индикатор текущего инструмента | P3 |
| **Живой elapsed на инструменте, «стрим молчит N с»** | Claude 2.1.210/2.1.214/2.1.185 | Elapsed только у хода | `ToolItem.StartedAt` в тот же 1-секундный тик; watchdog «omp молчит 2m · Force stop / Restart» (не во время `tool_execution_*`) | P2 |
| **Огромные таблицы** | Claude 2.1.208 (200 строк + «… N more») | `TableControl` строит все строки | Лимит 200 + раскрытие | P2 |
| **Полный вывод инструмента** | Claude ctrl+o | Режется до 4000 символов, целиком не открыть | «Open full output» в вкладке или редакторе | P3 |
| **Навигация по промптам** ⌥↑/⌥↓, outline ходов | Claude 2.1.139, piggy | Missing (sticky header есть) | Шорткаты + rail ходов в sticky header | P3 |
| **Закладки ответов** | Claude VSCode 2.1.286 | Missing | Клиентский список по id сообщения | P3 |
| **Выделение → Quote / Ask** | Codex app 26.410/26.707 | Только Copy | Поповер «Quote in message» / «Comment» → чип, как `CodeCommentViewModel` | P2 |

### 1.2 Composer

| Функция | У них | У нас | Как сделать лучше | P |
|---|---|---|---|---|
| **История промптов** ↑/↓, Ctrl+R | Claude с 0.2.x, фиксы до 2.1.292 | Missing (↑ только забирает сообщение из очереди) | Сначала история проекта (свои + первые user-сообщения из файлов сессий); ↑ только на первой строке; без подряд идущих дублей; отменённые промпты тоже сохраняются | P2 |
| **Черновики переживают выход и краш** | Codex 26.506/26.908; Claude 2.1.208; pi-gui | Только в памяти (`OpenSession`) | Запись с debounce 500 мс → `drafts.json` по файлу сессии (текст, чипы, пути вложений, комментарии); flush перед выходом; в карточке выхода — «черновики в N чатах» | P1 |
| **Send now / interrupt-and-send** | Claude 2.1.275 | Missing | `promote_queued_message` / `abort_and_prompt` | P2 |
| **Stop возвращает очередь** | Claude 2.1.239; omp TUI Esc | Missing | `abort_and_restore_queue` | P1 |
| **Ответ на вопрос не теряется** | Codex 0.157/0.162 | Missing (`DialogViewModel._inputText` пропадает при `cancel`) | Вернуть в composer как `> вопрос\nответ`; текст на `/` и `!` экранировать | P1 |
| **`/` и skills в любом месте текста**; skills/MCP в `@`-меню | Codex 26.317/26.318, Claude 2.1.0 | Только когда текст начинается с `/`; в `@` только файлы | Триггер после пробела; секции Skills и Connectors в `@`-меню; подсветка совпадений | P3 |
| **Вложения** | Codex: любые файлы; Claude: 17–20 картинок, HEIC/BMP/TIFF | 3 картинки × 230 KB; прочие файлы — путём | Чип файла; картинку сохранять в артефакты сессии и передавать путём, тогда лимит снимается; HEIC/BMP/TIFF → JPEG. Upstream: входящий `rpc_chunk` | P2 |
| **Модель/thinking по умолчанию** vs «только эта сессия» | Claude 2.1.153; Codex 0.162 | Missing; выбор модели вне Ready молча игнорируется | «Use for new chats»; `PendingModel` применяется на Ready до первого prompt | P2 |
| **Send до готовности omp** | Claude 2.1.10 | Send выключен до Ready | Ставить в очередь и отправить на Ready | P3 |
| **Внешний редактор, ghost-text, spellcheck** | Claude | Missing | P3 | P3 |

### 1.3 Сессии и навигация

| Функция | У них | У нас | Как сделать лучше | P |
|---|---|---|---|---|
| **Полнотекстовый поиск по сессиям** (содержимое, ветка, URL PR) | Codex 26.323/26.527; GiorgioTassoni (FTS5); nornzach | Только title и cwd | Индекс поверх кэша каталога (§3 O2), инкрементально по mtime; сниппет; открытие сразу на совпадении | P1 |
| **Палитра команд** ⌘K / ⌘P файлы | Codex 26.208–26.609 | Missing | Все действия, панели, страницы Settings, чаты (Unread сверху), файлы, темы. Решить конфликт шорткатов с Codex (⌘⇧P/T/F/B) до релиза | P1 |
| **Архив** (+ автоархив, Unarchive all) | Codex 26.323; Claude VSCode 2.1.252/2.1.265 | Только Delete | Клиентский флаг рядом с pins, группа «Archived». Убирает риск: удаление сейчас — единственный способ почистить список | P2 |
| **Фильтры по статусу** (Working / Needs input / Unread / Errors) | Claude VSCode 2.1.259; Codex Activity 26.727 | Статусы есть, фильтров нет | Чипы фильтра; персистентный unread | P2 |
| **Список: «Show older», ошибка вместо пустоты** | Codex 0.162 #51230; Claude 2.1.294 | Жёстко 500 файлов; IO-ошибка → `[]` → сайдбар пустеет | Мержить с прошлым списком, показывать ошибку, подгружать старше | P1 |
| **Сверка с диском при фокусе окна** | pi-gui 9c4ce78; Orbit | Missing (сессии из CLI не видны) | Reconcile по mtime/size при фокусе; хвост файла для idle-сессии | P2 |
| **«Открыта в другом месте»** | Claude VSCode 2.1.252 | Missing | См. B-класс 7 (lease) | P1 |
| **Переходы** ⌘1–9, ⌘[ ⌘], «следующий ждущий» ⌘⌥A | Codex commands | Только Ctrl+Tab | Стек навигации + шорткаты | P2 |
| **Сворачиваемые группы, двойной клик = rename, пользовательские группы** | Codex 26.417/26.203; Claude 2.1.231 | Missing | P3 | P3 |
| **Импорт из Claude Code / Codex** | Codex 26.608, 2026-08-11 | Только через терминал omp | «Import…» в сайдбаре и в онбординге → omp `--from-claude/--from-codex` | P2 |
| **Remote (SSH-хост)** | Codex 26.415 → GA | Missing | Сессия = `ssh host omp --mode rpc-ui` (stdio не меняется); бейдж хоста; Files через ssh | P2 |
| **Несколько окон / pop-out / split view** | Codex 26.212/26.415; nornzach split | Missing | Второе окно биндится к тому же `OpenSession` (omp у чата уже свой) | P3 |

### 1.4 Rewind, fork, checkpoints

| Функция | У них | У нас | Как сделать лучше | P |
|---|---|---|---|---|
| **Чекпоинты кода по ходам** (rewind: разговор / код / оба) | Claude 2.0.0+; pi-gui checkpoint-store; PIDesktop | Rewind откатывает только разговор | Отдельный bare-репозиторий клиента с hidden refs (не трогает пользовательский `.git`), снимок до каждого хода с бюджетом 2 с и лимитами 10k файлов / 64 MB; в Rewind выбор «разговор / код / оба» с diff-превью | P1 |
| **Fork from here** | Codex 26.212/26.317; Claude VSCode | Rewind есть, fork — в терминале | RPC `fork`; опция «in new worktree» | P2 |
| **Rewind с картинками** | Claude 2.1.119 | Баг: пустая метка у сообщения только с картинкой, картинки в поле не возвращаются | Метка «[изображение ×N]» + миниатюра, вернуть `UserItem.Images` | P2 |
| **Summarize up to here** | Claude 2.1.32/2.1.141 | Missing | Upstream (частичная компакция) | P3 |

### 1.5 Фоновая работа, режимы

| Функция | У них | У нас | Как сделать лучше | P |
|---|---|---|---|---|
| **Agent map: Stop / Message у каждого субагента, Stop all, failed после краха, живая лента** | Claude VSCode 2.1.269–2.1.290; nornzach Agent Hub | Список, метрики, транскрипт по запросу; Stop нет | `cancel_subagent`/`steer_subagent`; живой хвост через `get_subagent_messages fromByte`; сортировка по состоянию; модель субагента | P1 |
| **Jobs: Stop, полный вывод, «ждёт ввода»** | Claude 2.1.84/2.1.276 | Только список | Хвост вывода; эвристика «вывода нет N с, последняя строка похожа на prompt». Upstream: RPC отмены job | P2 |
| **Статус «работает в фоне»** | Claude 2.1.269/2.1.288 | «finished» приходит, хотя субагенты ещё работают | Отложенное уведомление, статус «Working (background)» | P2 |
| **Goal mode** | Codex GA 26.519; Claude 2.1.139 | Notice по `goal_updated`; управление — в терминале | RPC `goal` + полоса цели; между итерациями цели не уведомлять «finished» | P2 |
| **Plan mode** + карточка одобрения плана | Codex 26.210/26.317; Claude 2.1.0+ | В терминале | Upstream: RPC plan mode — в 18.8.0 его нет | P2 (upstream) |
| **Automations / scheduled tasks** | Codex app (Automations, thread automations 26.415) | Missing | Локальный планировщик: новый чат или follow-up в сессию; режим одобрений фиксируется в задаче; работает, пока приложение в tray | P2 |
| **Auto-review** (вердикт и риск на approval) | Codex 2026-04-23 | Missing | Upstream; GUI-карточка, когда omp отдаст событие | P3 |

### 1.6 Review и Git

| Функция | У них | У нас | Как сделать лучше | P |
|---|---|---|---|---|
| **Review-лента всех diff** + скоупы (uncommitted / staged / branch / last turn), ignore whitespace, «просмотрено» | Codex review pane; pi-gui #178; PIDesktop | Только Files › по одному файлу, сравнение с HEAD, лимит 3000 строк / 4 MB | Лента, виртуализированная как транскрипт; скоуп «last turn» — по чекпоинтам 1.4; «Load more» вместо жёсткого среза | P1 |
| **Commit / push / PR** со сгенерированным сообщением, поиск и переключение веток | Codex app | Missing | Commit-панель в Review (stage/unstage по hunk); сообщение — отдельным запросом к модели; PR через `gh`; поиск веток в `BranchChip` | P1 |
| **PR-бейдж** у сессии и ветки | Codex 26.227; Claude 2.1.217 | Missing | `gh pr view --json` по смене ветки и концу хода; URL PR — в индекс поиска | P2 |
| **Revert hunk прямо в карточке инструмента** | Claude VSCode 2.1.275 | Только в Files pane | Кнопка в diff-карточке транскрипта | P3 |
| **Handoff local ↔ worktree, «New chat in new worktree»** | Codex 26.203/26.303 | `/wt` в одну сторону | Обратный handoff, пункт в «+» | P2 |

### 1.7 Файлы, терминал, браузер

| Функция | У них | У нас | Как сделать лучше | P |
|---|---|---|---|---|
| **Вкладки файлов и редактирование** | Codex 26.409/26.707 | Один viewer, только чтение | Редактируемый CodeView: перед записью проверка mtime/хеша (omp пишет параллельно) | P2 |
| **PDF и rich-превью** | Codex 26.204/26.410 | «Binary file» | PDF через уже встроенный WebView | P3 |
| **Терминалы по сессии, панель снизу** | Codex 26.410/26.601 | Вкладки глобальные, панель только справа | Привязать вкладки к чату; выбор места панели | P2 |
| **Агент читает терминал** | Codex 26.311 | Missing | `set_host_tools` → `terminal_read` | P2 |
| **Браузер: согласие на действия по сайту** | Claude 2.1.288/2.1.296 | Нет; cookies preview живут между запусками | Карточка «omp хочет действовать на example.com: once / session / always» для click/type/eval не на localhost; чтение и скриншоты без вопроса | P1 |
| **Omnibox** (история + поиск), DevTools, reopen tab | Codex/ChatGPT 26.609/26.727 | Missing | `isInspectable` / `AreDevToolsEnabled` по кнопке; история; ⌘⇧T reopen (сейчас занят Background tasks) | P2 |
| **Allow/block list сайтов для агента** | Codex 2026-04-23 | Только network privacy для процесса omp | Проверка в bridge перед `open` | P2 |

### 1.8 Desktop-интеграция и доступность

| Функция | У них | У нас | Как сделать лучше | P |
|---|---|---|---|---|
| **Single-instance + deep links** `ombra://` | Codex 26.305; Claude 2.1.83 | Нет ни того, ни другого: второй экземпляр запускает второй набор omp на тех же файлах | Mutex или socket, пересылка аргументов; `open?cwd=&prompt=&session=` — только префилл, без автосенда | P1 |
| **Tray-резидент, статус и лимиты в tray, бейдж Dock/taskbar** | Codex 26.401/26.417; Claude 2.1.295 | Tray: Show / New / Quit; бейджа нет | Опция «оставаться в tray» (она же нужна automations), счётчик ждущих, overlay icon на Windows | P2 |
| **Windows toast** с переходом в чат | Codex | Только мигание кнопки панели задач | Toast с AUMID | P2 |
| **Quick chat по глобальному хоткею, Appshots** | ChatGPT 26.908; Codex 26.519 | Pet с мини-полем, без хоткея | `RegisterEventHotKey` / `RegisterHotKey`; Appshot = скриншот фронтального окна + AX/UIA-текст | P2 |
| **Масштаб UI** ⌘+/⌘−/⌘0 | Codex 26.227/26.324 | Missing | Зум корневого контейнера; проверить попапы и airspace WebView | P2 |
| **Reduced motion, live regions для скринридера, high contrast** | Claude 2.1.161–2.1.296; Codex 0.156 | Missing (Name расставлены) | Системный флаг → `Pulse`, питомцы, переходы; `LiveSetting` для «в очереди / ждёт ответа / ход закончен / режим сменён»; токены HC | P2 |
| **Настраиваемые шорткаты, поиск по настройкам, темы и шрифты, Report a problem** | Codex/Claude | Missing | P3 | P3 |
| **Диктовка: выбор микрофона и языка, словарь проекта** | Codex 26.429, 0.161; Claude 2.1.69 | Только устройство по умолчанию, язык авто | Список PortAudio-устройств, язык, словарь из имён файлов | P3 |

### 1.9 Расширения, MCP, инструкции

| Функция | У них | У нас | Как сделать лучше | P |
|---|---|---|---|---|
| **Workspace trust / trust review хуков** | Codex 26.506; Claude 2.1.196–2.1.225 | Missing | Проверить, что omp 18.8 автозагружает из проекта (MCP, extensions, hooks, rules). Если что-то исполняется — карточка доверия при первом открытии папки, до доверия старт с `--no-extensions` и т. п. Upstream ask | P1 |
| **Плагины: ошибки загрузки в строке, форма опций, состав перед установкой** | Claude VSCode 2.1.284/2.1.285 | Install/enable/update есть, остального нет | Строка ошибки с Disable/Uninstall; показывать hooks, tools, MCP плагина до установки | P2 |
| **MCP: Reconnect all, «0 tools» как предупреждение, OAuth в GUI** | Claude 2.1.284/2.1.288; Codex 0.161 | Test и статусы есть; OAuth только через TUI | Reconnect all; upstream RPC для MCP OAuth | P2 |
| **Редактор Instructions** (AGENTS.md, APPEND_SYSTEM) | Claude VSCode 2.1.274 | Missing | Settings › Instructions: найти файлы правил omp, открыть и править | P2 |
| **Safe mode** (без расширений, skills, rules) | Claude 2.1.169 | Только settings file | «Restart omp in safe mode» в Recover-карточке через официальные флаги | P3 |
| **Usage: лимит с обратным отсчётом, «as of», совет при 90%** | Codex 26.609; Claude 2.1.208 | При сбое `_reports = []`, совета нет | Держать последний удачный отчёт; баннер ≥80% со временем сброса | P2 |

---

## 2. Баги, которые чинили они: что с ними у нас

Статусы:
- **Bug** — подтверждено в коде;
- **Risk** — вероятно, нужна проверка;
- **Have** — уже закрыто;
- **N/A**.

### 2.1 Открытые (что делать)

| # | Класс бага | Их фиксы | У нас | Действие | P |
|---|---|---|---|---|---|
| 1 | Невидимые / bidi / zero-width символы подменяют текст в approval и обходят правила | Claude 2.1.211, 2.1.223; ASCII smuggling 2.1.277/2.1.280 | ★ **Bug**: `DialogViewModel` показывает текст как есть; `IsPlainCommand` пропускает U+202E, U+200B, NBSP | Символы категорий Cf/Zl/Zp и управляющие рисовать видимыми метками с предупреждением; при любом таком символе `IsPlainCommand = false`; очищать при вставке и отправке промпта (ZWJ/ZWNJ в контексте письменности сохранять) | P0 |
| 2 | Обход «Don't ask again» синтаксисом shell | Claude 2.1.145–2.1.223, 2.1.98, 2.1.113 | ★ **Bug**: `$VAR`, `~`, `\`, глобы проходят (`git log --output=$HOME/.bashrc` совпадёт с `git log:*`) | Запретить `$`, `\`, `~`, `*?[`, не-ASCII в prefix-правилах; никогда не авто-разрешать `rm -rf`, `git reset --hard`, `git clean -f`; fuzz-тесты на кейсах из их фиксов | P0 |
| 3 | UNC / `\\?\` / `file://host` по наведению → утечка NTLM | Claude 2.1.233, 2.1.234, 2.1.247, 2.1.271 | ★ **Bug** (Windows): `PathLinks` → `ProjectFiles.Resolve` → `File.Exists` + чтение превью | В `Resolve` отклонять UNC, `//host`, `\\?\`, `\??\`, `file://` с хостом; такие ссылки — plain text | P0 |
| 4 | Исполнение кода недоверенного репозитория нашими git-вызовами | Claude 2.1.265 (clean filters) | **Risk**: fsmonitor и hooks выключены, `filter.*` — нет | `GIT_ATTR_SOURCE` = пустое дерево или `-c filter.<d>.clean= …`; плюс `GCM_INTERACTIVE=never`, пустой `GIT_ASKPASS` | P0 |
| 5 | Код внедряется в TCC-привилегированное macOS-приложение | Codex 26.924 (CVE-2026-100754) | **Risk**: self-signed и ad-hoc подпись без `--options runtime` (`tools/package/sign.sh:75,83`); TCC-гранты наследуются между версиями | Hardened runtime во всех ветках подписи; `StartupHookSupport=false`; проверить, что entitlements не разрешают dyld env | P0 |
| 6 | Случайное одобрение лишним нажатием, кликом или фокусом | Claude 2.1.252, 2.1.280–2.1.290 | **Bug**: цифры 1–9 и кнопки новой карточки срабатывают сразу; Alt-акселераторы Allow/Deny активны при фокусе в поле feedback | Задержка 400–500 мс после появления карточки и после активации окна; игнорировать key-repeat; отключать access keys при фокусе в поле | P0 |
| 7 | Два писателя в одной сессии (второй GUI, вкладка omp TUI, CLI) | Claude 2.1.246–2.1.259, VSCode 2.1.284–2.1.287; pi-gui lease | **Bug**: нет single-instance и lock | Single-instance; `.jsonl.lease` (heartbeat 1 мин, TTL 5 мин, проверка pid); перед открытием — «открыта в другом месте: read-only / fork»; upstream: нативный lock в omp | P1 |
| 8 | Потеря очереди при крахе / рестарте / прерывании | Codex 0.160 #49105, 0.162 #50087 | ★ **Bug**: `ResetTranscript` → `_queued.Clear()`; `DropUndeliveredQueue` → только notice, картинки теряются | Возвращать тексты и картинки в composer; Stop → `abort_and_restore_queue`; тест «крах при непустой очереди» | P0 |
| 9 | Модель восстанавливаемой сессии недоступна → не открывается | nornzach 0.9.16; omp 18.6.3 «Could not restore model» | **Bug**: общая ошибка «omp didn't start» | Распознать ошибку → карточка с именем модели и «Открыть на текущей модели» (`open_session` с `provider`/`modelId` или `--model`) | P1 |
| 10 | Resumed-сессия меняет модель | Codex iOS 1.2026.202 | ★ **Bug**: на `--resume` передаётся `--model` из файла настроек (`OmpRuntimeOptions.cs:179`); `--thinking` намеренно нет | Не передавать `--model` при resume | P2 |
| 11 | Slash-команда во время хода уходит модели текстом | Claude 2.1.83, VSCode 2.1.275 | ★ **Bug** (код) + omp `followUp` builtin-команды не разбирает (по словам агента) | RPC-builtin во время хода держать в клиентской очереди до конца хода, затем отправить как `prompt`; бейдж «запустится после хода» | P1 |
| 12 | Набранный ответ теряется, когда вопрос отозван | Codex 0.157 #47422, 0.162 #50052 | **Bug** | См. 1.2 | P1 |
| 13 | Черновики теряются при краше и выходе | Codex 26.506/26.908; Claude 2.1.208 | **Bug** | См. 1.2 | P1 |
| 14 | Большие сессии: медленно / пусто / не открываются | Claude 2.1.116–2.1.290; Codex 0.158–0.162; iOS | ★ **Bug**: монолитный `get_messages`; timeout 30 с при ответе до 64 MiB | `get_messages_page` + индикатор загрузки; отдельный timeout для длинных команд (piggy: 120 с) | P1 |
| 15 | Транзиентная ошибка ФС стирает список сессий | Claude 2.1.294; Codex 0.162 #51230 | **Bug**: `ScanAsync` → `[]`, вызывающий код безусловно заменяет `_catalog` | Частичный или неудачный скан мержить с прошлым; показывать ошибку; retry с backoff | P1 |
| 16 | Сироты процессов после краха GUI (Windows) | Claude VSCode 2.1.153, 2.1.296; Codex 0.161; unkeyn, pi-desktop, piggy | **Bug**: Windows — `OwnedProcesses` пуст | Job Object `KILL_ON_JOB_CLOSE` для omp и терминалов; Linux — `PR_SET_PDEATHSIG`; сверять start time в `OwnedProcesses` | P1 |
| 17 | Короткий PATH при запуске из Finder (MCP `npx`/`uvx`, LSP не находятся) | dylansantwani, PiPilot 0.6.0 | **Bug** (macOS/Linux) | Один раз при старте `$SHELL -ilc 'env -0'` с timeout 3 с, слить PATH в окружение omp | P1 |
| 18 | Частичный ответ пропадает при error/abort | Claude 2.1.179/2.1.199 | **Risk**: `OnMessageEnd` заменяет стримленный текст финальным | Финал короче стримленного при error/aborted → оставить стримленный с пометкой «прервано» | P2 |
| 19 | Retry оставляет полусообщение или пустую карточку; каждый retry — постоянный warning | Claude VSCode 2.1.260, 2.1.181 | **Risk** | Одна живая строка retry; тест «обрыв стрима → retry» без пустых и дублирующих строк | P2 |
| 20 | Переоткрытая сессия выглядит иначе, чем живая | Claude VSCode 2.1.283–2.1.285 | **Bug**: `Hydrate` знает только user/assistant/bashExecution/toolResult; approve/deny/answered, компакция, custom-сообщения пропадают | Гидрировать остальные роли; upstream — стабильные id и timestamp у всех ролей | P2 |
| 21 | Сон / пробуждение: стрим «висит», часы прыгают | Claude 2.1.126–2.1.290 | **Risk**: подписки на power-события нет; длительности считаются по стенным часам | На wake — `get_state` и settle-probe для всех чатов; `Stopwatch` для длительностей; watchdog «нет событий N с» | P2 |
| 22 | Устаревший список аудиоустройств → запись тишины | Claude 2.1.195 | **Bug**: `PortAudio.Initialize()` один раз на процесс | Re-init перед записью, если поток не открыт; детект тишины по уровню | P2 |
| 23 | Esc во время IME-композиции отменяет ход; CRLF при вставке | Claude VSCode 2.1.178, 2.1.119 | **Risk** | Не прерывать ход при preedit; CRLF → LF при вставке и отправке; ручной тест с японским IME и Pinyin | P2 |
| 24 | Сообщение больше лимита строки RPC теряет вложения | Claude 2.1.136/2.1.205 | **Bug**: `TakeComposer` очищает поле до проверки размера | Проверять размер в UTF-8 до очистки; предлагать «отправить как файл» | P2 |
| 25 | Секреты в UI и в share | Claude 2.1.234–2.1.296; Codex 2026-08-20 | **Bug**: `SecretRedactor` используется только в diagnostics; regex без `matchTimeout`; пароли с `@`/`%40` пропускаются | Прогонять Detail коннекторов, marketplace source, stdio-аргументы и `/share` через redactor; `NonBacktracking` / timeout; тест-кейсы из Claude | P2 |
| 26 | ANSI escape в status / widget / notice / выводе команд | Codex 0.161/0.162; Claude VSCode 2.1.172 | **Risk** | Общий `Ansi.Strip` или SGR → цвета | P2 |
| 27 | macOS TCC-запрос «данные других приложений» при обходе `$HOME` | Claude 2.1.295 | **Bug**: без git `IndexAsync` обходит `~` целиком | При root = `$HOME` и выше — только первый уровень; исключить `Library`, `*.photoslibrary`, `Containers` | P2 |
| 28 | Файл настроек отвергается целиком из-за одного неверного поля | Claude 2.1.121/2.1.199 | **Bug**: `Load` → `InvalidDataException` | Десериализация по полям с предупреждениями; read-only папка → настройки в памяти | P2 |
| 29 | Конкурентная запись конфигов (между процессами), фиксированные `.tmp` | Claude 2.1.259/2.1.274; Codex 26.602 | **Risk**: фиксированные `.tmp` в `BrowserSiteSettings`, `AgentBrowserDefaults`, `SessionTitles` | Общий `AtomicFile.Write` с уникальным tmp и read-modify-write под file lock | P3 |
| 30 | Обновление на Windows через `powershell.exe` 5.1 (GPO/EDR) | Claude 2.1.212/2.1.221 | **Risk** | Применять обновление самим приложением (`--apply-update`) или через `pwsh`; окно повторов под AV до 60 с | P2 |
| 31 | Мигание консольных окон на Windows | Claude VSCode 2.1.273; Codex 0.159–0.160 | **Bug**: `ConnectionCheck` без `CreateNoWindow` | Общий хелпер для `ProcessStartInfo` | P2 |
| 32 | Суррогатные пары и эмодзи режутся | Codex 2025-05-22; Claude 2.1.132–2.1.218 | **Bug**: `DiffWords.Tokens` (`i++` по UTF-16), `Truncate` / `HeadTail` / `Shorten` | Токенизация по `Rune` / text element; один хелпер безопасной обрезки | P2 |
| 33 | NFC/NFD в fuzzy-поиске (macOS) | Codex 0.156 #45475 | **Bug** | `Normalize(FormC)` для запроса и индекса | P3 |
| 34 | Огромные markdown-таблицы и строка 60 KB в diff | Claude 2.1.208, 2.1.246 | **Risk** | Лимит строк; обрезка строки с маркером | P2 |
| 35 | Worktree, открытый в другом чате, удаляется | Claude 2.1.247 | **Risk** | Учитывать cwd всех открытых чатов | P3 |
| 36 | Папка сессии удалена | Claude 2.1.236/2.1.239 | Partial: только отказ | «Открыть в текущем проекте / только чтение» | P3 |
| 37 | Индикаторы после compaction устарели | Claude 2.1.15–2.1.83 | **Risk** | Принудительный `get_state` на `auto_compaction_end`, `/compact`, `session_settled` | P3 |
| 38 | Стек на раскладке дерева в 10k+ узлов | nornzach 0.9.17 | N/A, пока нет дерева | Только итеративные обходы при реализации `get_tree` | — |

### 2.2 Уже закрыто у нас

Проверено агентами, держать регресс-тестами:
- прыжки скролла и мигание стрима (`ChatScrollController`, инкрементальный Markdown);
- ответ на устаревший или повторный диалог;
- Stop при ожидающем approval;
- U+2028/U+2029 во framing;
- неограниченная память на кадрах (1 MiB / 64 MiB);
- граница вывода инструментов (4000 символов);
- патологический Markdown (ограничение глубины);
- авто-заголовок не перетирает ручное имя;
- битая история не ломает resume;
- путь сессии / регистр / `/private/var`;
- сортировка по последнему сообщению;
- удаление worktree без `--force`;
- `Open in editor` через `ArgumentList`;
- `GIT_TERMINAL_PROMPT=0`;
- approval mode при resume;
- вопрос перед выходом во время работы;
- Esc в посторонних полях;
- возврат неотправленного при рестарте;
- неизвестные кадры → notice;
- «running» не залипает (settle-probe);
- очередь approvals «1 of N».

---

## 3. Оптимизации из других GUI

| # | Приём | Откуда | У нас | Адаптация | Эффект | P |
|---|---|---|---|---|---|---|
| O1 | Дельта-стриминг `set_event_filter` | upstream 18.4.5 #13716; nornzach | ★ Missing | §0 | трафик и разбор JSON на ответ — линейно вместо O(n²) | P1 |
| O2 | Кэш каталога сессий по (path, size, mtime), сохраняемый на диск для тёплого старта, + watcher или reconcile при фокусе | pi-desktop `SessionIndexCache`; OMPiUI `omp-catalog.ts`; nornzach `session-index.ts` | Missing: до 500 файлов (по 64 KB с головы и хвоста) на каждом старте и конце хода любого чата | Словарь по ключу файла, перечитывать только изменённые; на нём же полнотекстовый индекс и дашборд расходов | меньше I/O, мгновенный сайдбар | P1 |
| O3 | Постраничная история + мгновенный первый кадр (хвост JSONL с диска, параллельные `get_state`/`get_messages_page`/`commands`) | piggy 4fea9ce/86726f5; nornzach 0.9.12; OMPiUI `branchWindow` | ★ Missing; запросы при открытии идут последовательно | См. §0; хвост с диска показывать до готовности omp | открытие больших сессий за <1 с | P1 |
| O4 | Авто-recover после краха: backoff 0,5 → 30 с, лимит попыток, сброс счётчика после стабильной работы; `--restore` | nornzach, dylansantwani; omp 18.10.3 | Только ручной Recover | Сделать вместе с подъёмом пака до ≥18.10.3 (риск ниже) | ход переживает краш | P1 |
| O5 | Уровни timeout'ов RPC | piggy a0185c3 | Один 30 с (кроме abort и branch) | 40 с — холодные команды, 120 с — `get_messages`/`get_tree`/`get_entries` | нет ложных «timed out» | P2 |
| O6 | Усыплять idle omp по времени (≥10 мин, `Ready`, без диалогов и очереди); снимок, черновик и якорь сохранять | piggy idle_timeout, GiorgioTassoni `idle.rs` | Только LRU на 4 процесса | Таймер в `SessionHost` | сотни МБ RSS | P2 |
| O7 | Пересмотреть spare-omp по замерам | piggy убрал warm worker: +80 МБ RSS за невидимые 0,45 с | Have (держим) | Замерить `PerfLog`; включать spare только после второго нового чата за N минут | память | P2 |
| O8 | Схлопывать промежуточные `tool_execution_update` одного `toolCallId` | nornzach event-batcher; OMPiUI liveToolOutput | Partial (UI коалесцирует, ридер получает все кадры) | Last-wins в reader для одного id | меньше разбора JSON на шумных инструментах | P3 |
| O9 | Строки по `messageId` | upstream docs/rpc.md; OMPiUI #109 | Missing | §0 | закрывается класс «текст ушёл не в ту карточку» | P2 |
| O10 | Живой субагент: `get_subagent_messages fromByte`, коалесинг 50 мс | OMPiUI | Читаем целиком и по запросу | Опрос раз в 1–2 с, пока карточка открыта | O(Δ) вместо O(n) | P2 |
| O11 | `get_state.usageLimit` вместо запуска `omp usage --json` | omp 18.6.3 #14153 | CLI раз в 30 с | Бейдж из соединения; CLI только для Settings | меньше холодных стартов Bun | P3 |
| O12 | Перф-бюджеты в CI: стрим 60 fps; 10k сообщений открываются <1,5 с; 30 мин стрима — дрейф RSS <5% | piggy perf-run; pi-gui #114 | Один тест линейности | Headless-сценарии с бюджетом, perf-lite блокирует PR | регрессии ловятся до релиза | P2 |
| O13 | Сворачивать длинные code block, diff, thinking (превью N строк) | Orbit; piggy rowMemory | Длинный code block в ответе строится целиком | Code >28 строк → превью 24 + «Show more» | дешевле layout | P3 |
| O14 | Стриминговый Markdown: удерживать незакрытый inline-синтаксис (`**`, `` ` ``, `[x](http…`) | Ompcot `remend`, nornzach | Неясно | Скрывать хвост незакрытой ссылки или выделения до закрытия | нет мигания сырого Markdown | P3 |
| O15 | Windows: срезать `\\?\` и пробелы в пути к `--extension` для Bun | Ompcot, piggy 7ba03b2 | Не проверено | Проверить на профиле с пробелом в имени | стабильный старт | P3 |
| O16 | Сериализовать первые старты omp после изменения плагинов | pi-heao-gui spawn-queue | Неясно (spare + 4 чата могут стартовать вместе) | Семафор на «первый старт после install» | нет сломанного префикса пакетов | P3 |
| O17 | DTO из `rpc-wire.schema.json` + CI-проверка совместимости | upstream ≥18.5.1; issue #3805 | Ручной разбор `JsonElement` | Генерация C#-DTO, тест на каждый апгрейд пака | безопасные апгрейды omp | P2 |

Что не переносить:
- патчи бандла omp (Orbit `rpc_patches.rs`);
- HTTP-сервер внутри omp без модели доверия (Ompcot);
- typewriter-сглаживание.

Удалённый доступ — только E2EE, по образцу iris или OMPiUI.

Риск апгрейда пака выше 18.8.0:
- 18.8.7: steer-interrupt больше не шлёт `agent_end`;
- 18.9.0: «вечно busy» сессия при сбое записи retry.

Это ломает наши `_runStartPending` и settle-probe. Перед подъёмом прогнать real-omp тесты на steer и settle.

---

## 4. Запросы в upstream omp

1. RPC для plan mode: enter/exit, событие с документом плана, approve/reject с feedback.
2. Trust-модель проекта: событие о hooks, extensions и MCP из проекта, ожидание подтверждения.
3. RPC отмены и вывода background jobs; «перевести в фон».
4. MCP OAuth login/reauth и elicitation через `extension_ui_request`.
5. Входящий `rpc_chunk` от клиента, чтобы снять лимит строки 1 MiB (3 картинки × 230 KB).
6. Нативный lock или lease файла сессии.
7. Стабильные id и `timestamp` у всех ролей в `get_messages_page`.
8. Файловые чекпоинты или хотя бы событие «файл изменён» с путём.
9. Частичная компакция («Summarize up to here»).
10. `windowsHide` для spawn MCP stdio и bash.
11. Защита от параллельного OAuth-refresh между несколькими процессами omp.
12. Политика версионирования `--mode rpc` и депрекаций (issue #3805).

---

## 5. Порядок работ

**Волна A — потеря данных и безопасность** (без апгрейда omp, в основном клиентский код):
- 2.1 #1–#6;
- #8 (очередь);
- #12–#13 (ответ на вопрос, черновики);
- #11 (slash во время хода).

**Волна B — скорость и стабильность:**
- O1 (delta);
- O3 + #14 (страницы истории);
- O2 + #15 (кэш каталога, ошибки скана);
- #16 (Job Object);
- #17 (PATH);
- #9 (недоступная модель);
- #7 (single-instance + lease);
- O5 (timeout'ы);
- O12 (перф-бюджеты в CI).

**Волна C — дешёвый паритет на RPC пина:**
- Stop / Message для субагентов;
- `abort_and_restore_queue`, Send now;
- Fork;
- `/btw`;
- Goal-полоса;
- Cancel retry;
- форма `ask`;
- `queue_update`;
- `messageId`.

Плюс исправить PARITY, ROADMAP и PROJECT_STATE.

**Волна D — крупные функции:**
- Find в чате + выделение через блоки;
- палитра ⌘K;
- полнотекстовый поиск по сессиям;
- история промптов;
- чекпоинты кода + Review-лента со скоупами;
- Git-панель (commit/push/PR);
- архив и фильтры;
- trust-карточка;
- согласие браузера по сайту.

**Волна E — desktop и доступность:**
- tray-резидент;
- toast;
- deep links;
- зум;
- reduced motion и live regions;
- Quick chat и Appshots;
- automations;
- remote по SSH;
- импорт;
- Mermaid и LaTeX.

**Волна F:** апгрейд пака до ≥18.10.3 (`--restore`, авто-recover O4) после real-omp тестов конца хода.

**Волна M — микро-UX** (§6): идёт параллельно с любой волной; почти всё S, без RPC и без апгрейда omp.

---

## 6. Микро-UX: мелочи, которые не замечают, но чувствуют

Четыре независимых прохода:
- [`micro-codex.md`](parity-research/micro-codex.md) — changelog и документация Codex app, ~85 пунктов;
- [`micro-claude.md`](parity-research/micro-claude.md) — Claude Code Desktop, VS Code и CLI, 369 записей `[VSCode]` + документация, ~90 пунктов;
- [`micro-guis.md`](parity-research/micro-guis.md) — UI-код pi-gui, nornzach, piggy, OMPiUI, Orbit, dylansantwani, PiPilot, ~90 пунктов;
- [`micro-audit.md`](parity-research/micro-audit.md) — аудит нашего UI, ~110 пунктов. Внутри матрица по 34 типам элементов: меню по правому клику (ПКМ), hover, pressed, кольцо фокуса, тултип, курсор, клавиатура.

Ниже — сводка без повторов. «Сигнал» — сколько проходов из четырёх нашли пункт независимо (A — аудит, C — Codex,
Cl — Claude, G — другие GUI). Effort почти везде S. ★ — перепроверено мной в коде.

### 6.1 Сквозные правила (делать первыми: убирают целые классы мелочей)

| Правило | Сейчас | Сделать | Сигнал | P |
|---|---|---|---|---|
| **Одна обратная связь на копирование** | ★ ✓ на 1,5 с есть только у code block (`MarkdownView.cs:816`). Copy у сообщения, ответа, итога хода и все Copy из контекстных меню (`ContextMenus.cs:92-95`, Files, branch, ссылка входа) срабатывают молча. При неактивном окне копирование пропускается, результат `SetTextAsync` отброшен | Общий attached-behavior и сервис: ✓ на кнопке 1–1,5 с; для меню — brief «Copied path» / «Copied 1 234 characters» через `Loc.Plural`; честное «Copy failed» | A C Cl G | P1 |
| **Одна система меню по правому клику для путей** | Меню есть у http-ссылок и дерева Files; у пути в тексте (`PathLinks`), `tool-path`, `turn-file`, diff-карточек — только tooltip-превью | `ContextMenus.ForPath`: Open in Files, Add to message, Open in editor, Reveal, Copy path / relative path, Open in terminal (для папки). Пункты взять из `FilesPane.axaml:16-60` | A C Cl G | P1 |
| **Токены движения** | ★ Во всём UI две Transitions (тумблер 120 мс, питомец), токенов нет; DESIGN_SYSTEM запрещает только бесконечные анимации | `GuiMotionFast` 120 мс / `GuiMotionBase` 150 мс, `CubicEaseOut`; правило в DESIGN_SYSTEM; один флаг reduced motion, который их обнуляет | A G Cl | P2 |
| **Запоминание UI-состояния** | ★ Сохраняется только `SidebarWidth`. Размер, позиция и maximized окна, ширины Preview, side pane, Terminal и Events, видимость сайдбара и открытые панели теряются после перезапуска (окно всегда 1100×760 CenterScreen) | Normal bounds + maximized с прижатием к видимым мониторам, debounce 250 мс (как в PiPilot); ширины и видимость — рядом с `SidebarWidth` | A C Cl G | P1 |
| **Одни подписи шорткатов** | В 7 файлах «Ctrl/⌘+Shift+…» захардкожено | Tooltip и пункты меню через `KeyboardShortcuts.Text` в нотации платформы (⌥A, ⇧Tab, ⌃`); колонка хоткея в контекстных меню | C Cl G | P2 |
| **Локализация хвостов** | Захардкожены `"Copied"` (`ImageViewer.axaml.cs:52`), ошибка буфера и заголовки file-диалогов (`MainWindow.axaml.cs:849/861/961/995`), «… N more lines» (`DiffView.cs:240`), «Continued with…» (`ConversationState.cs:359`); `Loc.Plural` без CLDR | Вынести в Loc; плюрализация по CLDR (ru: one/few/many) | A C | P2 |

### 6.2 Меню по правому клику (ПКМ)

| Где | Что должно быть | Сейчас | Сигнал | P |
|---|---|---|---|---|
| Строка и карточка инструмента | Copy command, Copy output, Copy path, Open in Files, Reveal; кнопка Copy в `card-head` для diff и для command+output | Нет ни меню, ни кнопки (`MainWindow.axaml:562-678`) | A C Cl G | P1 |
| Путь в тексте ответа | см. 6.1 `ForPath` | Только hover-превью | A C Cl G | P1 |
| Ссылка | Open in Preview (localhost, HTML), Open in browser, Copy link; ⌘-клик — системный браузер; tooltip с полным URL | Open / Copy, URL не виден (`MarkdownView.cs:740-750`) | A C Cl G | P2 |
| Картинка (в сообщении и chip вложения) | Copy image, Save as…, Open in default app, Reveal | `ImageMenu` — только Open (`MainWindow.axaml:91-102`) | A C Cl | P2 |
| Строка сессии | Mark as read/unread, Copy session ID, Reveal, Open in terminal | Rename, Pin, Copy path, Delete | C Cl G | P2 |
| Вкладки Preview и Terminal | Close, Close others, Close to the right, Copy URL, Reload; средний клик закрывает; hover; длинный заголовок обрезается | Только × | A C G | P2 |
| Терминал | Copy selection, Copy all, Paste, Clear; у ссылок — Open / Copy | Нет | G | P2 |
| Сообщение в очереди, chip Pasted text | Edit, Send now, Remove / Preview, Remove — как у `AttachmentMenu` | Нет; карандаш только на последнем через ↑ | A G | P2 |
| Ошибка ответа, notice | Copy error | Нет | C | P2 |
| Event log | Выделяемые строки, Copy line, Copy all, Clear | Нет (`MainWindow.axaml:892-899`) | A | P3 |
| Блок кода | Insert into message | Только Copy | G | P3 |
| Выделение в ответе | Quote in message, Comment (см. §1.1) | Только Copy | C | P2 |

### 6.3 Hover, pressed, курсор, тултипы

| Что | Сейчас | Сделать | Сигнал | P |
|---|---|---|---|---|
| Сплиттеры панелей | 1 px, без подсветки (`MainWindow.axaml:797,804,809,879`) — панель «не тянется» | Зона захвата 7 px, подсветка при hover, двойной клик сбрасывает ширину — как `SidebarGrip` | A | P1 |
| Кнопки действий у строк (`row-action`, `session-action`, copy блока кода) | Появляются мгновенно | Opacity 120 мс | G | P3 |
| Шевроны | Поворот мгновенный | Один стиль на все `Icon.chevron`: поворот 120–150 мс | A Cl G | P3 |
| Клик по фону заголовка diff | Не сворачивает | Сворачивает | C | P2 |
| Alt-клик по шеврону | — | Свернуть или развернуть все карточки хода | C | P2 |
| Строка таблицы в Markdown | Без подсветки | Подсветка при hover | G | P3 |
| Send | Без pressed-эффекта | `scale(0.94)` на нажатие | G | P3 |
| Ручка сайдбара с клавиатуры | Нет | ←/→ ±20 px, Home/End | G | P3 |

### 6.4 Обратная связь и состояния

| Что | Сейчас | Сделать | Сигнал | P |
|---|---|---|---|---|
| Brief- и Undo-карточка | Закрывается по таймеру 4 / 8 с даже под курсором; новая заменяет предыдущую | Пауза при hover и фокусе; очередь до 2; повтор того же сообщения продлевает показ | A C G | P1 |
| Undo | Только при скрытии проекта | Undo для удаления сообщения из очереди, unpin, снятия правила | C | P3 |
| «↓ к последнему» | Только иконка, появляется без анимации | «↓ N new» или точка, когда внизу прибыло новое; fade 150 мс | A Cl G | P2 |
| Список сессий при первой загрузке | Пусто до первого скана | Скелет или «Loading sessions…» с зарезервированной высотой | Cl | P2 |
| Открытие сессии | Activity-строка не видна в фазе `Starting` (`MainWindow.axaml:1047`) | Показывать «Opening…» | A | P2 |
| Drop-зона composer | Без подсветки | Tint-фон и подпись «Drop images or files to attach»; папка на сайдбар → «Add project» | G | P2 |
| После удаления открытого чата | Открывается новый пустой (`MainViewModel.Session.cs:579`) | Открывать соседний | C | P2 |
| Карточка запроса (approval / вопрос) | Закрывает ленту | Свернуть в однострочную полосу; ⌘↵ = основное действие; ⌘↵ отправляет многострочный ответ и «say why» | C Cl G | P2 |
| ✓ после rename, «Pasting…» при медленном буфере | Нет | Добавить | G Cl | P3 |
| Невидимые Unicode-символы при вставке | Вставляются как есть | Вырезать и сообщать «Removed N invisible characters» (связано с P0 §2.1 #1) | Cl | P1 |
| Уведомления | Вкл / выкл | Never / Background / Always + отдельные переключатели для вопросов и разрешений | C | P2 |
| Dock-бейдж | Нет | Число ждущих чатов, `requestUserAttention` (macOS) | A Cl | P2 |

### 6.5 Клавиатура и фокус

| Что | Сейчас | Сделать | Сигнал | P |
|---|---|---|---|---|
| Type-to-focus | Ввод при фокусе в разговоре или сайдбаре теряется (`MainWindow.axaml.cs:37`, `ChatScrollController.cs:59`) | Печатный символ перенаправлять в composer | A | P1 |
| Фокус после смены чата | Остаётся в сайдбаре | `FocusComposerRequested` в `OpenSessions` | A | P1 |
| Фокус после viewer, карточки, меню | Всегда в composer | Возвращать тому, кто открыл | G | P2 |
| ⌘L / ⌘Esc | — | Фокус в поле сообщения из любого места | Cl | P2 |
| Список сессий | Только Tab | ↑/↓, Enter, F2 (rename), Delete; двойной клик = rename | A C Cl | P2 |
| ⌘W | Закрывает окно | Сначала закрывает активную вкладку Preview / Terminal (`MainWindow.Menus.cs:55`) | C | P2 |
| Preview | — | ⌘L адрес, ⌘R reload, ⌘[ / ⌘] назад и вперёд, ⌘T, ⌘⇧C | A C | P2 |
| ⌘\ | — | Закрыть панель с фокусом; ⌘⇧I меню модели, ⌘⇧E thinking; всё — в shortcut sheet | Cl | P2 |
| Mark unread / all read | — | ⌘⇧U, ⇧Esc | C | P2 |
| Esc-Esc на пустом поле | — | Открыть Rewind | Cl | P3 |
| Меню Help | Нет | Shortcuts, Report a problem, What's new | C | P2 |

### 6.6 Проверить руками (по коду не определить)

- IME (японский или китайский ввод на macOS и Windows): Enter и Esc во время набора не должны отправлять сообщение и останавливать ход.
- ANSI-коды в выводе инструментов (`ls --color=always`).
- Мышь в slash-меню перехватывает выбор?
- Мигание «Loading» в Files.
- Shift-клик расширяет выделение в тексте сообщения.

### 6.7 Где мы уже лучше (не трогать)

- Превью файла и картинки по hover на пути (`PathLinks`) — у Claude только путь в tooltip.
- Подсказки клавиш в placeholder поля ввода по состоянию (`ComposerHint`).
- Чип причины сбоя (`exit 127`) на строке инструмента.
- Цепочка возврата фокуса после model menu.
- Лестница Esc; `TrimmedToolTip`.
- «Worked for · tools · files».
- Просмотрщик картинок.
- Undo при скрытии проекта.
- Каретка не мигает в неактивном окне.

### 6.8 Порядок (волна M)

1. Сквозные правила 6.1: копирование, `ForPath`, запоминание состояния окна и панелей, токены движения.
2. P1-точки: сплиттеры, type-to-focus, фокус после смены чата, ПКМ у инструмента, пауза тоста, очистка невидимых символов.
3. Остальное по таблицам: в каждой строке указан файл, с которого начинать.
