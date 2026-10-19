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

Исходный снимок 2026-10-13 проверялся grep/read, без запуска. Пункты со звёздочкой (★) перечитаны отдельно. Уточнения реализации beta R0.2 от 2026-10-15 отмечены ниже; фактические gates и ограничения ведутся в [NEXT_RELEASE_ROADMAP](../../docs/NEXT_RELEASE_ROADMAP.md), а не переносятся автоматически на все ОС.

### Реестр исполнения следующего релиза

Каждая строка §0, §1.1–1.9, §2.1 (#1–38), §2.2, §3 (O1–O17), каждый пункт §4 и §6.1–6.8,
включая исходные `micro-*` таблицы, сохраняется в объёме. Идентификатор строки — раздел и её
исходное имя/номер; дублирующий сокращённый backlog не заменяет этот реестр.
Интеграционный владелец всех строк — текущий исполнитель roadmap; специализированные исполнители
и reviewers не снимают с него ответственность за миграцию consumers и итоговую проверку.
Очередность владельца: A → B → C, независимые K/B proof, затем D/E/F/M и интеграционная приёмка.
Для каждой закрываемой строки обязательны источник изменения, фактически выполненные проверки,
UX/UI и независимый security review затронутых границ, а также явные непроверенные ОС.
Непереоценённые `Bug`/`Missing` ниже — исходный аудит 2026-10-13, не повторное доказательство
дефекта текущего кода; до изменения требуется актуальное воспроизведение. Приёмка одного блока
не закрывает соседние строки или весь roadmap.

Текущая воспроизводимая база: macOS arm64, .NET SDK 10.0.401, Rust 1.99.0, существующий
NightlyBrowser 1.23.2b (соответствие бинарника новым исходникам ещё не доказано), выбран
CommandLineTools, полноценный Xcode/Android SDK не обнаружены в проверенных стандартных путях.
Windows/Linux verification hosts не настроены; посторонний VPS не используется как тестовый стенд.
Общее ядро: Release 1411 passed / 34 skipped, real-engine 15 passed / 2 skipped, Bun 33 passed;
это macOS/stand-in evidence, не доказательство остальных функций и платформ.

**Итог релиза (RC QA, 2026-10-18).** Волны A–F и M закрыты на macOS (агенты ParityAB, ParityD, ParityE, ParityF,
ParityM, ParityRpc; этап 8 — Stage8); построчные статусы ниже. Релиз — только macOS (решение владельца). RC-прогон
на macOS arm64: Ombra.Tests **2206 passed / 0 failed / 44 skipped** в en и в ru, real-omp smoke 16/0 + perf-lite
(тёплое открытие 10k — 1323 мс при бюджете 1500), Bun engine 143/0, runtime/tests 28/0. Пропущенные 44 — тесты,
включаемые переменными окружения (настоящий omp — их часть прогоняет real-omp smoke, настоящий провайдер/модель,
речь и аудио, dev Nightly), и Windows-only. Открытые gates — Windows/Linux native, физический IME и
sleep/wake, Developer ID/нотаризация — в [RELEASE_GATES](../../docs/RELEASE_GATES.md).

## Главное

1. ★ **Закреплённый omp 18.8.0 уже отдаёт по RPC больше, чем мы используем.** Исходный аудит нашёл 23 неиспользуемые команды (§0); beta R0.2 уже подключает `abort_and_restore_queue`. Остальные строки сохраняются: `/fork`, `/tree`, `/btw`, `/goal`, `set_event_filter` не требуют апгрейда пина.
2. ★ **Квадратичный стриминг устранён на клиентском RPC-пути.** После `negotiate_protocol` клиент включает `set_event_filter {events:null,messageUpdates:"delta"}` на закреплённом omp 18.8.0. Все типы событий сохранены, `message_end` остаётся полным. Реальный runtime: одинаковый финальный текст, 26 updates, 29 932 → 7 222 байта в коротком smoke; это не end-to-end benchmark.
3. ★ **Постраничная история реализована:** хвост 100 сообщений, загрузка старых страниц без монолитного fallback; нативный сценарий 360 сообщений сохранил порядок, уникальность, исходное название и якорь (0 px). Бюджеты 10k сообщений и межплатформенная приёмка остаются отдельными gates.
4. **Сохранность ввода — реализована в beta R0.2:**
   - очередь возвращается в редактируемый composer при Stop/крахе/рестарте, без автоматического resend;
   - отозванный ответ восстанавливается с цитатой вопроса; поля входа/авторизации исключены;
   - черновики с изображениями, вставками и комментариями сохраняются в локальный journal с debounce 500 мс и flush на границах отправки/закрытия. Потерянный RPC acknowledgement не даёт гарантии exactly-once.
   - **Реализовано в beta R0.3:** одна конфигурация — один экземпляр Ombra (OS mutex до миграции данных + OS lease на journal). Проверено двумя реальными процессами: второй получает отказ без записи, другая конфигурация работает, после SIGKILL перезапуск получает lock. Не защищает от старых версий и от omp CLI/TUI.
5. **Безопасность (P0):**
   - **Реализовано в beta R0.2:** невидимые/control/bidi-символы показаны маркерами, не участвуют в автоматическом разрешении; prefix policy консервативна для новых и сохранённых правил. Exact/prefix identity не меняется при сохранении.
   - **Реализовано в beta R0.3:** UNC, `//host`, `\\?\`, `\??\` и `file://host` (включая URL-encoded формы) отклоняются до любого обращения к файловой системе — в ссылках, превью, автоопределении новых файлов и путях, полученных от Git. Проверено платформенно-нейтральными тестами; нативный прогон на Windows не выполнялся.
   - **Реализовано в beta R0.3:** клиентские вызовы Git перечисляют все filter-драйверы (включая include/global/worktree) и отключают их clean/smudge/process на время запуска; конфигурация пользователя не меняется. Некорректный UTF-8, превышение лимитов вывода/драйверов и таймаут — отказ без запуска команды. Удаление worktree учитывает фильтры целевого worktree и отклоняет worktree с submodules. Проверено на собранном клиенте: до исправления фильтр исполнялся, после — нет. Не sandbox: не защищает от одновременного изменения конфигурации и не блокирует сетевые include.
   - **Реализовано в beta R0.3:** hardened runtime во всех ветках подписи macOS; `StartupHookSupport=false`. Проверено: `DOTNET_STARTUP_HOOKS` и `DYLD_INSERT_LIBRARIES` исполнялись до изменений и не исполняются после. Ad-hoc/self-signed подписи требуют исключения library validation (без Team ID .NET не загружает собственные библиотеки); Developer ID его не получает. Notarization не проверялась — нет сертификата.
   - **Реализовано:** keyboard и positive-pointer approval требуют свежего жеста после 500 мс; смена карточки, другой pointer и overlays не наследуют разрешение. Deny без задержки. Проверены headless regressions и native macOS routed-pointer smoke; физический OS input/другие ОС остаются gates.
6. ★ **Builtin slash-команды во время хода исправлены:** FIFO до завершения предыдущего ввода, затем runtime `prompt`; локально обработанный ввод не выдаётся за ход модели. Нативно проверены отложенный builtin, поглощённый ввод и вложенный ход расширения.
7. **Windows-сироты:** Job Object с `KILL_ON_JOB_CLOSE` реализован до первого дочернего процесса после admission; updater и явно открытые внешние программы запускаются независимо. Нативные Windows-проверки ещё не выполнены.
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

## §0. Быстрые победы: дополнительные RPC закреплённого omp 18.8.0

★ Проверено по `…/Ombra/runtimes/omp-18.8.0-bun-1.4.2/…/modes/rpc/rpc-mode.ts` и grep по `avalonia/src`.

| RPC (в пине) | Что даёт | Что сделать | P |
|---|---|---|---|
| `set_event_filter {events:null,messageUpdates:"delta"}` | линейный, а не O(n²) трафик обновлений и разбор JSON | Реализовано после `negotiate_protocol`, до загрузки истории/Ready, включая restart; `message_end` остаётся полным. 68 targeted tests PASS, включая real-omp | P1 |
| `get_messages_page` | история страницами | Реализованы tail-first и prepend; `stale_cursor` обновляет согласованную страницу, не скачивает монолитную историю. Native 100/360, drift 0 px; большой performance budget проверяется отдельно | P1 |
| `abort_and_restore_queue` | Stop возвращает очередь в composer | Реализовано в beta R0.2: локальные originals сохраняются, восстановленное не отправляется автоматически | P1 |
| `promote_queued_message` | «Send now» для сообщения из очереди | Реализовано: кнопка ↑ на чипе follow-up; чип остаётся как Steer (и в журнале) до доставки, затем исчезает | P2 |
| `cancel_subagent`, `steer_subagent` | остановить или направить одного субагента | Stop / Message в Background tasks | P1 |
| `btw`, `btw_cancel`, `get_btw_history` (+ `btw_delta`/`btw_record`) | вопрос «в сторону» во время хода | Реализовано: панель «Кстати» над composer (`Views/Session/BtwPanel`, `MainViewModel.BtwGoal.cs`), открывается чипом «Кстати» (пока omp работает или есть темы) и `/btw [вопрос]`; ответ стримится по `btw_delta`, `btw_record` меняет статус, Stop — `btw_cancel`, «Уточнить» — follow-up в теме (`recordId`), история сессии из `get_btw_history` (сверка по времени чтения с кадрами); Esc в панели закрывает её, ход не останавливает. 5 targeted tests (en/ru) + native `btw` (реальный engine, BTWQ в stand-in model: ответ во время LONGTASK, Stop, follow-up, история с диска) PASS | P2 |
| `fork {entryId}` | нативный fork | Реализовано: «Fork from here» на ответе (кнопка в строке действий и правое меню) и «Fork» в меню чата / `/fork` (вся сессия); строка сопоставляется с `message`-записью текущего пути из `get_entries` (`ForkPoints`: роль + текст, `timestamp` сообщения, иначе место с конца), чат продолжается в новом файле, исходный остаётся; `session_busy` → карточка «omp ещё работает…». Targeted tests (en/ru) + native `fork` (реальный engine: from-here, меню, отказ при `!sleep`) PASS | P2 |
| `get_tree` | дерево веток | диалог дерева (только итеративные обходы, см. B-класс у nornzach) | P3 |
| `goal` | goal mode без терминала | Реализовано: полоса цели над composer (`Views/Session/GoalBar`): objective, статус, время (от `timeUsedSeconds`, тикает у активной), токены/бюджет, Pause/Resume, Edit (omp нет op для смены: drop + create с тем же бюджетом, пауза сохраняется), Clear (`drop`; достигнутая цель просто убирается). Состояние из `goal_updated`, `get_state.goal` (в т.ч. после рестарта omp — цель возвращается на паузе) и ответов `goal`; уведомление `goal_updated` в разговоре убрано. `/goal [objective]` — в GUI. Targeted tests + native `goal` PASS | P2 |
| `abort_retry` | отмена авто-повтора | одна живая строка «Retrying in 0:42 · attempt 3/10 · Cancel» | P2 |
| `set_ask_dialog` | все вопросы `ask` одной формой, multi-select | Реализовано: opt-in на каждом соединении; карточка со вкладками (header вопроса), один/несколько вариантов, свой ответ, «Рекомендуется» без предвыбора, цифры выбирают; Submit шлёт ответы по порядку, Cancel — `cancelled`; без команды остаются карточки select/input. Исправлен runtime: `ask` падал в RPC (`theme.status`). 10 тестов AskFormAndTodosTests + native smoke `ask-form` PASS | P2 |
| `queue_update` (событие) + `get_state.queuedMessages` | очередь, которую ведёт omp | Реализовано: чипы сверяются со снимком omp (`ConversationState.QueueUpdate.cs`): локальный `Enqueue` — оптимистичное эхо, пока omp его не перечислил (старый снимок его не снимает); при совпадении сохраняются картинки и submission id, повышение в steer меняет вид того же чипа, чужие записи (другой клиент, расширение) появляются, снятые omp исчезают; Stop с `abort_and_restore_queue` удерживает снятия до ответа. В снимке только тексты чипов, без id: запись сверяется с чипом, который omp из неё делает, — картинка без текста у omp «[Image]» (`queueChipText`), это та же строка с миниатюрой, а не вторая. Targeted tests + native `queue-update` (запись через RPC «извне» появляется чипом и доставляется) PASS; FakeOmp `queue-update[-N]` шлёт снимки как omp (UI-тесты картинки без текста: одна строка, Send now, ↑, ✕, одна доставка) | P2 |
| `messageId` в `message_*` | дельта точно попадает в своё сообщение | Реализовано: словарь `messageId → row` (`ConversationState.MessageIds.cs`); поздняя дельта/`message_end` сообщения A после старта B попадает в строку A; без `messageId` — прежние эвристики `_streamingAssistant`. Targeted tests + native `message-id` PASS | P2 |
| `set_todos` | правка плана | Реализовано в Plan pane: чекбокс done/to do, ⋯ — текст, статус, вверх/вниз, удалить; «Добавить задачу». Шлётся весь список (details/notes сохраняются), показывается ответ omp, затем get_state; правки в History. Native smoke `todo-edit` PASS (сверка с get_state после каждой правки) | P3 |
| `set_cache_warming` (+ `cache_warming_*`) | прогрев prompt cache | переключатель и индикатор «cache warm 3m» | P3 |
| `set_slow_mode`, `get_state.usageLimit` | /slow; стадия лимита без запуска CLI | тумблер; бейдж лимита из уже открытого соединения вместо `omp usage --json` | P3 |
| `set_steering_mode`, `set_follow_up_mode`, `set_interrupt_mode` | поведение очереди | Settings › General «Enter во время работы: в очередь / steer» | P3 |
| `predict_word`(+`_feedback`) | ghost-text в composer | за настройкой | P3 |
| `open_session` с `provider`/`modelId` | открыть сессию на другой модели | восстановление после «Could not restore model» (см. B-класс 9) | P1 |
| `get_entries`, `live_*`, `set_host_tools` | entries, голос, host-tools | Реализовано: `set_host_tools` → `terminal_read` (агент читает выбранную или указанную номером вкладку терминала: последние 200 строк, максимум 2000 строк / 64 K символов; регистрируется на каждом подключении, включая рестарт; одобрение — общим механизмом omp; `host_tool_cancel` отменяет); `get_entries` — для Fork from here. Targeted tests + native `terminal-read` (TERMREAD в stand-in model, карточка одобрения) PASS. `live_*` — нет | P2/P3 |

PARITY.md и PROJECT_STATE обновлены для общего ядра, delta и paging. Остальные RPC/UI строки остаются в плане; старое описание процесса на чат и spare/LRU не является текущей архитектурой.

---

## 1. Паритет по функциям

Для каждой функции: что делают они → что у нас → как сделать лучше. Источники и доказательства — в `parity-research/*.md`.

### 1.1 Транскрипт и чтение

| Функция | У них | У нас | Как сделать лучше | P |
|---|---|---|---|---|
| **Find в чате** ⌘F / ⌘G | Codex app 26.318/26.423, CLI 0.156–0.162; Claude `/` в transcript | **Реализовано в транскрипте:** ⌘F/Ctrl+F (префилл выделенным текстом сообщения), счётчик «3/17» / «0/0», Enter/⇧Enter и ⌘G/⇧⌘G (⇧⌘G — Changes, пока бар закрыт), Esc → фокус в composer. Поиск по модели строк (`TranscriptItem`: текст user/assistant, thinking, ошибка, строковые значения аргументов и вывод инструментов, diff, notices, command output, IRC) — без учёта регистра (ordinal), обе стороны NFC; включая загруженные, но не построенные строки. Переход: `ChatScrollController.Reveal` реализует строку и ставит совпадение на треть экрана; ход «Worked for…», группа шагов, вывод инструмента, thinking, длинное сообщение раскрываются только на время показа; совпадение выделяется в `SelectableTextBlock`. При стриме счётчик обновляется без сдвига вида и текущего совпадения | Осталось: то же в CodeView и diff; точное соответствие выделения, когда Markdown убирает разметку (берётся то же по счёту вхождение в видимом тексте строки) | P1 |
| **Выделение через блоки, rich copy** | Codex 0.156–0.162 (copy-on-select, таблицы, HTML+plain) | Partial: каждый абзац — отдельный `SelectableTextBlock` | Модель текста хода + выделение на уровне `TranscriptPanel`. В clipboard класть text/plain и text/html. Делать вместе с Find — у них общая основа | P2 |
| **Ссылки кликабельны везде** | Codex 0.162 (approval, вопросы, warnings, баннеры) | Только в ответах; Title/Message/Note карточек и notices — plain | Общий `LinkifiedText` (URL + `PathLinks`) | P2 |
| **Реальный адрес ссылки виден** | Claude 2.1.295–296 | Tooltip с URL нет | Tooltip/статус с полным href, IDN — в punycode, предупреждение «текст ≠ адрес» | P2 |
| **Mermaid, LaTeX** | Codex app/iOS, CLI 0.156–0.159 | **не делаем (принцип владельца)** | Отдельные движки диаграмм/формул и их sandbox/layout не закрывают заявленную боль работы с агентом; сохраняем читаемый и копируемый исходник в нативном Markdown (`MarkdownView`), без обещания rich-рендера | — |
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
| **Черновики переживают выход и краш** | Codex 26.506/26.908; Claude 2.1.208 | Реализовано в beta R0.2: per-config `.drafts.json`, текст/bytes изображений/чипы/комментарии, debounce 500 мс, atomic replace и flush; cross-instance lease — R0.3 (`ClientInstanceLease` + `.drafts.json.lock`, `JournalOwnershipTests`, перепроверено ParityAB) | Осталась платформенная приёмка Windows/Linux; plaintext локальный, encrypted storage — не делаем (принцип: нет заявленной боли, ключ хранился бы рядом) | P1 |
| **Send now / interrupt-and-send** | Claude 2.1.275 | Реализовано: «Send now» на чипе (`promote_queued_message`), «Stop and send» / ⌥⇧Enter (`abort_and_prompt`); при отказе текст и изображения возвращаются в composer | Builtin-команды не прерывают ход (abort_and_prompt не исполняет `/`-команды) | P2 |
| **Stop возвращает очередь** | Claude 2.1.239; omp TUI Esc | Реализовано в beta R0.2 через `abort_and_restore_queue`, с сохранением локальных originals и без resend | Сверять ограничения payload и потерянных acknowledgements; не обещать exactly-once | P1 |
| **Ответ на вопрос не теряется** | Codex 0.157/0.162 | Реализовано в beta R0.2 для наблюдаемой отмены/revoke/send failure; ответ возвращается с цитатой, `/` и `!` не становятся командами | Remote revoke после успешно записанного fire-and-forget ответа требует runtime acknowledgement; auth-поля не сохраняются | P1 |
| **`/` и skills в любом месте текста**; skills/MCP в `@`-меню | Codex 26.317/26.318, Claude 2.1.0 | Только когда текст начинается с `/`; в `@` только файлы | Триггер после пробела; секции Skills и Connectors в `@`-меню; подсветка совпадений | P3 |
| **Вложения** | Codex: любые файлы; Claude: 17–20 картинок, HEIC/BMP/TIFF | 3 картинки × 230 KB; прочие файлы — путём | Чип файла; картинку сохранять в артефакты сессии и передавать путём, тогда лимит снимается; HEIC/BMP/TIFF → JPEG. Upstream: входящий `rpc_chunk` | P2 |
| **Модель/thinking по умолчанию** vs «только эта сессия» | Claude 2.1.153; Codex 0.162 | Missing; выбор модели вне Ready молча игнорируется | «Use for new chats»; `PendingModel` применяется на Ready до первого prompt | P2 |
| **Send до готовности omp** | Claude 2.1.10 | Send выключен до Ready | Ставить в очередь и отправить на Ready | P3 |
| **Внешний редактор, ghost-text, spellcheck** | Claude | Missing | P3 | P3 |

### 1.3 Сессии и навигация

| Функция | У них | У нас | Как сделать лучше | P |
|---|---|---|---|---|
| **Полнотекстовый поиск по сессиям** (содержимое, ветка, URL PR) | Codex 26.323/26.527; GiorgioTassoni (FTS5); nornzach | **Реализовано по содержимому:** `SessionContentIndex` поверх строк каталога — текст user/assistant (без tool output и thinking; ≤2000 символов на сообщение, ≤64K на сессию, строки >1 MiB пропускаются), инвалидация по size+mtime как у O2, private profile/root-scoped sidecar `.session-search.json` (0600), повреждённый перестраивается. Сайдбар: title-совпадения сразу, совпадения в сообщениях после debounce 250 мс под ними со сниппетом и выделенным термином; новая клавиша отменяет поиск; IO-ошибки — notice + частичные результаты; клик открывает сессию с Find в чате на запросе | Ветка и URL PR в индексе — не делаем (принцип владельца), как и PR-бейдж (§1.6) | — |
| **Палитра команд** ⌘K / ⌘P файлы | Codex 26.208–26.609 | **Реализовано:** `CommandPalette` + `MainViewModel.Palette.cs`. ⌘K/Ctrl+K — все действия окна (через их же RelayCommand; недоступные серые с причиной), панели, все страницы Settings, темы, чаты из каталога (Unread сверху, затем новые), файлы проекта (индекс Files/«@», поиск вне UI-потока, ограничен и отменяется следующей клавишей). ⌘P/Ctrl+P — только файлы. Fuzzy: префикс > начала слов > подстрока > разбросанные буквы, совпавшие буквы жирные; недавно запущенное первым (`PaletteRecent` в настройках клиента); ↑↓ Enter Esc, Esc возвращает фокус. Конфликт решён: ⌘K и ⌘P были свободны (⇧⌘F/G/P/T/B/S/M — панели и превью, ⌘F/⌘G — поиск в чате) | Unread-порядок без отдельного headless-теста; поиск по ключевым словам (синонимам) | P1 |
| **Архив** (+ автоархив, Unarchive all) | Codex 26.323; Claude VSCode 2.1.252/2.1.265 | **Реализовано (ParityD):** клиентский список `ArchivedSessions` в настройках (файлы не трогаются); «Archive / Unarchive» в меню строки и hover-кнопка вместо корзины (Delete остался в меню), заметка с Undo; группа «Archived N» последней, свёрнута до клика, при поиске/чипе раскрывается; «Unarchive all» в заголовке и его меню; ⌘1–9 и Ctrl+Tab пропускают свёрнутый архив | Автоархив — не делаем (принцип владельца): правило «архивировать старые» ничего не чинит, ручного архива достаточно. Тесты: `Archive_moves_a_session_under_a_folded_Archived_group_and_back`; native `sidebar-nav` | — |
| **Фильтры по статусу** (Working / Needs input / Unread / Errors) | Claude VSCode 2.1.259; Codex Activity 26.727 | **Реализовано (ParityD):** чипы под полем поиска сессий (видны с поиском или пока чип выбран), повторный клик — все; список пересчитывается при смене статуса; пусто → «No sessions match» | Персистентный unread — не делаем (принцип владельца): статусы есть только у открытых чатов, хранить «непрочитано» для закрытых — новая модель без заявленной боли. Тест `Status_chips_back_forward_and_next_waiting_move_between_chats`; native `sidebar-nav` | — |
| **Список: «Show older», ошибка вместо пустоты** | Codex 0.162 #51230; Claude 2.1.294 | **Реализовано:** IO-ошибка — notice + прошлый список (было раньше); упор в лимит 500 файлов теперь не ошибка: «Show older sessions» под списком читает ещё 500 (`CatalogLimit`) | Тест `Capped_scan_preserves_older_known_rows_and_Show_older_reads_past_the_cap` | — |
| **Сверка с диском при фокусе окна** | pi-gui 9c4ce78; Orbit | **Реализовано (проверено ParityD):** `SetWindowActive(true)` → `RequestCatalogRefresh` по mtime/size (кеш каталога); живой чат из другого процесса — K6 lease read-only | Тест `Returning_to_the_window_reconciles_external_changes_without_replacing_surviving_rows`. Хвост файла idle-сессии — не делаем (принцип): lease-баннер «Open here» уже решает | — |
| **«Открыта в другом месте»** | Claude VSCode 2.1.252 | **Реализовано (K6):** `session_lease_held` при open/switch/resume показывает чат только для чтения из файла с баннером «Open in <другом окне Ombra / omp в терминале> (pid N) — Open here»; `session_lease_lost` переводит открытый чат в read-only «This chat was opened elsewhere — Open here» без crash/auto-recover. «Open here» = root `engine_take_session {path}` (без силового захвата), затем переоткрытие; composer заблокирован, черновик сохраняется | Headless + FakeOmp: 9 SessionLease tests passed; реальный двухпроцессный GUI-сценарий и Windows/Linux не проверены | P1 |
| **Переходы** ⌘1–9, ⌘[ ⌘], «следующий ждущий» ⌘⌥A | Codex commands | **Реализовано (ParityD):** ⌘/Ctrl+1–9 — n-я строка сайдбара; ⌘[ / ⌘] — стек показанных чатов (до 50, удалённые пропускаются); ⌥⌘A — следующий ждущий ответа, затем непрочитанный; в листе шорткатов | Native `sidebar-nav` (NSEvent ⌘[ ⌘] ⌘2 ⌥⌘A) PASS; тест `Status_chips_back_forward_and_next_waiting_move_between_chats` | — |
| **Сворачиваемые группы, двойной клик = rename, пользовательские группы** | Codex 26.417/26.203; Claude 2.1.231 | **не делаем (принцип владельца)** | Группы = папки проектов (pin/rename/remove есть), свёрнут только архив; пользовательские группы — вторая система организации без заявленной боли; rename есть в меню | — |
| **Импорт из Claude Code / Codex** | Codex 26.608, 2026-08-11 | **не делаем (принцип владельца)** — отдельный GUI-мастер импорта | Миграция чужих форматов истории не является заявленной болью владельца; существующий импорт omp через его терминал остаётся, второй каталог/мастер миграции не строим | — |
| **Remote (SSH-хост)** | Codex 26.415 → GA | **не делаем (принцип владельца)** — удалённый GUI-runtime | Settings › SSH hosts уже управляет хостами инструмента omp (`SshSettingsViewModel`); отдельный remote session transport, удалённый Files и recovery — второй рабочий контур без заявленной боли, не выдаём настройку SSH-инструмента за remote GUI | — |
| **Несколько окон / pop-out / split view** | Codex 26.212/26.415; nornzach split | **не делаем (принцип владельца)** | Вынос в отдельные окна владелец уже отклонил (PRODUCT_DIRECTIONS «Принцип»); чаты переключаются ⌘1–9 / ⌘[ ] | — |

### 1.4 Rewind, fork, checkpoints

| Функция | У них | У нас | Как сделать лучше | P |
|---|---|---|---|---|
| **Чекпоинты кода по ходам** (rewind: разговор / код / оба) | Claude 2.0.0+; pi-gui checkpoint-store; PIDesktop | **не делаем (принцип владельца)** | Теневой репозиторий со снимком до каждого хода и откатом поверх правок пользователя — тяжёлый продукт с риском потери данных; откат кода уже есть: Files › Changes (revert hunk/файл) и git | — |
| **Fork from here** | Codex 26.212/26.317; Claude VSCode | **Реализовано:** «Fork from here» у ответа (кнопка и меню, `MainViewModel.Fork.cs`, RPC `fork`) | «In new worktree» — не делаем (принцип): `/wt` есть | — |
| **Rewind с картинками** | Claude 2.1.119 | **Реализовано (ParityD):** Rewind (меню и у сообщения) возвращает в поле и текст, и картинки сообщения из его строки транскрипта (`RewindImages`) | Пустой метки нет: omp не отдаёт сообщения только из картинок в `get_branch_messages`; у сообщения вне загруженной страницы истории вернётся только текст. Тест `Rewind_brings_back_the_images_of_the_message_it_starts_over_from` | — |
| **Summarize up to here** | Claude 2.1.32/2.1.141 | **не делаем (принцип владельца)** | Есть Compact и Fork from here; частичная компакция требует upstream и не названа болью | — |

### 1.5 Фоновая работа, режимы

| Функция | У них | У нас | Как сделать лучше | P |
|---|---|---|---|---|
| **Agent map: Stop / Message у каждого субагента, Stop all, failed после краха, живая лента** | Claude VSCode 2.1.269–2.1.290; nornzach Agent Hub | Список, метрики, транскрипт по запросу; Stop нет | `cancel_subagent`/`steer_subagent`; живой хвост через `get_subagent_messages fromByte`; сортировка по состоянию; модель субагента | P1 |
| **Jobs: Stop, полный вывод, «ждёт ввода»** | Claude 2.1.84/2.1.276 | **Реализовано (K6):** список по RPC `get_jobs` вместо разбора текста `/jobs`; Stop (`cancel_job`); «Show output» — хвост, затем только новые байты (`get_job_output fromByte`), в карточке ≤64 K символов; статус «Waiting for input» | Эвристика в движке: running, вывода нет 5 с, последняя строка — prompt (y/n, password, «?», открытая строка на `:`/`>`/`$`/`#`). «Перевести в фон» — нет | P2 |
| **Статус «работает в фоне»** | Claude 2.1.269/2.1.288 | «finished» приходит, хотя субагенты ещё работают | Отложенное уведомление, статус «Working (background)» | P2 |
| **Goal mode** | Codex GA 26.519; Claude 2.1.139 | Notice по `goal_updated`; управление — в терминале | RPC `goal` + полоса цели; между итерациями цели не уведомлять «finished» | P2 |
| **Plan mode** + карточка одобрения плана | Codex 26.210/26.317; Claude 2.1.0+ | В терминале | Upstream: RPC plan mode — в 18.8.0 его нет | P2 (upstream) |
| **Automations / scheduled tasks** | Codex app (Automations, thread automations 26.415) | **не делаем (принцип владельца)** | Планировщик unattended-ходов с хранением задач, missed-run/retry и отдельной политикой approvals — самостоятельный продукт, не заявленная боль командной работы в агенте | — |
| **Auto-review** (вердикт и риск на approval) | Codex 2026-04-23 | Missing | Upstream; GUI-карточка, когда omp отдаст событие | P3 |

### 1.6 Review и Git

| Функция | У них | У нас | Как сделать лучше | P |
|---|---|---|---|---|
| **Review-лента всех diff** + скоупы (uncommitted / staged / branch / last turn), ignore whitespace, «просмотрено» | Codex review pane; pi-gui #178; PIDesktop | **Частично (ParityD):** Changes (⇧⌘G) — список изменённых файлов (git или файлы сессии), по файлу diff с revert hunk и комментариями; жёсткий срез 3000 строк заменён на «Show more» (+3000) | Отдельная лента/скоупы/«просмотрено» — не делаем (принцип владельца): второй review-продукт поверх готового Changes. Тест `Long_changes_count_every_hunk_and_huge_ones_are_not_read_whole` | — |
| **Commit / push / PR** со сгенерированным сообщением, поиск и переключение веток | Codex app | **не делаем (принцип владельца)** | Встроенный git-клиент — тяжёлый продукт; коммит/PR агент делает по просьбе, `BranchChip` показывает ветку | — |
| **PR-бейдж** у сессии и ветки | Codex 26.227; Claude 2.1.217 | **не делаем (принцип владельца)** | Опрос `gh` и сети по каждой ветке без заявленной боли; ветка и URL PR в индекс поиска — тоже | — |
| **Revert hunk прямо в карточке инструмента** | Claude VSCode 2.1.275 | **не делаем (принцип владельца)** | Revert hunk уже в Changes в один клик от карточки (путь открывает файл) | — |
| **Handoff local ↔ worktree, «New chat in new worktree»** | Codex 26.203/26.303 | **не делаем (принцип владельца)** | `/wt` есть; обратный перенос — merge, его делает агент | — |

### 1.7 Файлы, терминал, браузер

| Функция | У них | У нас | Как сделать лучше | P |
|---|---|---|---|---|
| **Вкладки файлов и редактирование** | Codex 26.409/26.707 | Один viewer, только чтение | Редактируемый CodeView: перед записью проверка mtime/хеша (omp пишет параллельно) | P2 |
| **PDF и rich-превью** | Codex 26.204/26.410 | **не делаем (принцип владельца)** — отдельный PDF/document renderer в Files | Не строим второй просмотрщик документов ради паритета; браузерные страницы остаются ответственностью Nightly/K1. Наличие встроенного браузера не означает реализованный PDF-viewer в Files | — |
| **Терминалы по сессии, панель снизу** | Codex 26.410/26.601 | Вкладки глобальные, панель только справа | Привязать вкладки к чату; выбор места панели | P2 |
| **Агент читает терминал** | Codex 26.311 | Missing | `set_host_tools` → `terminal_read` | P2 |
| **Браузер: согласие на действия по сайту** | Claude 2.1.288/2.1.296 | Нет; cookies preview живут между запусками | Карточка «omp хочет действовать на example.com: once / session / always» для click/type/eval не на localhost; чтение и скриншоты без вопроса | P1 |
| **Omnibox** (история + поиск), DevTools, reopen tab | Codex/ChatGPT 26.609/26.727 | Missing | `isInspectable` / `AreDevToolsEnabled` по кнопке; история; ⌘⇧T reopen (сейчас занят Background tasks) | P2 |
| **Allow/block list сайтов для агента** | Codex 2026-04-23 | Только network privacy для процесса omp | Проверка в bridge перед `open` | P2 |

### 1.8 Desktop-интеграция и доступность

| Функция | У них | У нас | Как сделать лучше | P |
|---|---|---|---|---|
| **Single-instance + deep links** `ombra://` | Codex 26.305; Claude 2.1.83 | Single-instance и lease journal реализованы и проверены реальными процессами; runtime session lease — K6 (`session_lease_held`/«Open here»). Deep links — **не делаем (принцип владельца)** | Регистрация URL-схемы на трёх ОС + пересылка аргументов — тяжёлая платформенная работа без заявленной боли; префилл с внешней страницы — лишняя поверхность атаки | — |
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
| **Workspace trust / trust review хуков** | Codex 26.506; Claude 2.1.196–2.1.225 | **Have (fail-closed).** Любая недоверенная папка стартует с `--untrusted-project` (наш флаг движка: без project settings всех провайдеров, project MCP/extensions/hooks/commands/tools/plugins и registry, без project skills, markdown slash-команд, prompt templates и определений субагентов `.omp/agents` (не видны и не вызываются до доверия; AGENTS.md/правила остаются), без `.venv`, без LSP; пользовательские extensions/MCP/tools/skills/субагенты грузятся); движок сам сообщает, что пропустил (`get_state.untrustedProject`), карточка в чате показывает ровно это: «Trust and restart» / «Keep restricted»; доверие по папке + SHA-256 отпечаток конфигурации (изменение → снова спрашивает); `/move`/`/wt` → перезапуск под доверием новой папки; CLI `usage`/`login` вне недоверенной папки; Settings › Permissions › Trusted folders с Revoke (занятые чаты — после конца хода, TUI — карточка закрыть) | Остаток: отпечаток покрывает конфигурацию, не импортируемый ею код; уже загруженный код остаётся в общем движке до его перезапуска | P1 |
| **Плагины: ошибки загрузки в строке, форма опций, состав перед установкой** | Claude VSCode 2.1.284/2.1.285 | **не делаем (принцип владельца)** — расширенный инспектор плагинов | Сохраняем существующие install/enable/update/uninstall и ошибки операций (`PluginsViewModel`); отдельный manifest/schema-инспектор с формами и каталогом runtime-ошибок усложняет менеджер без заявленной боли, не обещаем аудит безопасности пакета до установки | — |
| **MCP: Reconnect all, «0 tools» как предупреждение, OAuth в GUI** | Claude 2.1.284/2.1.288; Codex 0.161 | **Реализовано ранее, перепроверено по коду E:** `ReconnectAllAsync`, `ConnectorState.NoTools` + предупреждение, sign-in/re-sign-in/cancel через `mcp_login` | `ConnectorsSettings` подключает команды; `MainViewModel.Mcp` открывает URL автоматически только для начатого пользователем входа и безопасной схемы. Этот проход — code review, не новый native/OAuth smoke | P2 |
| **Редактор Instructions** (AGENTS.md, APPEND_SYSTEM) | Claude VSCode 2.1.274 | **не делаем (принцип владельца)** — отдельную Settings-IDE правил | Файлы правил правятся существующим редактором K7; дополнительный discovery/editor и отдельная форма system prompt дублируют файловый workflow без заявленной боли. Настройки движка остаются доступны в конфигурации | — |
| **Safe mode** (без расширений, skills, rules) | Claude 2.1.169 | **не делаем (принцип владельца)** — дополнительный режим перезапуска GUI | Отдельная матрица отключения user/project rules, skills и extensions усложняет recovery; сохраняем явную настройку движка и существующий workspace trust. Restricted project не называем safe mode: пользовательские расширения и правила там остаются | — |
| **Usage: лимит с обратным отсчётом, «as of», совет при 90%** | Codex 26.609; Claude 2.1.208 | **Реализовано E, проверка ожидает интегратора:** неудачный refresh сохраняет последний отчёт и явно отмечает устаревание; `As of` показывает timestamp, совет появляется от 80% и при exhausted | `ProviderUsageViewModel` + settings/group views; существующий countdown сброса сохранён. Добавлены 2 regressions: failed→retained→successful-empty и порог совета; тесты в этом проходе не запускались | P2 |

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
| 1 | Невидимые / bidi / zero-width символы подменяют текст в approval и обходят правила | Claude 2.1.211, 2.1.223; ASCII smuggling 2.1.277/2.1.280 | **Реализовано:** approval показывает маркеры и запрещает hidden/control auto-grants; prompt paste/send очищается с уведомлением, сохраняя ZWJ/ZWNJ/variation selectors | Send после удаления требует повторного подтверждения до классификации команд; пустой paste сохраняет выделенный draft. Source attachments не переписываются. Целевые regressions PASS; OS IME/platform gates отдельно | P0 |
| 2 | Обход «Don't ask again» синтаксисом shell | Claude 2.1.145–2.1.223, 2.1.98, 2.1.113 | Exact, prefix и tool-wide grants, включая старые сохранённые, не обходят проверку shell syntax/Unicode, известных wrappers/destructive roots и неоднозначного git; для них требуется новое разрешение | 100 целевых .NET regressions passed вместе с PATH и size-rejection; smoke загрузил старые exact/tool-wide grants и подтвердил отказ для опасных команд, сохранив разрешение git status. Классификатор не анализирует тела скриптов и не является sandbox | P0 |
| 3 | UNC / `\\?\` / `file://host` по наведению → утечка NTLM | Claude 2.1.233, 2.1.234, 2.1.247, 2.1.271 | **Реализовано, beta R0.3:** `LocalFilePaths` отклоняет сетевые/device-формы до обращения к ФС во всех клиентских точках | Нативный прогон на Windows; сетевые include в конфигурации Git этим не закрыты | P0 |
| 4 | Исполнение кода недоверенного репозитория нашими git-вызовами | Claude 2.1.265 (clean filters) | **Реализовано, beta R0.3:** filter-драйверы перечисляются и отключаются на время запуска; fail-closed при некорректном UTF-8, лимитах и таймауте; подтверждено на собранном клиенте | Не sandbox от одновременного изменения конфигурации; worktree с submodules удаляются только в терминале | P0 |
| 5 | Код внедряется в TCC-привилегированное macOS-приложение | Codex 26.924 (CVE-2026-100754) | **Реализовано, beta R0.3:** hardened runtime во всех ветках; `StartupHookSupport=false`; injection через startup hook и DYLD подтверждена до и отсутствует после | Ad-hoc/self-signed — с исключением library validation; полная защита требует Developer ID и notarization | P0 |
| 6 | Случайное одобрение лишним нажатием, кликом или фокусом | Claude 2.1.252, 2.1.280–2.1.290 | **Реализовано:** keyboard guard и pointer press/release identity на одной settled карточке, 500 мс, защита overlays/double-click; Deny без задержки | Headless regressions и native macOS routed-pointer smoke PASS; физический OS input и Windows/Linux ещё требуют приёмки | P0 |
| 7 | Два писателя в одной сессии (второй GUI, вкладка omp TUI, CLI) | Claude 2.1.246–2.1.259, VSCode 2.1.284–2.1.287; pi-gui lease | **Реализовано для кооперативных клиентов:** существующий OS lease fail-closed, удержание до close/drain; GUI delete через shared host; защита GC и вложенных журналов; явная новая identity при fork. Клиент показывает чат другого держателя только для чтения и передаёт владение по «Open here» (`engine_take_session`, ожидание до 15 с, отказ с holder) | macOS: 8 real-process regressions PASS, отдельный shared-host delete smoke PASS; клиентский lease UI — headless/FakeOmp. Linux/Windows native gates открыты; legacy writers без lease не покрыты | P1 |
| 8 | Потеря очереди при крахе / рестарте / прерывании | Codex 0.160 #49105, 0.162 #50087 | **Реализовано, beta R0.2:** queue journal и восстановление текста/images в composer; Stop → `abort_and_restore_queue` | При lost acknowledgement восстановление требует проверки пользователем, без auto-resend; exact-once требует runtime identity/ack | P0 |
| 9 | Модель восстанавливаемой сессии недоступна → не открывается | nornzach 0.9.16; omp 18.6.3 «Could not restore model» | **Реализовано:** typed `session_model_unavailable`, имя сохранённой модели, отдельные Retry и «Open on current model»; согласие одноразовое, история и несохранённый ввод сохраняются | 66 .NET recovery/session regressions и 16 Bun factory/host/isolation tests passed; native macOS окно с настоящим host подтвердило retry без изменения журнала и явное восстановление без отправки черновика. Hosted provider/OAuth не проверялись | P1 |
| 10 | Resumed-сессия меняет модель | Codex iOS 1.2026.202 | **Исправлено в shared runtime:** обычный resume игнорирует launch model preferences; замена только с `resumeOnCurrentModel`, затем сохраняется в журнале | Реальная factory проверяет сохранённую модель, отказ при недоступной last-used role, явную замену и последующий resume без подмены новым default | P2 |
| 11 | Slash-команда во время хода уходит модели текстом | Claude 2.1.83, VSCode 2.1.275 | **Реализовано:** FIFO builtin до готовности, затем `prompt`; admission различает исходный ввод и вложенную агентную работу | Native shared-engine smoke + consumer regressions; recovery без auto-send | P1 |
| 12 | Набранный ответ теряется, когда вопрос отозван | Codex 0.157 #47422, 0.162 #50052 | **Реализовано, beta R0.2** для наблюдаемого revoke/cancel/send failure | См. 1.2: remote acceptance race остаётся runtime prerequisite | P1 |
| 13 | Черновики теряются при краше и выходе | Codex 26.506/26.908; Claude 2.1.208 | **Реализовано:** локальный atomic journal (R0.2), cross-instance lease (R0.3); debounce `DraftStore.Debounce` = 500 мс, каждый ввод перезапускает окно, Send/close/switch делают немедленный flush | Тест на fake clock `Typing_is_journaled_within_the_debounce_bound_measured_on_a_fake_clock` (en/ru PASS): при SIGKILL теряется ≤0,5 с набора. Windows/Linux OS gates открыты | P1 |
| 14 | Большие сессии: медленно / пусто / не открываются | Claude 2.1.116–2.1.290; Codex 0.158–0.162; iOS | **Реализовано:** tail-first `get_messages_page`, prepend и согласованный cursor recovery без монолитного fallback | Native 360 сообщений, уникальность/порядок и drift 0 px; 10k/performance/другие ОС ещё требуют приёмки | P1 |
| 15 | Транзиентная ошибка ФС стирает список сессий | Claude 2.1.294; Codex 0.162 #51230 | **Реализовано:** явная полнота scan, merge частичных результатов, сохранение unreadable rows, root isolation, notice/Retry и ограниченный backoff; cap 500 не авторитетен для удаления | Catalog/session/state regressions: 77 passed, 0 failed; полная платформенная приёмка остаётся общей задачей | P1 |
| 16 | Сироты процессов после краха GUI (Windows) | Claude VSCode 2.1.153, 2.1.296; Codex 0.161; unkeyn, pi-desktop, piggy | **Реализовано, native gate открыт:** Windows job `KILL_ON_JOB_CLOSE` до legacy migration и запуска children; независимый updater/external launch | Независимый review исправил порядок admission/ownership. Четыре Windows-only проверки пропущены на macOS; не считать нативным PASS | P1 |
| 17 | Короткий PATH при запуске из Finder (MCP `npx`/`uvx`, LSP не находятся) | dylansantwani, PiPilot 0.6.0 | **Реализовано:** один bounded login-shell probe на старте; импортируется только PATH, timeout 3 с, остальные переменные и диагностический вывод shell не импортируются | Целевые .NET regressions passed; настоящий `/bin/zsh` smoke восстановил PATH. Explicit launch environment имеет приоритет; Linux native/Finder launch отдельно | P1 |
| 18 | Частичный ответ пропадает при error/abort | Claude 2.1.179/2.1.199 | **Исправлено:** короткий финал при error/aborted больше не стирает полученный текст и thinking; сохранён stop reason, успешный финал остаётся авторитетным | Regression воспроизвёл потерю до исправления; 30 ConversationState tests passed, включая поздний финал после EndRun и следующий ответ. Отдельный reducer smoke сохранил текст при пустом error-финале; native retry smoke (#19) визуально подтвердил сохранность partial text/thinking | P2 |
| 19 | Retry оставляет полусообщение или пустую карточку; каждый retry — постоянный warning | Claude VSCode 2.1.260, 2.1.181 | **Исправлено:** одна обновляемая informational-строка retry; исчезает при success/завершении хода, при исчерпании остаётся terminal error. Пустая failed assistant-строка текущего хода удаляется при retry; partial text/thinking сохраняются | Два regression воспроизвели дублирование и пустую карточку; 33 ConversationState tests passed. Reducer smoke и native Avalonia smoke: проверены шесть состояний настоящего MainWindow, обновление той же строки, success, exhausted error и сохранность partial text/thinking; снимки окна визуально просмотрены. События в native smoke синтетические, без инъекции ошибки настоящего provider/RPC; Windows/Linux не проверены | P2 |
| 20 | Переоткрытая сессия выглядит иначе, чем живая | Claude VSCode 2.1.283–2.1.285 | **Частично исправлено:** общая live/history-политика для видимых custom/hookMessage, summary, fileMention и execution-сообщений; `display=false` скрыт, представлены timestamp/images, reload без дублей. Labelled context не завершает ожидание вывода slash-команды | 127 целевых regressions passed; настоящий pinned-runtime resume/get_messages и native macOS снимок контекста/изображений проверены. Остались durable approve/deny/answered (таких persisted AgentMessage-ролей нет), неперсистентный upstream IRC relay, стабильные id/time и полная live/history/platform приёмка. Native UI подключался после controller startup: stock installed host не знает `editor_trust`, полный UI-startup требует совместимого runtime | P2 |
| 21 | Сон / пробуждение: стрим «висит», часы прыгают | Claude 2.1.126–2.1.290 | **Реализован lifecycle; платформенная приёмка открыта:** window-owned NSWorkspace / Windows power callbacks / Linux logind, сверка всех live-чатов, stale-response guards и post-wake watchdog. Read-only исключён; нет replay/abort/restart. Длительности монотонны. Review исправил false Running при idle + queue и тихую потерю D-Bus subscription | 210 tests passed, 1 real-omp scenario skipped. Native macOS: программные NSWorkspace sleep/wake → настоящий host `get_state` active/background, context 87 → 0, no replay, dispose. В окне `1m 05s` не меняется при тестовых UTC jumps; idle + queue остаётся Ready (synthetic backend). Linux adapter сообщил разрыв локального тестового D-Bus socket; физический сон и Windows/Linux native не проверены | P2 |
| 22 | Устаревший список аудиоустройств → запись тишины | Claude 2.1.195 | **Исправлено в коде:** capture и self-test владеют согласованными initialize/terminate leases; после последнего закрытия следующая запись заново перечисляет устройства. Нет samples / цифровая тишина дают отдельную ошибку без изменения черновика; тихий ненулевой сигнал не отбрасывается | AudioLifecycleTests и DictationModeTests прошли, включая overlapping leases, ошибки initialize/terminate и float.Epsilon. Native macOS smoke: три цикла перечисления, 11 устройств, default input 9; запись не запускалась. Физические unplug/replug/sleep/wake и Windows/Linux остаются открыты | P2 |
| 23 | Esc во время IME-композиции отменяет ход; CRLF при вставке | Claude VSCode 2.1.178, 2.1.119 | **Исправлено для TextBox preedit:** текущий TextInputMethodClient резервирует Esc/Enter/Up/Down/Tab до app shortcuts; завершение композиции или смена фокуса снимают защиту. Существующая нормализация CRLF при paste/send сохранена | CompositionInputTests и смежные input/UI regressions прошли. Native macOS окно: routed client preedit не отправляет/не отменяет ввод; commit заменяет выделение на 日本 с сохранением caret. Это не физический OS IME: японский/Pinyin, Windows/Linux и остальные редакторы требуют отдельной приёмки | P2 |
| 24 | Сообщение больше лимита строки RPC теряет вложения | Claude 2.1.136/2.1.205 | **Реализовано:** validation до очистки; явная кнопка Send text as file сохраняет полный UTF-8 text/pastes/comments в private durable file рядом с journal и отправляет bounded @file + images. Write/validation/journal failures сохраняют ввод; queued recovery возвращает одну ссылку/набор images без auto-send | 85 целевых tests passed вместе с browser grants. Native macOS: 600 000 кириллических символов, настоящая кнопка, полный файл/0600, bounded delivery и реальный runtime fileMention parser; без provider. Review исправил двойной queue recovery и непредставимые quote/backslash paths; native отказ до записи и failed-checkpoint recovery прошли. Файлы удаляет пользователь; runtime preview ограничен. Windows/Linux native открыты | P2 |
| 25 | Секреты в UI и в share | Claude 2.1.234–2.1.296; Codex 2026-08-20 | **Частично исправлено:** regex `SecretRedactor` используют `NonBacktracking`; URL-пароли с `@` и quoted `--api-key` маскируются целиком. Маскирование подключено к connector Detail (remote/stdio) и marketplace Source, без изменения operational config. Share-consent больше не обещает redaction до чтения настроек; учитывает `share.redactSecrets` и `secrets.enabled` | Утечки URL/quoted argument воспроизведены до исправления; 54 Diagnostics/ConnectorsFlow tests passed. Native Settings: masked connector/marketplace labels. Pinned `shareSession`: loopback upload → AES-GCM decrypt/gunzip, configured secret скрыт в user/tool output, opaque payload удалён, исходная сессия сохранена, ключ только в fragment. Ложное обещание share-consent воспроизведено в native окне до исправления; после проверены unknown/off/on с synthetic CLI settings, 14 SessionArea tests passed, 2 real-runtime tests skipped. Внешней публикации не было. Сквозной GUI upload, default-pattern cases и остальные UI-поверхности ещё открыты | P2 |
| 26 | ANSI escape в status / widget / notice / выводе команд | Codex 0.161/0.162; Claude VSCode 2.1.172 | **Исправлены presentation-пути:** видимые C0/C1-маркеры в Notice/CommandOutput/Tool, Background Tasks, extension widgets, status/detail, model/tool activity и startup error. CSI/OSC инертны; CR/LF/Tab и обычный Unicode сохранены, raw transcript/runtime не переписывается | 209 интеграционных tests passed, 1 real-omp scenario skipped. Native macOS failing-before/passing-after: MainWindow widgets/status показывают ESC/OSC/BEL/C1 как маркеры; Japanese/ZWJ emoji/newline/tab и источник сохранены, снимок просмотрен. Цветной SGR-рендеринг не добавлялся; Windows/Linux native не проверены | P2 |
| 27 | macOS TCC-запрос «данные других приложений» при обходе `$HOME` | Claude 2.1.295 | **Частично исправлено:** macOS index root, равный `$HOME` или являющийся его предком, ограничивается файлами первого уровня; рекурсивный `git ls-files` не запускается, индекс помечается неполным. Вложенные папки, включая `Library`, `Containers` и `*.photoslibrary`, не обходятся | Тест с синтетическим home проверил root `$HOME`, ancestor root, отсутствие обхода вложенных/защищённых папок и `Truncated=true`; не macOS native и не подтверждает поведение TCC/Finder | P2 |
| 28 | Файл настроек отвергается целиком из-за одного неверного поля | Claude 2.1.121/2.1.199 | **Исправлено, проверено на macOS:** кэшируемые метаданные writable-полей; вычисляемые свойства не сериализуются, старые игнорируются. Неверные поля/null-required collections используют defaults, raw JSON и unknown keys сохраняются. При read-only папке локальные изменения живут в памяти, последующие Load объединяют их с внешними правками; Update повторяет сохранение с `.bak`. Видимое предупреждение без значений секретов | Исправлена воспроизведённая регрессия thinking save/reload (`Property set method not found`). 181 смежный тест прошёл. Независимый native smoke с chmod read-only: предупреждение в Settings просмотрено, thinking сохраняется в памяти, внешние theme/profile не теряются, после восстановления прав запись работает. Windows/Linux native не проверены | P2 |
| 29 | Конкурентная запись конфигов (между процессами), фиксированные `.tmp` | Claude 2.1.259/2.1.274; Codex 26.602 | **Исправлены writer-пути и freshness browser grants:** PID+GUID temp/atomic replace; каждый permission read перечитывает backing file без timestamp-cache. Missing/malformed/unreadable deny; failed Forget запрещает локально и сохраняется перед следующей успешной mutation; несохранённый grant не разрешает | Существующие concurrent-writer regressions сохранены. Настоящий revoke-subprocess: старый экземпляр больше не разрешает камеру; deletion/malformed/failed revoke и частичный regrant проверены smoke. Независимый security review не нашёл дефектов. Windows/Linux native не проверены | P3 |
| 30 | Обновление на Windows через `powershell.exe` 5.1 (GPO/EDR) | Claude 2.1.212/2.1.221 | **Исправлен Windows apply-путь; native-приёмка открыта:** app-owned `--apply-update` запускается из отдельной копии приложения/runtime, без PowerShell. Проверяются ownership, staged receipt и пути; замена начинается после выхода исходного PID/start-time. Transient locks повторяются до 60 s на операцию; rollback сообщает фактический результат | 123 смежных теста passed, 3 Windows tests skipped. Настоящие Prepare и helper subprocess на macOS создали рабочую копию app/runtime, дождались fixture parent, заменили disposable payload, сохранили previous, удалили staging и записали success. Fixture receipts не доказывают signed-package delivery. Windows locks/Job Object/GPO/EDR и packaged relaunch ещё не приняты | P2 |
| 31 | Мигание консольных окон на Windows | Claude VSCode 2.1.273; Codex 0.159–0.160 | **Исправлен известный путь:** `ConnectionCheck` запускается с `CreateNoWindow=true`, как существующий engine launcher; отдельная абстракция ради одного поля не добавлена | Настоящий Bun subprocess на macOS выполнил loopback-запрос и вернул Works/204. Отсутствие мигания окон на Windows ещё требует native-приёмки | P2 |
| 32 | Суррогатные пары и эмодзи режутся | Codex 2025-05-22; Claude 2.1.132–2.1.218 | **Исправлено в presentation-путях:** общий `GraphemeText` округляет UTF-16 budgets внутрь до целых grapheme clusters; `DiffWords` и подсвеченные runs сохраняют cluster boundaries. Мигрированы transcript head/tail, задачи, approvals, previews, annotations, названия/уведомления и видимые ошибки. Payload/byte limits не менялись | 306 смежных тестов прошли, 3 real-runtime сценария skipped. Независимый smoke: 13 548 сравнений границ с StringInfo (RI/Prepend/ZWJ/CRLF), целые emoji spans и просмотр нативного DiffView на macOS. Suffix после LF не сканирует весь отброшенный префикс; без безопасного delimiter возможен O(n). Windows/Linux shaping и повреждённый исходный UTF-16 не покрыты | P2 |
| 33 | NFC/NFD в fuzzy-поиске (macOS) | Codex 0.156 #45475 | **Исправлено, платформенная приёмка открыта:** NFC keys кэшируются при индексации; запросы, `@` folder prefixes и Changed/Session filters нормализованы; исходные пути не переписываются и не склеиваются. На интеграции обнаружен `InvariantGlobalization=true`: Normalize был no-op; включена обычная globalization, в package self-test добавлен реальный canonical lookup | Native macOS: NFC résumé нашёл NFD-файл, открыл исходное содержимое и сохранил путь (Git fixture с precomposeunicode=false). App `--self-test` прошёл. После исправления editor context-menu и model-choice race: 177 смежных тестов passed, 2 real-runtime skipped. Полный suite ранее превысил 600 s и не засчитан. Linux требует системную ICU; Linux/Windows package smoke открыты | P3 |
| 34 | Огромные markdown-таблицы и строка 60 KB в diff | Claude 2.1.208, 2.1.246 | **Ограничено отображение:** таблица до 100 строк/16 столбцов, oversized cell до 1024 UTF-16 units; diff до 4096 на строку перед syntax/layout; plain fallback до 16384. Границы grapheme-safe, есть маркеры и копирование оригинала; исходные данные и file-open номера сохранены | 123 смежных теста passed, 3 Windows tests skipped. Native macOS: таблица 2000×4 создала 400 ячеек; wide/long-cell, 60 KB/ZWJ/tab-heavy diff и >200000 plain fallback проверены с полным OS clipboard copy. Markdig по-прежнему парсит целиком вход до 200000 символов; Windows/Linux rendering не проверены | P2 |
| 35 | Worktree, открытый в другом чате, удаляется | Claude 2.1.247 | **Исправлено для GUI:** все hosted chats, вложенные cwd и symlink paths защищены; повторная проверка при confirm. Operation-scoped reservation блокирует admission в удаляемый subtree и новые команды/ввод до завершения Git. Удаление ждёт idle/пустых очередей всех чатов. Missing worktree очищается адресным `remove`, не общим `prune` | На ревью устранена гонка после dispatch перед Git spawn. Детерминированные тесты проверяют admission во время paused removal и освобождение при успехе/ошибке; входят в 306 passed. Native macOS + настоящий Git: stale confirm отказал после открытия фонового чата, после закрытия удаление прошло. Чаты smoke не запускали engine. Внешние процессы/изменения FS не сериализуются; Windows/Linux native не проверены | P3 |
| 36 | Папка сессии удалена | Claude 2.1.236/2.1.239 | **Реализовано:** явный выбор текущего проекта/существующей папки либо read-only; отмена и ошибка сохраняют прежний чат и черновик. Read-only использует каноническую ветку и страницы через root host без логической сессии, project discovery и writer; изменившийся источник требует reload | 109 смежных .NET tests и 20 Bun tests passed. Настоящий source host: 205 сообщений, исключение abandoned branch, stale-cursor/reload, неизменность исходного файла. Native macOS: выбор/отмена, banner, disabled composer и последующий writable resume в явно выбранном проекте; без prompt/provider. Manifest перегенерирован/check passed. Windows/Linux native-приёмка открыта | P3 |
| 37 | Индикаторы после compaction устарели | Claude 2.1.15–2.1.83 | **Частично исправлено:** auto_compaction_end refresh дополнен session_settled и terminal manual command_output (включая intercepted output). Coalesced get_state ждёт предшествующие события и отвергает устаревшие ответы/смену сессии; не выводит Ready из индикаторов. RunSlashCommandAsync закрывает capture под тем же lock, что и запись: поздний output попадает в transcript, а не теряется | 177 смежных тестов passed, 2 real-runtime skipped. Настоящий pinned host: empty-session /compact вернул «Compaction failed: No model selected»; terminal output сохранён, затем выполнен автоматический get_state. Synthetic end/settled + настоящий get_state восстановили context 87 → 0. Успешное сжатие с history/provider не проверено; пункт открыт | P3 |
| 38 | Стек на раскладке дерева в 10k+ узлов | nornzach 0.9.17 | N/A, пока нет дерева | Только итеративные обходы при реализации `get_tree` | — |

Приёмка 2026-10-16 выявила дополнительную гонку выбора модели после локальных команд: UI `Phase` мог ещё быть
`Running`, хотя контроллер уже `Ready`. `ChooseModelAsync` теперь проверяет состояние выбранного контроллера.
Детерминированный regression падал до исправления и прошёл после; native macOS smoke прошёл с synthetic backend.
Контекстное меню DocumentEditor проверено в нативном окне: right-click/Ctrl-click сохраняют выделение, Copy
использует OS clipboard, Cut/Paste меняют буфер без записи на диск, read-only запрещает изменения.

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
| O1 | Дельта-стриминг `set_event_filter` | upstream 18.4.5 #13716; nornzach | Реализовано, real wire и consumer gates | §0 и NEXT_RELEASE_ROADMAP | линейный трафик; не обещание ускорения модели | P1 |
| O2 | Кэш каталога сессий по (path, size, mtime), сохраняемый на диск для тёплого старта, + watcher или reconcile при фокусе | pi-desktop `SessionIndexCache`; OMPiUI `omp-catalog.ts`; nornzach | Реализован private profile/root-scoped sidecar, metadata invalidation, partial-scan retention и reconcile при фокусе; повреждённый кэш перестраивается | 256 журналов: повторные scans 4,7/1,9 мс вместо 18,3/17,8 мс; новый процесс прочёл summaries без открытия заблокированного журнала. Native macOS focus обновил external edit/add/delete, сохранив draft. Полнотекстовый индекс/дашборд расходов остаются отдельными требованиями | меньше повторного I/O; cold scan 45,8 мс, не заявлено ускорение первого заполнения | P1 |
| O3 | Постраничная история + мгновенный первый кадр | piggy; nornzach; OMPiUI | Tail-first и prepend реализованы, native drift 0 px | Не подменять согласованную runtime-историю несверенным хвостом с диска; отдельно измерить cold/10k budget | цель <1 с ещё не доказана | P1 |
| O4 | Авто-recover после краха: backoff 0,5 → 30 с, лимит попыток, сброс счётчика после стабильной работы; `--restore` | nornzach, dylansantwani; omp 18.10.3 | **Реализовано (клиент):** `SessionController.AutoRecover.cs` — после неожиданного выхода тот же session file переоткрывается через 0,5/1/2/4/8/16/30 с, не более 7 попыток подряд; 2 мин стабильной работы сбрасывают счётчик. Stop, Cancel, закрытие, рестарт по настройкам и любой явный restart отменяют авто-restart. Чаты общего движка проходят через startup admission: первый поднимает процесс, остальные открываются в нём или принимают его неудачу (один старт процесса на попытку). Прерванный ход и очередь не переотправляются; черновик остаётся в композере. Карточка Recover показывает обратный отсчёт с «Restart now»/Cancel, после лимита — обычный ручной Recover. `--restore` runtime не используется | Тесты на инжектируемых часах (`AutoRecoverTests`, `AutoRecoverUiTests`): backoff, лимит, сброс, Stop/Cancel/close, два чата — один рестарт процесса | ход переживает краш без повторной отправки | P1 |
| O5 | Уровни timeout'ов RPC | piggy a0185c3 | Реализовано: 40 с ordinary, 120 с history/page/tree/entries, включая root read-only; явный request/config timeout имеет приоритет | Startup bound отделён от чтения истории; abort/wake и другие явные limits сохранены. Настоящий pipe принял history через 45 с и cold response через 32 с. Review исправил блокирующий read-cancel cleanup; actual held-writer smoke подтвердил отмену без потери live chat. Итоговая выборка O2/O5: 162 passed, повторный review без замечаний | длинное чтение не ограничено прежними 30 с; provider run не получает новый timeout | P2 |
| O6 | Выгрузка ресурсов idle логических сессий по замерам; сохранять снимок, черновик и якорь | piggy idle_timeout, GiorgioTassoni `idle.rs` | Старый LRU процессов удалён при shared-engine cutover; политика idle-ресурсов ещё не реализована | Не завершать общий host ради одной сессии; сначала измерить retained resources и recovery | измеренная память, без обещания сотен МБ на чат | P2 |
| O7 | Убрать spare-omp при переходе на общий движок | уточнение владельца D2 | Удалён; native две сессии в одном PID, Stop/close/crash recovery проверены | Один host, отдельные логические сессии, без скрытого per-chat процесса | нет дублирующего idle runtime | P2 |
| O8 | Схлопывать промежуточные `tool_execution_update` одного `toolCallId` | nornzach event-batcher; OMPiUI liveToolOutput | **Реализовано:** `ToolUpdateReader` схлопывает накопившиеся подряд text-only снимки с тем же `toolCallId`/`toolName`/`args`; details/media/неизвестные поля, start/end, delta и response/Intercept-барьеры сохраняются, `Sequence` остаётся последней исходной позицией | Все кадры по-прежнему разбираются: экономится число доставок в reducer при backlog, а не JSON-разбор. Fixture `tool-flood` (2048 снимков, два чата с общим id) через настоящий pipe; независимый review без замечаний | меньше работы reducer на шумных инструментах | P3 |
| O9 | Строки по `messageId` | upstream docs/rpc.md; OMPiUI #109 | **Реализовано:** словарь `messageId → row`, fallback на `_streamingAssistant` без id | §0 | Targeted tests (дельта A после старта B → строка A) + native `message-id` | P2 |
| O10 | Живой субагент: `get_subagent_messages fromByte`, коалесинг 50 мс | OMPiUI | **Реализовано:** byte-cursor delta, последние 40 записей; одна UI-пачка на ответ без отдельного 50-мс таймера | Опрос раз в 1 с после opt-in только для видимой развёрнутой карточки; collapse/offscreen/pane/chat/dispose отменяют чтение; reset/path change начинают новый preview | Настоящий runtime reader: partial UTF-8/newline, empty poll, reset; native macOS: append без дублей, stop/reopen; независимый review без блокеров | P2 |
| O11 | `get_state.usageLimit` вместо запуска `omp usage --json` | omp 18.6.3 #14153 | **Реализовано:** badge exceptional stage активного аккаунта; это не полная квота | CLI только в Settings; `/fresh` принимает подтверждённую смену ID; login/logout очищают limit и отсекают старые ответы; post-reset refresh сохраняется при уходе со страницы | Native macOS RPC badge/popover и Settings прошли; три замечания review воспроизведены, исправлены и закрыты повторным review | P3 |
| O12 | Перф-бюджеты в CI: стрим 60 fps; 10k сообщений открываются <1,5 с; 30 мин стрима — дрейф памяти <5% | piggy perf-run; pi-gui #114 | **Реализовано:** `tools/verify/performance` на настоящем общем движке и stand-in модели. Тёплое открытие 10k истории (gate) отдельно от холодного (старт движка + JIT, только отчёт); compositor-кадры через custom draw probe; поминутный тренд памяти. На macOS gate по `phys_footprint`: RSS там прыгает 222–540 МБ из-за компрессора при ровной куче | Native macOS 30 мин: тёплое открытие 1205 мс, 97,2 fps, p95 15,4 мс, 38 104 RPC-кадра без зависаний, footprint −11,7%, куча 350→350 МБ. Headless perf-lite (30 с) добавлен в `tools/ci/real-omp-smoke.sh` ручного workflow; включение PR-триггеров — решение владельца по оплате Actions. Windows/Linux native не замерены | регрессии ловятся до релиза | P2 |
| O13 | Сворачивать длинные code block, diff, thinking (превью N строк) | Orbit; piggy rowMemory | **Реализовано для code block:** >28 строк → превью 24 + «Show more/Show less»; копирование всего кода, включая скрытый дописанный хвост; раскрытие привязано к позиции и переживает stream/theme/layout; ленивые placeholder оценивают вложенный (quote/list) код и абзацы по состоянию каждого блока | Native macOS: 4000 строк в quote — оценка 572px против фактических 570px (до исправления 18018px), настоящий clipboard содержит все строки. Diff и thinking этой партией не менялись | дешевле layout | P3 |
| O14 | Стриминговый Markdown: удерживать незакрытый inline-синтаксис (`**`, `` ` ``, `[x](http…`) | Ompcot `remend`, nornzach | Неясно | Скрывать хвост незакрытой ссылки или выделения до закрытия | нет мигания сырого Markdown | P3 |
| O15 | Windows: срезать `\\?\` и пробелы в пути к `--extension` для Bun | Ompcot, piggy 7ba03b2 | Не проверено | Проверить на профиле с пробелом в имени | стабильный старт | P3 |
| O16 | Координировать общие изменения runtime/plugins и новые логические сессии | pi-heao-gui spawn-queue | Shared-engine restart требует all-idle; гонки новых opens и повторных settings changes проверяются отдельно | Зафиксировать поколение конфигурации и не допускать старта на несовместимом живом host; аудит общих plugin installs сохраняется | согласованная конфигурация без потери работающих чатов | P3 |
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
3. ~~RPC отмены и вывода background jobs~~ — сделано в собственном движке: `get_jobs`, `cancel_job`, `get_job_output` (byte cursor, ≤256 KiB за чтение, движок хранит последний 1 MiB вывода job). Открыто: «перевести в фон».
4. MCP OAuth login/reauth и elicitation через `extension_ui_request`.
5. Входящий `rpc_chunk` от клиента, чтобы снять лимит строки 1 MiB (3 картинки × 230 KB).
6. ~~Нативный lock или lease файла сессии~~ — сделано в собственном движке: holder (`ombra`/`omp`, pid), `session_lease_held`, `session_lease_lost`, root `engine_take_session`.
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
- automations — **не делаем (принцип владельца)**: отдельный unattended-планировщик (§1.5);
- remote по SSH — **не делаем (принцип владельца)**: отдельный удалённый GUI-runtime; SSH-инструмент остаётся (§1.3);
- импорт — **не делаем (принцип владельца)**: отдельный мастер миграции чужих историй; терминал omp остаётся (§1.3);
- Mermaid и LaTeX — **не делаем (принцип владельца)**: отдельные движки rich-рендера (§1.1).

**Волна F:** выбрать и проверить конкретный совместимый артефакт собственного движка после real-runtime gates; историческое «≥18.10.3» не является автоматическим разрешением апгрейда или доказательством recovery O4.

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
| **Одна обратная связь на копирование** | **Реализовано**: общий awaitable clipboard path; успех только после записи, одна видимая ошибка, reply привязан к своей строке/окну, а не IsActive | `ConversationMenuTests`, `ContextMenuUiTests`; ✓ code/tool и brief для меню | A C Cl G | P1 |
| **Одна система меню по правому клику для путей** | **PASS headless, 2026-10-18**: `ContextMenus.ForPath`; пути ответа, tool-path, turn-file и diff используют одно меню | `PathContextMenuTests`: right-click, Copy absolute/relative, Add to message; ошибки clipboard/open видны | A C Cl G | P1 |
| **Токены движения** | **Реализовано**: `GuiMotionFast` 120 мс / `GuiMotionBase` 150 мс, `CubicEaseOut`, reduced motion обнуляет длительности и Send scale | `VisualMotionTests`; контракт в DESIGN_SYSTEM; Linux OS probe отдельно | A G Cl | P2 |
| **Запоминание UI-состояния** | **Реализовано**: normal bounds, maximized, прижатие к screen working area/DPI, ширины/высоты и видимость панелей | Сохраняется при закрытии; minimized не затирает normal/maximized; восстановление Terminal не запускает shell. `MicroUxStateTests` | A C Cl G | P1 |
| **Одни подписи шорткатов** | **Реализовано**: platform-aware `KeyboardShortcuts`, включая terminal labels и новые действия | Отдельная колонка клавиш у действий без жеста **не делается (принцип владельца)**: не создаём фиктивные хоткеи ради оформления | C Cl G | P2 |
| **Локализация хвостов** | **Реализовано**: clipboard/image/dialog feedback через Loc; `Loc.Plural` использует ru one/few/many (целочисленные CLDR), включая 11–14, 21–24 и отрицательные | `PluralFormsTests`, en fallback и пользовательские языки сохранены; ru формы всех текущих plural callsites | A C | P2 |

### 6.2 Меню по правому клику (ПКМ)

| Где | Что должно быть | Сейчас | Сигнал | P |
|---|---|---|---|---|
| Строка и карточка инструмента | Copy command, Copy output, Copy path, Open in Files, Reveal; Copy patch | **Реализовано**: menu всей карточки + существующие Copy/✓ и `ForPath`; вложенное меню пути не перехватывается | A C Cl G | P1 |
| Путь в тексте ответа | см. 6.1 `ForPath` | **PASS headless**: `A_path_in_the_reply_opens_the_path_menu_and_copies_its_path_for_real` | A C Cl G | P1 |
| Ссылка | Open in Preview (HTTP/localhost), Open in browser, Copy link; полный URL tooltip | **Реализовано**: `ForLink`, `MicroUxTabTests`; обычный/модифицированный hyperlink сохраняет системное открытие, Preview — явный пункт меню | A C Cl G | P2 |
| Картинка (в сообщении и chip вложения) | Copy image, Save as…, Open in default app, Reveal | **Реализовано**: общий image menu; bytes, picker cancel/errors и временная копия для системного Open/Reveal покрыты `ConversationMenuTests` | A C Cl | P2 |
| Строка сессии | Mark as read/unread, Copy session ID, Reveal, Open in terminal | **Реализовано**: explicit unread сохраняется в settings; open/read очищает; статусы waiting/running/error приоритетнее unread | C Cl G | P2 |
| Вкладки Preview и Terminal | Close, Close others/right, Copy URL, Reload, middle-click; truncation и tooltip | **Реализовано**: общие tab menus, отдельная полоса Browser rail, scoped ⌘W; permanent browser tab закрывает страницу, не окно | A C G | P2 |
| Терминал | Copy selection/all, Paste, Clear, Open/Copy ссылки | **Реализовано** для выделенной ссылки и Ctrl-click URL; ПКМ никогда не вставляет в shell. Собственный URL hit-test по пикселям **не делаем (принцип владельца)**: не форкаем renderer ради второго способа выбрать ссылку | G | P2 |
| Сообщение в очереди, chip Pasted text | Edit, Send now, Remove / Preview, Remove | **Реализовано**: `ConversationMenus`, сохраняет draft и read-only guard | A G | P2 |
| Ошибка ответа, notice | Copy error | **Реализовано**: Copy details, Retry где разрешён, Show in Events | C | P2 |
| Event log | Выделяемые строки, Copy line/all, Clear | **Реализовано**: selectable rows и menu контейнера/строки | A | P3 |
| Блок кода | Insert into message | **Реализовано**: вставляет у каретки, сохраняет draft, недоступно без writable composer | G | P3 |
| Выделение в ответе | Quote in message, Comment | **Quote реализован**; transcript Comment **не делаем (принцип владельца)**: новая модель устойчивых text-range anchors не нужна для цитирования; K4 code/diff/page comments остаются | C | P2 |

### 6.3 Hover, pressed, курсор, тултипы

| Что | Сейчас | Сделать | Сигнал | P |
|---|---|---|---|---|
| Сплиттеры панелей | **PASS headless**: захват в 7 px и double-click reset (`MicroUxUiTests`), `MainWindow.Splitters.cs` | Реализовано; native multi-monitor drag отдельно | A | P1 |
| Кнопки действий у строк (`row-action`, `session-action`, copy блока кода) | **Реализовано**, `VisualMotionTests` | Opacity 120 мс; постоянно видимый Copy не прячется | G | P3 |
| Шевроны | **Реализовано**, общий motion token | Поворот 120–150 мс, reduced motion | A Cl G | P3 |
| Клик по фону заголовка diff | **Реализовано** | Сворачивает; path/copy/right-click не перехватываются | C | P2 |
| Alt-клик по шеврону | **Реализовано на модели** | Все карточки хода, включая unrealized; соседние ходы не меняются | C | P2 |
| Строка таблицы в Markdown | **Реализовано** | Hover всей body-row без перехвата выделения | G | P3 |
| Send | **Реализовано** | `scale(0.94)`; без scale при reduced motion | G | P3 |
| Ручка сайдбара с клавиатуры | **Реализовано** | ←/→ ±20 px, Home default, End max; Tab/focus ring и persistence | G | P3 |

### 6.4 Обратная связь и состояния

| Что | Сейчас | Сделать | Сигнал | P |
|---|---|---|---|---|
| Brief- и Undo-карточка | **PASS headless**: пауза под курсором, очередь до 2, повтор продлевает показ; permission notice не ждёт в очереди (`MicroUxUiTests`) | Реализовано; отдельный сценарий удержания клавиатурным фокусом не выполнялся | A C G | P1 |
| Undo | **Реализовано** | Queue removal восстанавливает в draft без auto-send; session/project unpin; точное scoped permission rule | C | P3 |
| «↓ к последнему» | **Реализовано** | Точка live activity + fade 150 мс; prepend history/layout не считаются новым; исчезающий control сразу noninteractive | A Cl G | P2 |
| Список сессий при первой загрузке | **Реализовано** | Loading sessions…; повторный refresh, даже пустого каталога, не мигает | Cl | P2 |
| Открытие сессии | **Реализовано** | Starting показывает Opening… | A | P2 |
| Drop-зона composer | **Реализовано** | Tint/подпись; sidebar folder drop добавляет проект | G | P2 |
| После удаления открытого чата | **Реализовано** | Открывается соседний чат; regression с сохранением draft | C | P2 |
| Карточка запроса (approval / вопрос) | **Не делаем (принцип владельца)**: дополнительные collapse/глобальный ⌘↵ | Полная команда уже selectable в bounded 160px scroll. Не скрываем consent и не перегружаем Send; focused/released-key safety остаётся | C Cl G | P2 |
| ✓ после rename, «Pasting…» при медленном буфере | **Реализовано** | Localized pending/success feedback и очистка состояния в finally | G Cl | P3 |
| Невидимые Unicode-символы при вставке | Очищаются с уведомлением; ZWJ/ZWNJ/variation selectors сохранены | PASS regressions: selected draft не удаляется пустым результатом; send-cleanup требует повторного подтверждения. OS IME gate отдельно | Cl | P1 |
| Уведомления | Вкл / выкл + только фон для системного attention | Дополнительную матрицу Never/Background/Always × тип события **не делаем (принцип владельца)**: существующий toggle и foreground suppression решают шум без нового набора настроек | C | P2 |
| Dock-бейдж | **Реализовано, en/ru regressions PASS**: число чатов с approval/question, очищается при ответе/закрытии; информационный requestUserAttention один раз при начале ожидания в фоне | macOS `MacDockAttention`; существующий Notifications=false очищает badge и отключает attention. Повтор snapshot и повторное включение не вызывают bounce | A Cl | P2 |

### 6.5 Клавиатура и фокус

| Что | Сейчас | Сделать | Сигнал | P |
|---|---|---|---|---|
| Type-to-focus | **PASS headless**: печатный символ в разговоре фокусирует composer, Ctrl/Cmd-комбинация не перенаправляется (`MicroUxUiTests`) | `MainWindow.TypeToFocus.cs`; ручной IME gate остаётся | A | P1 |
| Фокус после смены чата | **PASS headless**: composer получает клавиатуру (`MicroUxUiTests.After_switching_chats_the_composer_has_the_keyboard`) | Реализовано в `OpenSessions` | A | P1 |
| Фокус после viewer, карточки, меню | **Реализовано**, `KeyboardFocusUiTests` | Возврат opener, fallback composer; намеренно новый фокус не крадётся | G | P2 |
| ⌘L / ⌘Esc | **Реализовано** | Composer/возврат к чату; браузер сохраняет собственный ⌘L | Cl | P2 |
| Список сессий | **Реализовано** | ↑/↓ focus-only, Enter, F2, Delete с подтверждением, double-click rename | A C Cl | P2 |
| ⌘W | **Реализовано** | Сначала вкладка focused Terminal/right pane/Browser, затем окно | C | P2 |
| Preview | **Реализовано** | ⌘L/R/[/]/T/⇧C и Ctrl+Tab/Shift+Tab; Browser rail и side Preview | A C | P2 |
| ⌘\ | **Реализовано** | Закрыть focused pane; ⌘⇧I model, ⌘⇧E thinking; shortcut sheet | Cl | P2 |
| Mark unread / all read | **Реализовано** | ⌘⇧U для focused/current chat, ⇧Esc для всех | C | P2 |
| Esc-Esc на пустом поле | **Не делаем (принцип владельца)** | Не переосмысляем безопасную лестницу Esc/abort по таймингу; явный Rewind остаётся | Cl | P3 |
| Меню Help | **Реализовано** | Shortcuts, Report → Diagnostics/export, What's new → Updates/release notes; без выдуманных URL | C | P2 |

### 6.6 Проверить руками (по коду не определить)

- IME (японский или китайский ввод на macOS и Windows): Enter и Esc во время набора не должны отправлять сообщение и останавливать ход.
- **ANSI: done (решение владельца)**. В tool output удаляются только завершённые SGR `ESC[...m` (цвет/стиль, включая 256/truecolor); обычный текст остаётся читаемым. Остальные escapes/controls сохраняют прежнее безопасное представление, OSC/cursor/malformed escape не интерпретируются. `ToolSgrTests` + настоящий `/bin/ls --color=always` в `MicroUxReviewTests`; non-tool output не менялся.
- **Slash mouse: done**. Headless click по невыбранному пункту вставляет именно его, не keyboard selection. Найден и исправлен фокус: `CaretToEndRequested` теперь возвращает клавиатуру в composer после выбора мышью; regression `MicroUxReviewTests.Mouse_chooses_the_clicked_slash_suggestion_not_the_keyboard_selection`.
- **Files loading: audit done (headless)**. Три последовательных `RefreshAsync` после загрузки дерева: ни одного перехода `IsLoading=true`; дерево остаётся на экране, кадры `files-before-refresh`, `files-refresh-0..2`. Первый scan и смена проекта отдельно от refresh.
- **Shift-click: done (headless)**. Реальные mouse down/up с Shift расширяют выделение в отрисованном ответе, сохраняя anchor; `MicroUxReviewTests.Shift_click_extends_selection_in_a_rendered_reply`, screenshot `shift-selection.png`.

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

### 6.9 Аудит M, 2026-10-18: итоговая таблица и доказательства

Первый проход (до последующего закрытия строк ниже): настоящий Avalonia UI с FakeOmp, headless recorder:
`MicroUxUiTests|ContextMenuUiTests|PathContextMenuTests|InteractionStatesTests|ChatReviewRecorder` —
**32 passed / 0 failed / 0 skipped**, `.temp/ParityM/results/micro-en.trx`.
После мелких исправлений отдельный набор `MicroUxUiTests|PathContextMenuTests|MarkdownViewRenderTests.A_reply_with_links|TerminalUiTests.A_shell_runs|CompositionInputTests|ComposerUiTests.An_all_hidden`:
**en 22/22, ru 22/22**, `fixes-en.trx`, `fixes-ru.trx` в той же папке.
Команда: `DOTNET_ROOT=/Users/igo.shulga/.dotnet DOTNET_HOST_PATH=/Users/igo.shulga/.dotnet/dotnet OMBRA_TEST_LANG=<en|ru> OMBRA_REVIEW_DIR=<absolute review directory> /Users/igo.shulga/.dotnet/dotnet test avalonia/tests/Ombra.Tests/Ombra.Tests.csproj --artifacts-path .temp/ParityM/artifacts --filter '<набор выше>' --logger 'trx;LogFileName=<имя выше>' --results-directory .temp/ParityM/results`.
Просмотрены `.temp/ParityM/review/menus/reply-menu-Light.png`, `sizes/w480x360.png`,
`states/dark-focus-135.png`, `showcase-open-light.png`: меню, узкий composer, фокус, раскрытые tool/diff-карточки.
Recorder также сохраняет frames, оба theme и hover/pressed/focus; геометрия кнопок не скачет.
Нативные IME/Dock/Nightly этим не проверены.

Итоговый разбор каждой ранее Missing/Partial строки §6.1–6.6. Done означает реализованное поведение с headless/runtime regression, не native acceptance другой ОС:

| Строки | Итог | Проверка / ограничение |
|---|---|---|
| 6.1 Copy | **Done** | Awaitable clipboard, success после записи, одна ошибка; owning window вместо IsActive |
| 6.1 Motion; 6.3 fade/chevrons; 6.4 fade Jump | **Done** | 120/150ms CubicEaseOut, reduced motion; `VisualMotionTests` |
| 6.1 UI-state | **Done, headless restart round-trip** | Bounds/DPI clamp, minimized/maximized, панели; native multi-monitor hardware pass не заявлен |
| 6.1 shortcut labels | **Done** | Platform-aware catalog, terminal labels, sheet новых жестов; не создаём фиктивные menu-only shortcuts |
| 6.1 localization/CLDR | **Done** | ru one/few/many всех текущих plural callsites; 11–14/21–24/negative/long.MinValue, en/custom fallback |
| 6.2 image | **Done, headless** | Copy/Save/Open/Reveal bytes, errors/cancel; native picker/external app не заявлены этим recorder |
| 6.2 session; 6.5 unread/all-read | **Done** | Persisted explicit unread; read/open clears; status filter; Reveal/terminal; ⌘⇧U / ⇧Esc |
| 6.2 tabs; 6.5 Preview keys/⌘W | **Done** | Close others/right/middle, URL/reload, rail strip, scoped close; native web-engine keys дополнительно K1 gate |
| 6.2 terminal | **Done**: selection/all/Paste/Clear/selected-URL/Open/Ctrl-click | Реальный PTY, ПКМ не вставляет clipboard. Pixel-level link menu **не делаем (принцип владельца)**: не форкаем private renderer hit-test |
| 6.2 queue/Pasted | **Done** | Edit/Send now/Remove, Preview/Expand; draft и read-only guards |
| 6.2 error/notice | **Done** | Copy details, eligible Retry, Events |
| 6.2 Event log | **Done** | Selectable rows, Copy line/all, Clear |
| 6.2 code Insert | **Done** | Caret insertion, draft preservation; disabled без writable composer |
| 6.2 selection Quote/Comment | **Quote done**; Comment **не делаем (принцип владельца)** | Новая persisted transcript-range discussion model несоразмерна; K4 file/line/page comments остаются |
| 6.3 diff header | **Done** | Фон collapse, path/copy/right-click независимы |
| 6.3 Alt-chevron | **Done** | Model-level turn expansion, включая unrealized rows |
| 6.3 Markdown table hover | **Done** | Whole-row highlight не перехватывает selection |
| 6.3 Send scale | **Done** | 0.94 pressed, reduced motion = 1 |
| 6.3 keyboard grip | **Done** | Tab/focus ring, ±20px, Home default, End max, persistence |
| 6.4 Undo | **Done** | Queue → draft без auto-send; session/project unpin; scoped permission rule |
| 6.4 Jump unread | **Done** | Точка live activity вместо неточного счётчика; history prepend/layout не считаются новым |
| 6.4 sessions initial load | **Done** | Delayed initial scan; refresh без flicker, даже пустого каталога |
| 6.4 Opening | **Done** | Starting виден в ActivityLine |
| 6.4 Drop | **Done** | Composer overlay и sidebar folder target |
| 6.4 delete current | **Done** | Neighbor selection, regression с draft |
| 6.4 approval collapse/⌘Enter | **не делаем (принцип владельца)** | Полные selectable details уже bounded 160px; не скрываем consent и не ломаем focused/released-key safety |
| 6.4 rename/Pasting | **Done** | Localized pending/success/finally reset |
| 6.4 Unicode | **Done, headless** | Cleanup/preedit guards; реальный OS IME отдельно |
| 6.4 notifications/Dock | **Dock done, native macOS 12/12**; новая matrix **не делаем (принцип владельца)** | Toggle и foreground suppression решают шум без умножения настроек |
| 6.5 focus return | **Done** | Opener/fallback; intentional focus не перехватывается |
| 6.5 ⌘L/⌘Esc | **Done** | Composer/chat, браузер сохраняет адрес |
| 6.5 source-list keys | **Done** | Focus-only arrows, Enter/F2/Delete/double-click, virtualized groups |
| 6.5 pane/model/thinking | **Done** | Focus scope, sheet |
| 6.5 Esc-Esc rewind | **не делаем (принцип владельца)** | Не переосмысляем Esc/abort по таймингу; явный Rewind остаётся |
| 6.5 Help | **Done** | Реальные Diagnostics/Updates/Shortcuts targets |
| 6.6 native IME | **Native gate; Windows требует другую ОС** | Системный японский/китайский IME macOS/Windows не эмулируется headless preedit |
| Windows/Linux clipboard, WM/DPI, OS reduced-motion | **Нужна другая ОС** | Headless/macOS не выдаются за native Windows/Linux; Linux reduced-motion preference probe отсутствует |
| 6.6 ANSI | **Done** | SGR-only stripping + настоящий `ls --color=always`; controls не исполняются |
| 6.6 slash mouse | **Done** | Clicked item, composer focus, screenshots |
| 6.6 Files flicker | **Done** | Три refresh, ноль loading transitions; initial scan закрыт выше |
| 6.6 Shift selection | **Done, headless** | Shift-click сохраняет anchor; screenshot |
| Дополнительные исходные строки 6.2 tool/link | **Done** | Whole-tool Copy command/output/patch/path/Open/Reveal; link Open in Preview и URL tooltip |

Исходные `micro-*` таблицы сохраняются как реестр, не закрываются автоматически этим набором.
Собственный selection layer через границы сообщений (`micro-audit`, Transcript rows) —
**не делаем (принцип владельца)**: Copy/Copy as Markdown закрывают копирование без собственного текстового движка над виртуализацией.

Дополнительный M-проход: `MicroUxReviewTests|AttentionTests|FilesPaneUiTests|ImageAttachmentUiTests|ComposerMentionTests|DialogUiTests`
— **en 55/55 PASS, ru 55/55 PASS**, `.temp/ParityM/results/remaining-{en,ru}.trx`; команда выше с этим filter. Ru выполнен с `--no-build` на том же успешно собранном артефакте (параллельная правка SidebarRefreshTests временно блокировала новый build, не эту сборку).
Просмотрены `.temp/ParityM/remaining-en/{ansi-ls-tool-card,files-refresh-2,shift-selection,slash-mouse-after}.png`
и `files/dark-640-no-project.png`, `files/light-640-tree-menu.png`; также `remaining-ru/slash-mouse-after.png`. Files suite записал дерево, меню, search/changed/session, code/image/Markdown/too-large viewer,
no-project в light/dark и wide/narrow; layout audit не нашёл ошибок.
Dock после решения владельца реализован и проверен нативно (ниже). Реальные OS IME остаются native gate;
Windows/Linux native acceptance требует другой ОС; системный IME и реальная multi-monitor смена топологии не объявляются пройденными по headless. Решения по ранее Missing строкам перечислены в итоговой таблице выше.

Решения ANSI/Dock: `ToolSgrTests|AttentionTests|MicroUxReviewTests|TranscriptUiTests.Terminal_control_sequences` —
**en 27/27 PASS, ru 27/27 PASS**, `.temp/ParityM/results/decision-{en,ru}.trx`, тот же `dotnet test --artifacts-path .temp/ParityM/artifacts`.
Нативный Release smoke: `PATH=/Users/igo.shulga/.dotnet:$PATH DOTNET_ROOT=/Users/igo.shulga/.dotnet DOTNET_HOST_PATH=/Users/igo.shulga/.dotnet/dotnet OMP_SMOKE_PORT=18394 bash avalonia/tools/verify/feature-smoke/run.sh .temp/ParityM '/Users/igo.shulga/Library/Application Support/Ombra/runtimes/omp-18.8.0-bun-1.4.2' micro-ux-attention` —
**12 PASS / 0 FAIL**: настоящий engine bash с SGR, native Dock badgeLabel=1 при двух вопросах одного чата,
один informational request, повтор snapshot без bounce, settings off/on без повторного bounce, ответ очищает badge.
Скриншоты `.temp/ParityM/shots/40-sgr-readable.png`, `41-dock-desktop.png` просмотрены:
читаемый output без escape-маркеров SGR; красный badge «1» на Dock-иконке smoke-приложения.

**Последняя интеграция M (headless recorder, en).** Успешная сборка через
`dotnet test avalonia/tests/Ombra.Tests/Ombra.Tests.csproj --artifacts-path .temp/ParityM/artifacts --filter 'FullyQualifiedName~MicroUxTabTests'`
дала **4/4 PASS** (`tabs-en.trx`): реальные PTY Copy/Paste/Clear и безопасный ПКМ, tab menus/middle-click,
Browser shortcuts/scoped close, persisted unread/hotkeys, whole-tool Copy и link Preview.
Найден и исправлен настоящий конфликт: глобальный Ctrl+Tab/chat-history handler перехватывал browser keys;
теперь он явно исключает browser context, независимо от порядка tunnel handlers.

Расширенный финальный набор: `PluralFormsTests`, `MicroUxTabTests`, `MicroUxStateTests`, `MicroUxReviewTests`,
`KeyboardFocusUiTests`, `VisualMotionTests`, `ConversationMenuTests`, `TerminalUiTests`, `LocalizationTests`,
`ChatScrollTests`, `ContextMenuUiTests`, `AttentionTests`, `PathContextMenuTests`, `SessionAreaTests`,
`TranscriptUiTests`, `FilesPaneTests`, `FilesPaneUiTests`, `WorkspaceSettingsTests`.
Запуск на том же успешно собранном артефакте с `--no-build --no-restore`, `OMBRA_TEST_LANG=en`,
`OMBRA_REVIEW_DIR=/Users/igo.shulga/Documents/omp/.temp/ParityM/final-en`:
**213 PASS / 1 FAIL / 2 SKIP**, `final-en.trx`. Единственный FAIL — существующий timing guard
`AttentionTests.A_detached_helper_gets_its_arguments_and_the_environment`: запуск helper занял 152ms;
порог не ослаблялся, ошибка не скрывалась. Два SKIP требуют явного real-omp runtime.
Все функциональные M/UI cases прошли; общий набор не объявляется полностью зелёным.

Просмотрены финальные кадры `browser-tab-menu`, `terminal-context-menu`, `tool-card-menu`, `link-preview-menu`,
`session-marked-unread`, `initial-sessions-loading`, `restored-window-panels`, `keyboard-thinking-menu`,
`rename-paste-feedback`, `markdown-row-hover`, `alt-expand-turn`, motion/reduced-motion и context menus
для image/queue/paste/notice/error/event. Browser screenshot честно показывает headless native-surface limitation,
а не выдуманную загрузку страницы; WebKit/Nightly native acceptance — отдельный K1 gate.

Тот же расширенный набор на том же артефакте с `OMBRA_TEST_LANG=ru`,
`OMBRA_REVIEW_DIR=/Users/igo.shulga/Documents/omp/.temp/ParityM/final-ru`,
`dotnet test avalonia/tests/Ombra.Tests/Ombra.Tests.csproj --artifacts-path .temp/ParityM/artifacts --no-build --no-restore`
и указанным выше filter: **214 PASS / 0 FAIL / 2 SKIP**, `final-ru.trx`.
Просмотрены ru `browser-tab-menu.png`, `terminal-context-menu.png`, `initial-sessions-loading.png`,
`link-preview-menu.png`: подписи помещаются, disabled actions различимы, UI не перекрывает меню.
English timing failure остаётся зафиксированным отдельно, несмотря на успешный Russian запуск; проверка не ослаблялась.

Регрессионная проверка sidebar/pets после общего прогона: `SidebarRefreshTests` ждёт завершения
120ms fade только после hover/focus/menu changes; проверки сразу после catalog rescan остаются
немедленными, чтобы не скрывать повторный запуск анимации. На актуальном дереве с отдельным
`PetLayer` (видимость страницы не заменяет собственный `Pets.IsOnScreen` у perch):
`dotnet test avalonia/tests/Ombra.Tests/Ombra.Tests.csproj --artifacts-path .temp/ParityM/artifacts --filter 'FullyQualifiedName~SidebarRefreshTests|FullyQualifiedName~PetsTests'`
— **49 PASS / 0 FAIL / 0 SKIP** при `OMBRA_TEST_LANG=en`, затем тот же набор
с `--no-build --no-restore`, `OMBRA_TEST_LANG=ru` — **49 PASS / 0 FAIL / 0 SKIP**.
Результаты: `.temp/ParityM/results/followup-en.trx`, `followup-ru.trx`.
