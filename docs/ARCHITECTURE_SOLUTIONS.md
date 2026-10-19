# Решения для следующего релиза

2026-10-15. Дополнение к [roadmap](NEXT_RELEASE_ROADMAP.md) и [требованиям](PRODUCT_DIRECTIONS.md).

**Принято владельцем:** D1 — обязательное E2EE командного содержимого, доступного только авторизованным участникам команды; D2 — собственный форк omp с review upstream; D4 — общий Nightly runtime и полноценная работа с браузерными вкладками только через Ombra, без обязательного второго приложения. D3 уточнён: улучшаем наш harness, не меняем работающую авторизацию. Прочие решения ниже остаются рекомендациями, если явно не отмечено обратное. Принятие архитектуры не означает её реализацию; объём следующего релиза не сокращается.

## 1. omp: собственный runtime, а не набор обходов вокруг upstream

### Работа с исходниками и обновлениями

- Канонические полные исходники — `runtime/engine/coding-agent`; host — `runtime/engine/host.ts`. Для публикации форка предусмотрены upstream remote, рабочая ветка Ombra и защищённые release tags; создание удалённого репозитория и публикация не считаются выполненными одним импортом исходников. GUI и движок выпускаются согласованной парой.
- Обычные предметные коммиты в исходниках. Не хранить вторую обязательную копию тех же изменений как очередь `.patch`; не вводить искусственный лимит количества наших исправлений.
- Upstream updates: выбранный тег → integration branch → merge/review → проверки → новый immutable artifact → изменение пина GUI. Защищённую рабочую ветку не rebase-ить автоматически. Срочное upstream-исправление можно перенести отдельно, записав исходный commit.
- Автоматический отчёт обновления: изменения wire schema, agent lifecycle, approvals, session persistence, browser/host tools, dependencies/licenses, наши конфликты. Автоматизация открывает кандидат, но не продвигает его пользователям без review.
- Если upstream уже решил нашу проблему, удалить дублирующее изменение, а не поддерживать два пути. Отправлять общие улучшения upstream полезно, ждать принятия PR не требуется.
- Runtime manifest: upstream base, digest полного дерева и contract version; fork commit — только реально существующий, не синтетический идентификатор. После изменения исходников `python3 runtime/generate-engine-artifact.py` обновляет manifest/resources/identity; `--check` проверяет отсутствие рассогласования. Собственная immutable identity отделена от upstream-пакета.
- Установленные runtime не редактировать. Новый pack `ombra-engine-18.8.0-bun-1.4.2-v1` собирается в staging, проверяет source digest и загружает настоящий host через `--self-check` перед активацией. Старые директории сохраняются, но несовместимый per-session CLI не используется как скрытый fallback.

**Уточнение владельца: общее исходное ядро, не процесс omp на каждую сессию.** GUI-чаты создают отдельные `AgentSession` внутри одного движка приложения. У каждой — собственные registry/history/queue/approval/cwd и отмена; загруженный код и безопасные общие ресурсы переиспользуются. Клиент не запускает CLI для каждого чата и не держит spare-процессы. Для TypeScript/Bun и native dependencies выбран единый служебный host, управляемый Ombra; это не самостоятельное обязательное приложение и не отдельная VM/Worker на чат. Логические сессии разделяются явным транспортным ключом, не активной вкладкой. Закрытие/Stop одной не завершает движок или соседние сессии; сбой общего host восстанавливает ввод всех затронутых чатов без auto-send.

Первый SDK smoke (macOS arm64, локальный stand-in model): две `AgentSession` с private `AgentRegistry`, явными cwd/agentDir и `bindProcessState:false` одновременно выполнили `read` в одном PID; результаты относились только к своим проектам. После `dispose` первой вторая выполнила следующий запрос. Это не приёмка shared-host интеграции, расширений/субагентов или UI и не end-to-end замер ускорения.

### Контракт, который нужен нам

Порядок изменений:
1. Использовать готовые `set_event_filter`, `queue_update`, host tools и управление субагентами. **Уточнение RPC pin:** `get_messages_page` в 18.8.0 идёт только вперёд от offset 0; `limit` ограничивает страницу, но не позволяет запросить хвост. Для tail-first нужен собственный versioned backward-pagination контракт. Не конструировать opaque cursor на клиенте и не выдавать скачивание всех страниц за быструю загрузку последних сообщений.
2. Формализовать lifecycle: идентификаторы session/run/request, terminal result ровно один на принятое действие, отдельные foreground-complete и session-quiescent состояния. Не путать ожидание фоновой задачи с зависшим UI.
3. Structured approval: тип действия, полный аргумент, cwd, request id, scope и разрешённые ответы; никаких правил безопасности по тексту локализованной подсказки.
4. Queue ownership в runtime: подтверждение admission, восстановление pending, идемпотентность по request id. GUI хранит durable draft/outbox, но не объявляет отправку успешной до подтверждения.
5. Согласованный session lease для RPC, TUI и CLI; timeout/heartbeat не должен разрешать второго писателя при долгой паузе первого. Фактические существующие short-lived file locks проверить отдельно от lifetime lease.
6. Plan mode, job cancellation/output, MCP OAuth и входящий chunking; стабильные entry/message ids и timestamps после reload.
7. Прямой host-browser контракт нашего runtime: вызовы браузера идут в GUI с session/tab ownership и cancellation, а не через эмуляцию чужого терминального хоста. После переноса всех поддерживаемых callers удалить ставшие ненужными cmux/Tern-адаптационные обходы. Backend preview при этом сохраняется.
8. Provider discovery/catalog opt-in сделать настройкой runtime, затем убрать соответствующий временный provider-gate скрипт. Strict network proxy — отдельная пользовательская функция; не удалять его как будто это тот же обход.

На переходе contract version/capabilities проверяются до открытия сессии. Несовместимый пользовательский omp показывает понятную ошибку/выбор проверенного pack; не возвращать молча старые текстовые эвристики.

### Что действительно создаёт лишнюю работу сейчас

Проверено по установленным исходникам omp и коду GUI:
- Исходный аудит нашёл монолитный `GetMessagesAsync` при запуске. Delta-фильтр уже подключён после negotiation; 68 целевых client/RPC/real-omp проверок прошли. Tail-first история требует расширения runtime, описанного выше.
- `SessionController.cs:33,889–934`: fallback после non-terminal `agent_end` ждёт 3 секунды тишины перед `get_state`. Это **не** задержка каждого хода: `ConversationState.cs:309` уже обрабатывает `session_settled`.
- Установленный `rpc-mode.ts:2187–2201` требует `events: null` для всех событий. Корректный запрос: `{"type":"set_event_filter","events":null,"messageUpdates":"delta"}`; одного `messageUpdates` недостаточно.
- `rpc-session-events.ts` по умолчанию передаёт full snapshot; delta убирает accumulated `message` и `partial`, оставляя полный `message_end`.
- `rpc-session-settle.ts` уже есть в пине. Перед удалением probe проверить завершение prompts/slash/abort/goal/background, не планировать апгрейд только ради существующего события.

**Выполненный smoke настоящего `RpcSessionEventForwarder`**, без модели и сетевых запросов: 64 ASCII-символа на chunk, одинаковые start/update/end события.

| Chunks | Full, байт JSON | Delta, байт JSON | Финальный текст, символов |
|---|---:|---:|---:|
| 100 | 685 099 | 27 999 | 6 400 |
| 200 | 2 649 999 | 55 799 | 12 800 |

На 200 chunks — примерно **47,5× меньше трафика**. Удвоение chunks даёт ~3,87× full против ~1,99× delta. Это замер сериализованных кадров настоящего модуля, **не ускорение LLM в 47 раз**, не end-to-end benchmark UI и не измерение всех типов событий.

## 2. K1/D4: Nightly внутри Ombra и самостоятельное приложение

**ПРИНЯТО владельцем в текущем объёме:** один **Nightly Browser Runtime** на основе нашего Gecko-форка для самостоятельного Nightly, нынешнего встроенного браузера/preview Ombra и web-режима панели устройств. Это название планируемого компонента, не существующий SDK. Сохраняем разные интерфейсы, но не второй production-движок WebKit/WebView2. Пользователь может работать только в Ombra: полноценные вкладки и автоматический запуск runtime, без обязательного второго окна, pairing или «передать вкладку».

```text
самостоятельный Nightly ───────────┐
Ombra: браузер / preview / web ────┼─ общий Nightly Browser Runtime
наш omp ↔ Ombra browser broker ───┘       Gecko / WebRender / общие сервисы
           панель Ombra ← GPU-поверхность; ввод → runtime
```

### Повседневный сценарий

1. Пользователь открывает нынешнюю панель браузера/preview либо агент открывает URL по задаче. Ombra автоматически использует совместимый установленный runtime или получает проверенный pack из своей поставки/через установку при первом использовании. Ручная установка, настройка и запуск самостоятельного Nightly не обязательны. Runtime работает в фоне без отдельного окна; при его отсутствии чат/редактор доступны, браузер показывает установку/ошибку, а не подменяется старым движком.
2. Соединение аутентифицируется автоматически при запуске/повторном подключении. Ручное pairing и передача каждой созданной здесь вкладки не нужны. Видимая привязка вкладки к чату определяет scope; личные вкладки самостоятельного браузера автоматически не выдаются агенту.
3. Внутри Ombra доступны создание/закрытие/переключение нескольких вкладок, адрес, назад/вперёд, reload, мышь, клавиатура, выделение, аннотации, авторизация сайтов, загрузки и popups. Вкладки сохраняются и восстанавливаются после перезапуска Ombra. Это настоящий Nightly, не повторная загрузка URL в другом WebView и не slideshow скриншотов; агент работает с той же страницей, что видит пользователь.
4. Дополнительная команда «Открыть в отдельном окне» переключает представление той же вкладки, а не создаёт вторую сессию: сохраняются DOM, формы, авторизация и история. Обратное встраивание не перезагружает страницу. Одновременное управление из двух представлений не допускается.
5. В обоих представлениях видны индикатор агента и Stop. Ручной ввод приостанавливает агента. Разрешения на действия по сайту сохраняются; автоматическое подключение не означает доступ ко всем профилям/cookies/history.
6. Disconnect завершает незавершённое действие ошибкой; восстановление соединения не повторяет submit/платёж. Закрытие панели не должно незаметно закрывать личные окна Nightly; закрытие вкладки и lifecycle managed session — отдельные явные операции.

### Рекомендуемая техническая основа и проверка

- **Выбранный путь: out-of-process Gecko host + GPU-поверхность в Avalonia.** Использовать наш Nightly как runtime с embedded-режимом, сохранив sandbox/content/GPU процессы; не загружать `libxul` в .NET и не портировать Android GeckoView на desktop. Embedded-режим и exporter предстоит реализовать. Источник изображения — отдельный render target вкладки, не захват рабочего стола. Рендер должен работать без видимого внешнего окна.
- Разделить control plane (навигация, DOM, tools, аннотации) и display/input plane (кадры, focus, coordinates, клавиши). Model-facing browser API не меняется; агент работает со структурой страницы, а не только с изображением.
- Основной транспорт — GPU shared surface с платформенными адаптерами: macOS IOSurface, Windows DXGI/D3D shared handles; Linux требует отдельно доказать совместимость Gecko DMA-BUF/EGL с backend Avalonia, это не то же самое, что Vulkan opaque FD. Если нужна GPU-копия в совместимую поверхность, учитывать её явно. Shared-memory CPU frames — только явно обозначенный режим совместимости того же Gecko при недоступном GPU interop, не второй движок. Скриншотный polling, PNG/base64 и video encode/decode не являются основным display path.
- Перенос чужого OS-окна внутрь Avalonia не принимать как универсальное решение для macOS/Windows/Linux. Если зеркало не проходит UX-проверку, исследовать native view/surface hosting для конкретных платформ. Внешнее окно само по себе не закрывает K1; смена UX требует отдельного согласования.
- Проверить focus, IME/composition, Unicode, shortcuts, clipboard, drag/drop, resize/DPI, scroll, selection, popup/context menus, browser dialogs, files/downloads, audio/video и accessibility. Одних кадров и mouse click недостаточно; browser chrome/OS dialogs требуют отдельного маршрута отображения и ввода.
- Привилегированный компонент Nightly использует browser-parent/actors и штатные input primitives, не `dispatchEvent`. Страница не имеет прямого bridge API. Локальный транспорт: Unix socket / Windows named pipe с ACL, version handshake и session credentials вне страницы/URL/logs; не слушать LAN. Аутентифицированный bootstrap выполняется приложениями, не пользователем на каждой вкладке.
- Один владелец профиля: не запускать два процесса с одной profile directory. Рекомендация — сохраняемая managed browser session, связанная с workspace; standalone/embedded представления используют того же владельца. Импорт личного профиля не делать молча; cookies не копируются в GUI.
- Capabilities ограничены вкладками workspace/session; origin/action consent проверяется и после навигации. Ни prompt injection, ни аннотация не расширяют доступ. Preview остаётся функцией Ombra, но переезжает на тот же Nightly backend. Реальные Android/iOS эмуляторы K3 остаются отдельными устройствами, не заменяются web-эмуляцией.

**Приёмка:** из Ombra открыть Nightly без ручного pairing/передачи вкладки; работать со страницей внутри панели; читать/кликать/вводить/снимать через агента; аннотировать; вынести ту же вкладку в окно и вернуть без потери формы/авторизации. Проверить пользовательский ввод/Stop, недоступность личных вкладок, disconnect без replay, рендер без видимого внешнего окна, IME/DPI/dialogs и задержку на трёх ОС. Аккаунт P и облачный сервер для локальной интеграции не нужны. Это план проверки, не уже работающий renderer.

### Почему выбран этот путь: проверенные прецеденты

| Источник | Что действительно подтверждено | Что берём / чего не обещаем |
|---|---|---|
| [CEF `OnAcceleratedPaint`](https://github.com/chromiumembedded/cef/blob/master/include/cef_render_handler.h) | Windowless renderer отдаёт GPU resource: DXGI на Windows, IOSurface на macOS, native buffer planes/fd на Linux; отдельно описаны lifetime, popup, IME и drag/drop | Образец surface/input контракта, не готовая Gecko-библиотека. Не переходим на Chromium и не копируем CEF API целиком |
| [Electron offscreen rendering](https://github.com/electron/electron/blob/main/docs/tutorial/offscreen-rendering.md) | Shared GPU texture в отличие от дорогого GPU→CPU bitmap path; dirty updates, отсутствие кадров у неизменной страницы | Подтверждает подход интерактивного зеркала без видеокодека, но не наличие exporter в Nightly |
| [GeckoView architecture](https://firefox-source-docs.mozilla.org/mobile/android/geckoview/contributor/geckoview-architecture.html) | Runtime/session/view разделены; навигация, permissions, prompts и media вынесены в delegates; библиотека для Android | Берём границы ответственности, не Android-код как desktop control |
| [Sailfish Browser](https://docs.sailfishos.org/Reference/Core_Areas_and_APIs/Browser/) | Реальный Gecko browser с нативной Silica оболочкой | Прецедент интеграции Gecko с чужим UI, не готовый кроссплатформенный Avalonia SDK |
| [GEM / Amethyst](https://gecko-embedded.org/docs/webviewer/) | Облегчённая оболочка Firefox для embedded Linux, [патчи и ограничения](https://github.com/webdino/amethyst) | Пример отдельной оболочки над Gecko, не shared-surface SDK. Не переносить отключение security-функций ради скорости |
| [Avalonia GPU interop](https://docs.avaloniaui.net/api/avalonia/rendering/composition/icompositiongpuinterop) и [типы handles](https://docs.avaloniaui.net/api/avalonia/platform/knownplatformgraphicsexternalimagehandletypes) | Импорт внешнего GPU image, capabilities и синхронизация; IOSurface/D3D/Vulkan handles | Приёмная сторона уже есть в framework; поддержка конкретного runtime GPU/backend проверяется отдельно |

Готового поддерживаемого решения «наш Gecko → Avalonia на трёх desktop ОС» в проверенных проектах не найдено. Поэтому **не форкаем ещё один старый Gecko embedder**: добавляем узкий host/surface слой в уже поддерживаемый Nightly. Цена — собственный exporter и платформенный input/accessibility слой, а не разработка HTML/JS движка.

### Что найдено в наших исходниках и проверено запуском

- `avalonia/src/Ombra.App/Controls/PreviewPanel.axaml.cs:15–18,202–245`: сейчас `NativeWebView`, WebKit на macOS/Linux и WebView2 на Windows. `Ombra.App.csproj:15–17` пинит Avalonia 12.1.3 и WebView 12.1.0.
- `ViewModels/TernPreviewHost.cs:22–49`: текущий Tern page host принимает только macOS/WKWebView; `ClientCore/Browser/TernPage.cs:3–8` описывает fallback omp в Chromium при `no_window`/`unsupported`. Это нужно убрать в нашем cutover, иначе обещание одного движка нарушается.
- Nightly проверен в `~/Developer/arclike/desktop`: `surfer.json:6–10` задаёт Firefox 157.0.1 / candidate 158.0; это не доказательство версии собранного binary.
- В его `engine/gfx/layers/ipc/PNativeLayerRemote.ipdl:19–94` уже есть layer commands и `IOSurfacePort` между GPU и parent. `NativeLayerRootRemoteMacParent.mm:19–89,200–214` применяет набор layers/surfaces. Это **внутренний протокол Gecko, не внешний exporter и не один готовый кадр вкладки**. Нужны собственная граница доступа, tab-scoped composition target и управление временем жизни поверхности.
- `NativeLayerRootRemoteMacParent.mm:92–95` запрещает readback вне automation. [Mozilla screenshot docs](https://firefox-source-docs.mozilla.org/gfx/DebuggingWebRenderScreenshots.html) также отличают software `drawSnapshot` от compositor output. Не строить продукт на debug readback, не снимать защиту ради захвата.
- Выполнен временный .NET probe настоящей `Avalonia.Base.dll` из Debug output: **12.1.3+8eeda4f6f546165b3f72e63c9f42247abb306905**. Reflection подтвердил `TryGetCompositionGpuInterop`, `ImportImage`, keyed-mutex/semaphore/timeline updates и `IOSurfaceRef`/D3D/Vulkan handle constants. Это runtime-проверка наличия API, **не** создание GPU device, импорт Gecko frame или UI/performance proof.

### Общий runtime и перенос оптимизаций

- Один source baseline, общие engine patches/build flags и versioned runtime artifacts для обеих оболочек. Browser installer и Ombra получают совместимый проверенный build; нельзя брать любой системный Firefox или незаметно использовать разные сборки. Обновление проверяет protocol/capabilities, code signing, integrity и profile compatibility; runtime не заменяется под работающим процессом.
- Один runtime build **не означает один OS-процесс или общий личный профиль**. Gecko сохраняет process/site isolation; у каждого профиля один владелец. Повторно использовать подходящий запущенный host, но не смешивать личные и agent-owned данные ради экономии памяти.
- Оптимизации JS/layout/network/media/compositor общие благодаря одной сборке. Общие политики tab suspension, cache и resource scheduling должны жить в runtime/shared services, а не только в JS интерфейса Nightly. Проверить service dependencies облегчённой оболочки; не отключать security, permissions или browser services ради «быстрого preview».
- Оптимизация сайдбара Nightly не ускоряет Avalonia UI автоматически. Surface transport добавляет собственные расходы. B4 измеряет одинаковые страницы/сценарии в standalone и embedded, cold/warm start, input-to-present p50/p95, frame pacing, idle CPU, суммарную память всех процессов, GPU/video и поведение нескольких вкладок. Порог регрессии согласовать на baseline, а не объявлять ещё не измеренное ускорение.

### Чистая миграция существующего браузера

1. Сначала вертикальный proof: реальная страница нашего Gecko → surface → Avalonia, ввод, resize/DPI, скрытый host и вынос/возврат вкладки; затем те же сценарии Windows/Linux. Отдельно сравнить GPU и CPU compatibility paths. Не выдавать тестовый checkerboard за интеграцию Nightly.
2. Перевести существующий `PreviewPanel`, `PreviewTab`, браузер агента, popups и responsive web preview на общую session/page модель. Сохранить UI и поведение `BrowserPageHooks`/dialog broker, annotations, downloads, auth, permissions, isolated scripts, frames, capture/PDF, clipboard, UA/viewport и отмену; не переписывать эти функции вторым независимым набором.
3. Заменить engine-specific участки `TernPreviewHost`, `TernWebKit*`, `Platform/BrowserHooks`, `NativeInputs`, `*WebViewSnapshot`, `Mac/PopupWebViewHost` и engine probe/settings. Прямой host-browser контракт fork omp заменяет Tern/cmux обходы; все поддерживаемые callers мигрируют, fallback в управляемый Chromium убирается.
4. После функциональной и платформенной приёмки удалить старые WebKit/WebView2/WebKitGTK adapters и зависимость `Avalonia.Controls.WebView`, если её потребители полностью мигрированы. Старый browser backend не оставляется постоянным флагом/шимом. Существующие полезные поведенческие проверки перенести на новый backend; engine-specific harness не считать доказательством Gecko.
5. Перенести вкладки, URL, аннотации и настройки с сохранением исходных данных. Не обещать прямую переносимость WebKit/WebView2 cookies, keychain/passkeys и browser storage: определить поддерживаемый импорт, явно объяснить повторный вход на сайты, не удалять прежние данные молча. Авторизация model providers/Claude/Codex не меняется.
6. Runtime доставляется совместно либо по запросу при первом использовании браузера, с понятным размером/статусом; самостоятельное приложение Nightly не обязательно открывать. При отсутствии/несовместимости runtime — восстановление установки и явная ошибка, не возврат к другому движку.

**Критерий завершения cutover:** и обычный встроенный preview, и browser tools используют тот же проверенный Nightly runtime build; нет скрытого WebKit/Chromium пути. Сохранены прежние функции, новый UX, независимая работа приложений и данные; сравнение производительности выполнено в обоих представлениях. Самого рабочего bridge недостаточно.


## 3. B3: защищённая галерея без собственного SQLite codec

Рекомендую **обычный SQLite с зашифрованными payload + поисковый индекс только в памяти**. Шифруются все чувствительные значения, а не только PNG. Это защищает содержимое индекса, но не скрывает сам факт существования SQLite, число/размеры записей и структуру таблиц; такое ограничение должно быть явно записано в threat model.

### Формат и операции

- На диске: случайный `recordId`, crypto format/key version, nonce и ciphertext. URL/title/date/tags/folders/palette/note/hash/размеры изображения — внутри authenticated ciphertext. Поиск по открытому URL/тегу в SQLite запрещён.
- Случайный 256-битный ключ галереи; AES-256-GCM через WebCrypto для metadata/images/thumbs. Новый случайный 96-битный nonce на каждую запись/перезапись; AAD связывает recordId, тип объекта и версию формата. Подмена объектов/thumbnail между записями не должна проходить authentication.
- OSKeyStore используется для **обёртки небольшого ключа**, не для вызова системного keystore на каждую картинку. Реальный JS API: `OSKeyStore.encrypt(string)` / `decrypt(cipherText, trigger, reauth)`; base64 ключа подходит для строкового API. Ключ не выводится из memberId, пароля входа или profile path.
- SQLite получает только уже зашифрованные BLOB-параметры. Поэтому его WAL/journal не содержит открытых метаданных. `temp_store=MEMORY` — дополнительная защита, **не** шифрование WAL.
- Metadata + thumbnails можно держать в одной транзакционной БД; крупные изображения — отдельные зашифрованные файлы со случайными именами. Сначала атомарно сохранить encrypted image, затем commit ссылку; после crash удалить непривязанные ciphertext-файлы. Никогда не писать промежуточный открытый PNG в профиль.
- После unlock прочитать compact metadata одним потоком, расшифровать и построить простой RAM index. Для 5 000 объектов начать с нормализованных строк/полей и структур фильтрации; не добавлять native FTS до замера. Декодировать только видимые thumbnails, ограничивать cache и число parallel decrypt/decode.
- Изменения сначала атомарно записываются, потом обновляют RAM index; при ошибке UI сохраняет прежнее состояние. При lock очистить UI и caches; закрытый экран не должен продолжать отдавать расшифрованные object URLs.

**Важная проверка API:** `Sqlite.openConnection({path:"memory"})` в данном Firefox **не** создаёт memory DB: `Sqlite.sys.mjs:1422–1435` превращает относительный путь в файл профиля. Не использовать этот рецепт. Рекомендованный RAM index вообще не требует такого SQLite соединения.

### Почему не SQLCipher/свой VFS

SQLCipher — отдельная native dependency и сборочная интеграция; stock mozStorage не становится SQLCipher от `PRAGMA key`. Это возможный путь, если потребуется скрыть все SQLite pages и выполнять дисковый FTS, но не самый простой для 5 000 записей. Свой encrypted VFS добавляет сложные crash-consistency/nonce/WAL риски; его не писать ради галереи.

Если формулировка «индекс зашифрован» означает обязательное шифрование **каждого байта файла SQLite**, а не всех чувствительных payload и поисковых данных, вариант BLOB требует явного уточнения требования; альтернативой будет отдельное поддерживаемое encrypted DB решение. Не выдавать column encryption за full-database encryption.

### Ключи, импорт и ограничения

- При отказе/блокировке OS key store показывать unlock/retry, не создавать новый ключ поверх существующей галереи. На Linux отсутствие подходящего безопасного storage не разрешает plaintext fallback.
- Перенос на другое устройство — отдельный зашифрованный backup с recovery key; смена аккаунтного пароля сама по себе не восстанавливает локальный ключ. Перевыпуск обёртки и ротация скомпрометированного data key — разные операции.
- Export создаёт plaintext только после подтверждения. Системный импорт по умолчанию выключен; оригинал сохраняется до явного удаления пользователем. Общие Downloads не считать достоверным источником именно скриншотов.
- Шифрование профиля не защищает от процесса с доступом к разблокированной памяти, OS swap/crash dump, уже экспортированных файлов и скриншота экрана. Не обещать абсолютное отсутствие plaintext за пределами контролируемых файлов приложения.

**Proof:** 5 000 реальных metadata records с ограниченными размерами полей, first viewport + search; crash между image write и commit; key cancel/loss; скан DB/WAL/temp на контрольные URL/теги; повреждение ciphertext; явный export. ≤1 с остаётся целевым бюджетом, не измеренным результатом этого исследования.

**Состояние реализации local B3 (исходники, не runtime-proof).** В Nightly добавлен канонический `src/zen/gallery`: `about:gallery`, вход из sidebar, виртуализированная masonry-галерея, RAM-поиск и фильтры, просмотр/метаданные, массовые операции, подтверждённый plaintext-export; захват использует настоящие Firefox Screenshots actors/`ScreenshotsUtils`, а импорт системной папки включается явно в настройках галереи. Сетевой доставки B2 в этой реализации нет.

`ZenGalleryStore.sys.mjs` хранит AES-256-GCM envelopes с AAD `(format, randomId, objectKind)` для изображения, миниатюры и metadata/config/folders. На SQLite-диск попадают только случайные идентификаторы и ciphertext, включая имена папок/теги, путь наблюдения и bookkeeping; открытый поиск существует только в RAM. **Это не SQLCipher:** schema/count/размеры ciphertext и filesystem timing видимы; swap/crash dumps, разблокированная память, clipboard и явно экспортированные копии остаются вне этой защиты. Native `nsIOSKeyStore.usesOSBackend` различает Keychain/Credential Manager/libsecret и NSS fallback: отсутствие capability, безопасного backend, ключа или отмена разблокировки запрещают доступ, а не создают новый ключ поверх существующей галереи.

Нативные регрессии находятся в `engine/zen/gallery/tests/browser.toml` (канонические файлы `src/zen/gallery/tests`). В этой source-итерации сборка/тесты не запускались. Настоящие 5 000 записей, бюджет ≤1 с, визуальный smoke `about:gallery`, скан DB/WAL/temp и security review, включая Linux с libsecret и без него, остаются обязательными интеграционными acceptance gates, а не заявленными результатами.

## 4. D1/P: сервер и offline без самодельного CRDT прав

### Рекомендуемая основа

**ASP.NET Core 10 + Postgres + HTTPS commands/delta sync + WebSocket уведомления**, один сервис и один deployable на старте. Это тот же стек, что клиент; отдельные Kafka/Redis/микросервисы пока не нужны. Вложения — object storage через серверную авторизацию. Сервер можно развернуть самостоятельно; hosted endpoint подключается только после входа и явного выбора.

Auth: ASP.NET Identity для пользователя и [OpenIddict](https://documentation.openiddict.com/introduction) для OIDC, [Authorization Code + PKCE](https://documentation.openiddict.com/configuration/proof-key-for-code-exchange.html) для public desktop/browser clients. Не писать свой OAuth сервер и не помещать client secret в приложение. Pairing — одноразовый short-lived challenge, подтверждённый уже вошедшим устройством и привязанный к ключу нового устройства; короткий публичный member ID не является credential. Device sessions/refresh tokens отзываются отдельно, а backend проверяет revoke при каждом действии.

### Консистентность

- Local SQLite: replica + outbox с `operationId`, `baseVersion`, typed operation. В транзакции одновременно записываются local intent и outbox. Сервер дедуплицирует operationId и возвращает сохранённый результат.
- По каждому team/project scope сервер последовательно фиксирует revision и change log в одной транзакции с изменением состояния. Revision выдаётся под сериализацией scope, не как глобальный sequence до commit: иначе поздно закоммиченный меньший номер можно пропустить.
- WebSocket сообщает о новых revision; durable delta endpoint догоняет. При истёкшем cursor — snapshot с согласованной revision, затем delta. Сокет и presence не являются единственным хранилищем событий.
- Простые поля: серверная версия, сравнение baseVersion и явный конфликт либо документированный merge. Время клиентских часов не определяет победителя. Группы/теги — операции над стабильными item IDs; удаления с tombstones.
- **ACL, owner, invitations и device revoke — серверные транзакции, не LWW/add-wins.** Offline управление правами остаётся pending, не показано успешно применённым. Входящая старая операция после revoke получает forbidden и не возвращается в бесконечную очередь.
- Передача владельца: предложение с TTL/current owner/version, подтверждение получателем, atomic compare-and-swap. Команда не может остаться без владельца или получить двух из-за гонки.
- Revoke повышает access revision, блокирует следующие действия/доставки и инвалидирует subscriptions. Проверка перед enqueue недостаточна: очистить ожидающие доставки и заново авторизовать выдачу attachments. Уже отправленные в сеть байты/скачанные копии забрать нельзя.
- [Postgres RLS](https://www.postgresql.org/docs/current/ddl-rowsecurity.html) — дополнительный барьер, не замена app authorization: table owner/BYPASSRLS обходят политики, а JOIN с таблицей прав требует учёта гонок.
- Progress opt-in: phase/counts, отдельно разрешаемые step text/relative filenames. Logout закрывает connection, отменяет публикацию/outbox и изолирует кэш прежнего аккаунта. Публичные update/model запросы не относятся к сервису команд.

### Приватность: обязательное E2EE — принято владельцем

Содержимое передаваемых сообщений, правок, ссылок, снимков и вложений доступно только участникам
команды с соответствующим доступом на их авторизованных устройствах. Ни оператор/владелец сервера,
ни администраторы инфраструктуры, ни разработчики сервиса, ни посторонние пользователи читать его не могут.
Сервер не получает ключи расшифрования; серверное добавление фиктивного участника или подмена ключа
не должны давать доступ к содержимому. Recovery, резервные копии и добавление устройств не создают обход E2EE.
Вариант доверенного оператора с одним лишь TLS/encryption at rest не принят.
Сервер хранит encrypted payload и необходимые identity/ACL/routing metadata; TLS и server-side
авторизация обязательны дополнительно к E2EE. Локальный режим не обращается к сервису команд.

До реализации общей БД выбрать проверенную библиотеку/протокол управления ключами и подтвердить
распределение ключей устройств/групп, проверку новых устройств, ротацию при revoke и recovery/history policy.
Не проектировать собственный криптографический протокол и не обещать E2EE поверх сохранённого plaintext.
Сервер всё равно видит membership и маршрутизацию; ранее полученное содержимое исключённого участника
не исчезает. Поиск, multi-device, onboarding и recovery входят в реализацию и независимый security review.
После отзыва доступа исключённый участник не получает ключи новых сообщений; уже полученные им
сообщения и сохранённые копии отозвать невозможно. Независимый review проверяет также активного
недоверенного сервера, а не только чтение пассивного дампа базы.

### Развёртывание и управление участниками — уточнение владельца (2026-10-17)

- Сервер — **отдельная папка в репозитории** (`server/`), которая ставится на свою машину. Команда подключается к нему по **IP или домену**. Главные требования — безопасность и управляемость.
- **Админ удаляет участников двумя путями:** на сервере (CLI оператора, например уволенный сотрудник) и из приложения, где админу доступны расширенные инструменты.
- Следствия для реализации, иначе требования противоречат E2EE выше:
  - Права админа подтверждаются **подписью ключа его устройства**, не публичным member ID. Приложение показывает админские инструменты по проверенной роли, а сервер проверяет подпись и роль на каждом действии.
  - Сервер (и его CLI) может **только сокращать** доступ: удалить участника или устройство, отозвать сессии, остановить доставку и подписки. **Выдать** доступ к содержимому он не может: приглашение становится действующим только после подтверждения устройством админа. Иначе оператор мог бы тихо добавить себя.
  - Удаление из приложения сразу ротирует групповой ключ. Удаление с сервера закрывает доступ немедленно, а ротацию выполняет первое устройство админа, которое выйдет в сеть; до ротации сервер ничего не доставляет удалённому.
  - Подключение по IP без публичного сертификата идёт через закреплённый отпечаток сертификата, переданный в приглашении (не «доверять любому»). По домену — обычный TLS (автоматический сертификат).

- **Решения владельца (2026-10-17), детали — [SERVER_DESIGN §11](SERVER_DESIGN.md#11-решения-владельца-sd-18--приняты-2026-10-17):**
  - **D-3 владение:** сервер принадлежит владельцу. Сменить владельца без его устройства можно только его офлайн-ключом восстановления (заметно всей команде, с задержкой и правом вето владельца); владелец может явно передать ключ преемнику — это событие видно команде. Без ключа — переустановка сервера и новая команда. Админы, в том числе вместе, сменить владельца не могут.
  - **Модель обмена:** сервер нужен только для состава/presence и обмена типизированными элементами — метка в духе Figma (якорь на элементе страницы или в коде + контекст), ссылка, снимок, правка/патч, коллекция; адресация — пространству или конкретным участникам. Чата, ленты комментариев, тредов и истории диалогов нет; каждый получатель отвечает только своим статусом (`seen/done/dismissed`) и короткой заметкой. Всё содержимое — под E2EE; сервер — очередь доставки, не архив.
  - **D-4 доступ к прошлому:** новый участник по умолчанию не видит ранее опубликованные элементы и метки; админ может явно «поделиться элементами».
  - **D-5 удаление:** удалённый сразу теряет доступ ко всем элементам команды — сервер немедленно прекращает любую выдачу, приложение сразу ротирует ключ, а честный клиент удалённого по подписанному уведомлению/commit стирает локальные элементы, вложения и ключи команды (best-effort: изменённый или офлайн-клиент и заранее сделанные копии не отзываются). При удалении через CLI новые элементы держатся до ротации.
  - **D-7:** резервной копии элементов на сервере нет; сервер держит только очередь доставки и одноразовые передачи.
  - **D-1, D-2, D-6, D-8, D-9, D-10:** приняты рекомендации (OpenMLS через Rust, отдельные группы проектов, свой Rust-компонент в Nightly, blob-ы на ФС с прокси для S3, Caddy в режиме домена, handshake как PublicMessage).


## 5. K3: устройства без внешних окон IDE

### Android

- SDK/AVD локально; IDE не требуется. Установка SDK только с согласием и принятием его лицензий, конкретный image по архитектуре.
- Headless emulator + **закреплённая пара scrcpy server/client protocol**. Наш host читает video socket и посылает control messages; не встраивать SDL-окно scrcpy и не выполнять `adb shell input` на каждый pointer move.
- Переиспользовать протокол/код upstream с notices; H.264 — практичный первый вариант с платформенными декодерами (VideoToolbox / Media Foundation / доступный Linux decode backend). Проверить распространение codec/native dependencies отдельно; не объявлять любой H.264 автоматически запрещённым или royalty-free.
- Ограничить очередь decoded frames, выводить самый свежий допустимый кадр, не накапливать video buffer. Не выбрасывать произвольные encoded interframes, ломая decode chain; backpressure/drop делать с учётом codec.
- При скрытии остановить передачу/декодирование, сохранить emulator; resume получает новый keyframe/актуальный размер. Поворот обновляет frame/input transform атомарно.

Источник: [scrcpy developer documentation](https://github.com/Genymobile/scrcpy/blob/master/doc/develop.md) прямо описывает отдельные video/control sockets, default H.264 и **отсутствие совместимости протокола между разными версиями**.

### iOS

- macOS + полноценный Xcode/runtime обязательны; Simulator.app и Xcode UI открывать не нужно.
- Lifecycle/install/openurl/appearance — simctl. Live video и HID input — закреплённый `idb_companion`; прототип через `idb video-stream`, production может использовать долгоживущий companion transport вместо запуска CLI на каждый tap.
- [idb commands](https://fbidb.io/idb/commands) документируют поток H.264/MJPEG в stdout; это подходящий кандидат в отличие от обещания интерактивности от `simctl recordVideo` без проверки буферизации.
- Частные Apple API внутри companion означают зависимость от Xcode/runtime версии: pin, проверка совместимости и понятная ошибка, а не молчаливое чёрное окно. Не распространять Apple runtime внутри нашего пакета.

**Проверено локально:** `xcode-select -p` → `/Library/Developer/CommandLineTools`; `xcrun simctl list runtimes` → utility `simctl` не найдена. `command -v` нашёл xcrun/xcode-select, но не adb/emulator/scrcpy/idb в текущем PATH. Это не доказывает их отсутствие во всех каталогах. Реальный video/input smoke здесь пока требует установленного и выбранного Xcode/runtime либо Android SDK; ничего не скачивалось и устройства не запускались.

## 6. K5/D3: изучить чужие харнессы, улучшить наш omp

**Исправление постановки владельцем:** авторизация уже работает; менять её и создавать сменные ACP-backend не требуется. K5 — изучение приёмов Codex/Claude и перенос полезного поведения в наш harness/runtime. Предыдущая рекомендация ACP была неверным расширением задачи.

Исходная база уже существует: `harness/profile/agent/AGENTS.md`, skills и extensions. Не создавать параллельный профиль с дублирующими правилами.

| Что изучаем | Что переносим к нам | Куда |
|---|---|---|
| Формирование контекста | приоритет инструкций, точное добавление workspace/selection/diagnostics, ограничения объёма tool output | harness + runtime context builder |
| Agent loop | корректное продолжение после tools/approval, interruption, восстановление и compaction без потери задачи | fork omp |
| Инструменты | понятные schemas/errors, адресные edits, доступ к LSP/browser, bounded output | runtime tools / host tools |
| Планирование | соразмерный план, обновление шагов по результату, отсутствие лишней делегации | harness + todo contract |
| Проверка результата | реальная проверка изменённого поведения, источник доказательств, честные ограничения | существующие skills и harness |
| Долгая работа | context summary, handoff, project memory без превращения недоверенных файлов в инструкции | runtime + harness |

Источники: [открытый Codex](https://github.com/openai/codex) (Apache-2.0), публичные [документы Claude Code](https://code.claude.com/docs/en/overview), опубликованные plugins и наблюдаемое поведение. Публичный [репозиторий Claude Code](https://github.com/anthropics/claude-code) не считать автоматически полными открытыми исходниками его ядра. Копируем лицензированный код с notices либо независимо реализуем идею; не строим план на добыче скрытых prompts.

Метод: фиксированный набор реальных задач → baseline нашего агента → одно изменение → повторные прогоны на одинаковой модели/settings/исходном repo snapshot. Измерять правильность результата, нарушения разрешений, потерю данных, tool calls/tokens/time и объём лишних правок. Повторные попытки нужны из-за стохастичности, а не чтобы выбрать единственный удачный ответ.

**Приёмка K5:** таблица «приём → источник → наш пробел → изменение → измерение», реализованные улучшения выбранных приёмов и отсутствие security/data-loss регрессий. Скопированный большой system prompt или запуск чужого движка не является результатом K5. Работающую авторизацию сохраняем без переоценки.

## 6a. K7: встроенный редактор кода

По описанным функциям речь о редакторе, не о втором coding agent; «подсветка суффика» трактуется как подсветка синтаксиса. Рекомендация — **свой поддерживаемый fork AvaloniaEdit + TextMate**, не fork целого VS Code и не переписывание текстового редактора.

Факты: нынешний `avalonia/src/Ombra.App/Controls/CodeView.cs` — рисуемый read-only viewer с selection и `SyntaxHighlighter`, без полноценной модели редактирования. [AvaloniaEdit](https://github.com/AvaloniaUI/AvaloniaEdit) предоставляет editing, folding, completion UI, line virtualization и multi-caret; [NuGet 12.0.0 manifest](https://api.nuget.org/v3-flatcontainer/avalonia.avaloniaedit/12.0.0/avalonia.avaloniaedit.nuspec) указывает MIT, net10.0 и зависимость Avalonia 12.0.0. Совместимость с нашим Avalonia 12.1.3 должна пройти actual UI smoke, из manifest она не следует автоматически.

### Почему эта основа

- Native Avalonia control сохраняет текущий стек и не зависит от установленного Nightly. Fork нужен для наших исправлений, а не чтобы менять namespace всех upstream файлов.
- TextMate даёт синтаксическую подсветку; completion/hover/diagnostics/rename/go-to-definition даёт отдельный LSP слой. Наличие completion UI в библиотеке не означает готовый language server.
- Monaco — достойная альтернатива для web-shell, но здесь добавляет WebView/JS/worker/IPC и дополнительные пути keyboard/IME/focus. Его [README](https://github.com/microsoft/monaco-editor) прямо предупреждает: обычные VS Code extensions в Monaco не работают.
- Fork VS Code оправдан при необходимости полного extension host/debugger/IDE ecosystem и смене оболочки приложения. Для файлов и подсветки это лишняя вторая IDE внутри Ombra.

### Модель редактора

1. Один document buffer на canonical workspace URI, несколько views/tabs на него. Undo/redo, dirty state, поиск/замена, переход на строку, folding, multi-caret, сохранение кодировки/BOM/EOL и восстановление несохранённых буферов после crash.
2. Язык определяется extension/shebang и ручным выбором; TextMate fallback работает без LSP. LSP процессы запускаются лениво по workspace/language, после trust; URI/document version и negotiated encoding согласованы для UI и агента.
3. Один document/workspace service координирует UI и инструменты нашего fork omp. Не открывать второй LSP на том же unsaved document с другой версией. Доступный существующий LSP сначала исследовать, затем выбрать одного владельца и host RPC для второго потребителя.
4. Agent edit несёт expected base revision/hash. Dirty пользовательский buffer не перезаписывается: показать diff/merge либо предложить сохранить перед применением. Применённая edit операция попадает в undo stack, dirty сохраняется до явного save.
5. Для disk writes общий координатор + проверка base version; temp + atomic replace защищают от оборванной записи, но **не** от гонки с внешним редактором. File watcher дополняется сверкой версии при сохранении; внешняя гонка остаётся конфликтом, не молчаливой потерей текста.
6. Read-only файл, binary, слишком большой файл, symlink и удаление/rename извне — отдельные состояния. Не обрезать содержимое, затем сохранять укороченный текст поверх файла. Review/diff остаётся отдельным представлением, но открывает тот же document.
7. K4 comments привязаны к document version/anchors; выбранный код и несохранённые изменения отправляются агенту явно с provenance. Run/build используют disk snapshot, не притворяются проверкой unsaved buffer; сначала явный save или отдельный согласованный snapshot.

**Приёмка:** открыть/редактировать/сохранить файл с подсветкой; undo/redo; Unicode/IME и CRLF/BOM; восстановить dirty buffer; одновременно изменить файл агентом и внешним редактором без потери текста; diagnostics/definition соответствуют текущей версии; large-file/read-only режимы честные. Actual UI smoke macOS/Windows/Linux обязателен.


## 7. B1/B4/K4: более прямые решения

- **B1:** один per-workspace state machine на события tab open/close/location change/move/restore. Ключ — Firefox effective-TLD service, не regex «последние две части домена». Гистерезис 4/2; при настраиваемом пороге правило разбора определено отдельно. Ручное извлечение подавляет повторный auto-add до закрытия вкладки; rename/color переводят группу в manual. Массовый restore обрабатывается одним батчем, не N полными пересканированиями.
- **B4:** сначала Profiler baseline. Приоритет — не загружать 120 страниц при restore, не пересчитывать весь список на каждое событие, не анимировать невидимое. Ограничивать только массовое открытие, а активация пользователем обходит очередь. Tabs с audio/form activity не выгружать по простому idle timeout. Виртуализацию вводить только если профиль докажет стоимость layout, сохранив drag/drop, keyboard и accessibility.
- **Состояния B4:** actual load events + очередь самого менеджера; crash/error/unloaded из browser state. Когда общий объём загрузки неизвестен, показывать честное неопределённое состояние, не выдуманный процент. Это уточнение требования «реального прогресса».
- **K4 anchors:** file path + commit + range + excerpt/context; сначала точное сопоставление, затем ограниченный поиск в нужном файле. Несколько равных кандидатов — выбор пользователя, не произвольный первый match. Page anchor включает origin/route/frame/selector и контекст; stale anchor показывается вместе с исходным snapshot.
- **Входящие:** сообщение другого участника — недоверенные данные. Оно не запускает агент автоматически и не выдаёт tool permission. «Принять → агенту» — явное действие с показом payload; ссылки проходят scheme validation/redaction перед отправкой, не после доставки.

## 8. Следующие технические работы

1. Создать исходный fork workflow и versioned runtime contract; одновременно включить готовые delta/paging возможности в GUI. Приоритет не зависит от ожидания ответа upstream.
2. Proof общего Nightly runtime: Gecko surface → Avalonia через GPU interop, ввод/IME/DPI, скрытый host, вынос/возврат вкладки. Затем полный cutover существующего preview и web-режима K3; B4 сравнивает обе оболочки.
3. Proof encrypted SQLite payload + RAM search на 5 000 объектах, без plaintext temp. Согласовать, достаточна ли видимость schema/count для threat model B3.
4. Для платформы реализовать принятое обязательное E2EE; проверить вертикальный сценарий двух устройств с revoke/reconnect и активной подменой ключей сервером.
5. Подготовить SDK для device proof; отдельно baseline задач харнесса K5 и native editor proof K7. Авторизацию/ACP не переделывать.

**Граница доказательств:** прочитаны текущие browser/CodeView/harness, первичные источники embedder-проектов и GPU API, внутренний Gecko surface protocol; выполнен reflection probe установленной Avalonia 12.1.3. Ранее выполнены smoke сериализатора omp и проверка toolchain. Общий Nightly runtime/exporter, GPU frame import, bridge и editor UI пока не реализованы/не проверены запуском; API/manifest не выданы за end-to-end совместимость. Работающая авторизация принята как сообщённый владельцем факт.

## 9. Бета: технические усиления и кандидаты

Статус: **предложения по результатам brainstorming, не автоматически принятый дополнительный объём**. Все согласованные P1–P4/K1–K7/B1–B4 и PARITY сохраняются. Ниже отдельно уточнения существующих работ и новые продуктовые связки; даты и оценки до технических proof не назначены.

### 9.1. На что опираемся в текущем коде

- `avalonia/src/Ombra.App/Program.cs`, `BuildAvaloniaApp`: на macOS явно выбираются **OpenGl → Software**, не Metal. Причина в комментарии — прежние resize/popup проблемы. Наличие GPU import API не доказывает совместимость с активным compositor: K1 proof должен проверять реальный rendering mode, handle/sync capability и восстановление, а не только reflection.
- `Services/Updates.cs` уже проверяет подпись manifest; `Services/UpdateInstaller.cs` сохраняет `.previous`, возвращает старую версию при неудачном swap и проверяет staged app через `--version`. Это не доказательство здоровья новой версии после открытия профиля/миграций и запуска omp/Nightly.
- `Services/Runtime/RuntimePack.cs` уже устанавливает Bun dependencies с `--frozen-lockfile --ignore-scripts`, проверяет версию omp. Не заменять существующие механизмы вторым installer.
- `Services/SelfTest.cs` и `Services/DiagnosticsBundle.cs` уже существуют. Диагностика маскирует настройки, экспортирует счётчики и типы событий, а не весь разговор. `ReadTail` ограничивает итоговый текст, но сначала вызывает `File.ReadAllText`: большой лог всё ещё читается целиком.
- `Ombra.ClientCore/SessionController.cs` использует capacity-1 `Channel<bool>` как сигнал обновления **snapshot**, а не как очередь RPC frames. `DropWrite` здесь сам по себе не доказывает потерю событий; переписывать коалесинг на основании названия режима нельзя.
- `SECURITY.md` явно описывает незакрытые границы trust для project extensions/MCP и то, что strict proxy не является firewall. Это существующие ограничения, не новый подтверждённый эксплуатацией security finding.

### 9.2. Уточнения существующих работ — рекомендуемый приоритет

| ID / привязка | Что усилить | Проверяемая приёмка |
|---|---|---|
| **BT1 — этап 1, K6/K7** | Общая идентичность операций: workspace/session/run/request, revision документа и generation навигации. Один владелец записи/процесса; поздние ответы после restart, смены файла или вкладки не применяются к новому состоянию. Разрешение привязано к операции и ресурсу, а не ко всем будущим вызовам инструмента. Использовать существующие контроллеры/lease, не вводить глобальную event bus. | Два чата, ручная правка, restart и навигация во время tool call: ни запись в чужой буфер, ни клик по устаревшей странице не выполняются молча. |
| **BT2 — этап 1, P offline/outbox** | Durable очередь/черновики и небольшой журнал значимых действий: queued → started → confirmed либо unknown. Сохранение до подтверждения принятия пользователю. После обрыва нельзя автоматически повторять отправку формы, публикацию, внешний shell effect с неизвестным исходом. Idempotency key использовать только там, где получатель поддерживает её семантику. | Kill до/после dispatch и ack, disk-full, reconnect: принятое задание не пропадает; неизвестный внешний результат показан явно. Откат checkpoint не выдаётся за отмену сетевой операции. |
| **BT3 — этап 1, security** | Enforcement вне LLM: trust до project hooks/LSP/MCP, явные области filesystem/network/secrets, наследование ограничений дочерними процессами. Browser grant ограничен workspace/tab/origin; личный профиль и cookie не становятся общедоступными агенту из-за общего runtime. Изменение исполняемой команды/конфигурации требует пересмотра разрешения. | Недоверенный проект не запускает код до согласия. Тестовый инструмент не читает запрещённый файл и не выходит на запрещённый адрес; отдельно проверены child process, MCP и прямые инструменты. Недоступная изоляция не маскируется зелёным статусом. |
| **BT4 — этап 0/поставка, K1** | Подписанный совместимый набор Ombra + omp + Nightly + схемы данных, capability handshake, staged install и first-start health commit. Разделить rollback приложения и восстановление данных. Добавить политику ротации ключей/свежести metadata; зафиксировать зависимости, лицензии и состав поставки (SBOM). | Повреждённый pack, несовместимый protocol, потеря сети, неудачная миграция и crash первого запуска оставляют доступный recovery path без потери новых данных. Живой Gecko profile не открывается двумя host и не переписывается updater. |
| **BT5 — этапы 1–2/B4, devices** | Развить существующий owner-aware supervisor: различать агентный turn, workspace dev-server, browser host и device stream. Общий учёт CPU/RSS/GPU/диска, лимиты spawn/concurrency, bounded logs/очереди и чтение только хвоста логов. Отбрасывать заменяемые визуальные обновления, но не approvals/терминальные события. | Stop нужной области не убивает чужой dev-server или вкладку; после выхода нет бесхозных собственных процессов. Большой stdout, несколько чатов, sleep/wake и memory pressure не блокируют ввод; несохранённая форма/audio не выгружаются простым idle timeout. |
| **BT6 — K1/K3 web/B4** | Вертикальный GPU proof на фактическом Avalonia backend раньше полной browser migration. Согласовать ownership buffers, frame acknowledgement, resize/DPI/IME/focus, input routing, device-loss и shutdown. Если требуется смена compositor — проверить исходные resize/popup сценарии. Ошибка import не запускает второй Chromium/WebKit скрыто. | Реальная Gecko-страница внутри Ombra, ввод, быстрый resize, разные мониторы/DPI, crash host и восстановление. Сравнить standalone/embedded cold и warm, p95 задержки и CPU/RSS/GPU с учётом транспорта. Нет необходимости держать отдельное окно Nightly. |
| **BT7 — K5/K7, этап 2** | Incremental document/index/context pipeline: lazy LSP, отмена устаревших запросов, cache по revision, исключение build/vendor/generated деревьев, отдельные large-file режимы. Сохранить единственного владельца документа; не создавать второй LSP/index ради агента. Источник, доверие и свежесть контекста явные. | Rename/save/external edit инвалидируют нужные данные; diagnostics и patch используют актуальную версию. Большой репозиторий/файл не замораживает UI и не загружается целиком в prompt автоматически. |
| **BT8 — K5/fork workflow** | Сравнивать заимствования из Codex/Claude на нашем harness: 20–50 небольших реальных задач, повторные прогоны, одинаковые model/config/budget. Оценивать результат и запреты отдельно от стоимости/скорости. Upstream update — исходный diff + изменения contracts/dependencies + эти сценарии, не правка installed bundle. | Задачи «только исследуй», dirty-buffer conflict, Stop/restart, browser login/navigation, длинный контекст, ошибки tool и вредоносные инструкции не регрессируют ради экономии токенов. Проверяется состояние файлов/приложения, не фраза агента «готово». |

**Рекомендуемая последовательность уточнений:** BT1–BT4 формируют границы сохранности/доверия/совместимости; BT6 снимает главный риск принятого browser решения; BT5/BT7 доводят длительную работу; baseline BT8 запускается до изменений harness и повторяется при каждом обновлении форка. Это зависимости внутри существующего релиза, не отмена остальных потоков.

**Не обещать лишнего:**
- Worktree — изоляция рабочей копии, не security sandbox; environment, caches, внешние сервисы и неотслеживаемые данные требуют отдельной политики.
- OS sandbox, browser permissions и trust UI — разные границы. У Anthropic sandbox продукта и sandbox-runtime библиотеки различается матрица платформ; pin конкретной версии и proof macOS/Windows/Linux обязательны. Не выдавать WSL2 за поддержку native Windows.
- Локальная SQLite transaction не делает атомарными правки файлов, shell и сетевые effects. WAL требует локальной FS, checkpoint discipline и осознанного выбора durability; не мигрировать весь transcript на новый store без доказанной необходимости.
- TUF решает доверенную доставку metadata/артефактов, не compatibility приложения и не rollback БД. Заимствовать модель угроз/готовый verifier при выборе TUF, а не писать собственную криптосхему.

### 9.3. Новые продуктовые связки — кандидаты, не обязательства

**Принцип владельца (2026-10-18):** C1–C4 в этот релиз не берём — см. [PRODUCT_DIRECTIONS, «Принцип»](PRODUCT_DIRECTIONS.md#принцип-владелец-2026-10-18).

1. **C1. «Запустить проект».** Проверяемый recipe из команды, cwd, environment references и readiness condition; доверие до запуска. Start/Stop/Restart + принадлежащий задаче preview в одном месте. Не выполнять автоматически найденный `package.json` и не убивать чужой процесс по занятому порту. Секреты не записывать в recipe; URL из stdout не считать доверенной командой. Приёмка: проект запущен из Ombra, правильная вкладка открыта, конфликт порта объяснён, остановлены только свои процессы.
2. **C2. «Разобраться с этой ошибкой».** Из LSP/build/browser console передать выбранный diagnostic, stack и ограниченный контекст кода агенту. Общий формат с source location/revision/run ID; никаких дампов всех логов и cookie. Приёмка: агент получает именно выбранную актуальную ошибку, исправление проходит обычный diff/approval, результат проверен на соответствующей версии кода.
3. **C3. Рабочее место задачи.** Связать уже запланированные чат/worktree/checkpoints с editor tabs, browser tabs и dev-server ownership. Не копия домашнего каталога и не новый менеджер Git. Закрытие задачи не удаляет dirty/untracked файлы; cleanup с явным review. Приёмка: две задачи с одинаковыми именами файлов/портами не перепутаны, переключение и восстановление возвращают нужное окружение.
4. **C4. Инспектор контекста и проверок.** Показать, какие files/instructions/browser snapshots вошли в запрос, откуда они и насколько свежие, оценку бюджета; дать исключить источник. Рядом с review — что действительно запускалось, exit/result и revision/snapshot. Изменение кода инвалидирует старый зелёный результат. Это наблюдаемые входы/действия, не раскрытие внутреннего reasoning и не автоматическая долговременная память.

**Предпочтительный набор добавлений:** C1 + C2 — короткий пользовательский цикл «запустил → увидел проблему → исправил → проверил». C3 стоит поверх существующих worktrees/ownership, C4 — поверх versioned context/evidence; их расширенную UX-часть принимать отдельно, а не считать внезапно обязательной.

**Развитие уже запланированной диагностики, не пятая новая подсистема:** объединить self-test, diagnostics bundle и safe mode в понятный Doctor: несовместимый pack, фактический GPU backend/import, SDK, права записи, свободное место, зависший owner/request и ожидание approval. Локальная диагностика, preview экспорта, allowlist полей; код, prompts, credentials и cookie по умолчанию не экспортируются.

**Пока не добавлять сверх согласованного:** marketplace расширений, полную VS Code-совместимость, собственную универсальную vector DB, cloud agent farm и неограниченную фоновую автономию. Сначала доказать нужный сценарий/пользу; сервер, совместная работа и устройства из согласованного плана при этом не исключаются.

### 9.4. Общие сценарии приёмки беты

- Обновление на чистой машине и с предыдущего релиза; оборванная загрузка, неподходящий pack, неуспешная миграция, безопасное возвращение.
- Одновременная работа пользователя и агента в dirty документе; внешний editor; crash в момент save и восстановления очереди.
- Недоверенный repository, MCP/config change, prompt injection со страницы/входящего; запрет чтения секрета и запрещённого сетевого доступа проверяется вне модели.
- Только Ombra открыта: вкладки/restore/login/формы, agent/manual input, resize/DPI/IME, браузерный crash; standalone по желанию, без ручного pairing.
- Длительная сессия: несколько задач, большой stdout/history, 120 tabs, sleep/wake, reconnect, ограниченный диск и память; после закрытия нет потерянных данных и бесхозных собственных процессов.
- Web/Android/iOS проверяются своим реальным путём; отсутствие SDK/capability объясняется. Responsive web не выдаётся за Safari/iOS или полноценную эмуляцию устройства.
- Изменение после проверки снимает «проверено»; UI, правки, логи и отчёт относятся к одной версии. Результат model eval не подменяет smoke настоящей программы.

### 9.5. Первичные источники и пределы проверки

- [Anthropic: OS sandboxing](https://code.claude.com/docs/en/sandboxing), [sandbox-runtime](https://github.com/anthropics/sandbox-runtime): ограничения процесса и границы охвата инструментов; пакет не принимался как проверенная зависимость.
- [MCP SEP-1024](https://github.com/modelcontextprotocol/modelcontextprotocol/blob/main/seps/1024-mcp-client-security-requirements-for-local-server-.md): явное согласие на локальный server command; согласие не заменяет изоляцию.
- [Git worktree](https://git-scm.com/docs/git-worktree): несколько рабочих деревьев с общими данными repository.
- [TUF specification 1.0.33](https://theupdateframework.github.io/specification/v1.0.33/): threats freeze/rollback/mix-and-match и ротация доверия; ссылка на изученную версию, не утверждение о latest.
- [SQLite WAL](https://sqlite.org/wal.html): durability, checkpoints, single writer и ограничение network FS.
- [Anthropic: evals for agents](https://www.anthropic.com/engineering/demystifying-evals-for-ai-agents): outcome отдельно от transcript, повторяемость, изолированные trials, реальные задачи вместо жёсткой последовательности tool calls.

Выполнен временный .NET smoke **существующего** `DiagnosticsBundle.WriteAsync` из Debug `Ombra.dll`: экспорт в memory ZIP с синтетическим config. В valid case значения environment/apiKey/token-argument отсутствуют во всех экспортированных entries, обычный `approvalMode` сохранён; malformed config исключён без утечки синтетического секрета. Оба сценария PASS. Пользовательские config/logs/sessions не читались; runtime/provider/UI не запускались; временные файлы удалены. Результат подтверждает только эти пути ранее собранного бинарника, не всю redaction/privacy и не соответствие сборки текущему исходному коду. Остальные выводы — source review и предложения, а не выполненные browser/sandbox/update/performance проверки.

## 10. Принятое уточнение: Git-команда, общение и обмен инструментами

**Упрощено принципом владельца (2026-10-18), [PRODUCT_DIRECTIONS, «Принцип»](PRODUCT_DIRECTIONS.md#принцип-владелец-2026-10-18).** Приоритет у этой записи над текстом раздела ниже.
- Остаются: обмен по сценарию P5 (файл, skill, ссылка или вкладка, снимок, правка или патч, коллекция, метки со статусами) и привязка проекта к репозиторию.
- Не делаем: общение, сообщения, обсуждения, mentions и ленту активности (§10.2); MCP-рецепты, reproduction recipes, библиотеку ресурсов проекта и запрос merge от read-only участника (§10.3).
- **Не делаем в этом релизе (решение владельца, 2026-10-18):** Git-провайдер как источник личности, состава и прав команды (§10.1). Команды — только собственные: участник с ID и именем, создатель — владелец, владелец добавляет и удаляет участников, владение передаётся (PRODUCT_DIRECTIONS P1, P2). Привязка проекта по remote URL остаётся.

Статус: согласовано владельцем в обсуждении командной работы; реализация не начата. Для Git-проектов этот раздел уточняет P1–P4/K2/K4/B2 и заменяет сценарий повторного ручного создания той же команды. Это не трекер задач и не совместное владение одним агентом. Прежняя самостоятельная модель команд применима только к отдельно предусмотренным non-Git пространствам; D1 принят: обязательное E2EE с исключением доступа оператора сервера.

### 10.1. Участники и права

> **Заменено решением владельца (2026-10-18):** в этом релизе §10.1 не действует. Команды — только собственные (PRODUCT_DIRECTIONS P1/P2); Git-сценарий SERVER_DESIGN §4.4 и срез S8 — вне релиза. Текст ниже сохранён как задел на будущее.

- Репозиторий — общий контекст; Git-провайдер — источник подтверждённой личности и доступа. Ключи: provider host + stable repository/user ID, не display name/email или строка remote с credentials. Авторы `git log` не являются ACL; публичная доступность repository не делает всех читателей участниками команды.
- Открытие проекта и вход через провайдера связывают пользователя с командой без повторных приглашений и назначения Git-ролей в Ombra. Внутренние member/device IDs сохраняются для синхронизации, но отдельный публичный Ombra ID не нужен для основного Git-сценария.
- Показывать роли и участников только по доступным подтверждённым данным. Read-only токен не обязательно позволяет перечислить collaborators/admins. Для полного состава нужна разрешённая администратором интеграция; отсутствие разрешения отображается явно, не заменяется догадкой по истории commits.
- Права Git, возможность общения и разрешения локального агента различаются. Read-only участник может общаться и предлагать изменения, но не получает write/merge или право запускать инструменты коллеги.
- Revoke проверяется сервером при операциях, подписках и выдаче вложений. Доступ, который нельзя подтвердить после истечения срока проверки, не продолжать безусловно; учитывать задержку событий/доступность provider API. Уже скачанные копии отозвать нельзя.

### 10.2. Общение и видимость работы

- В Ombra и Nightly общие сообщения, обсуждения и входящие. Можно написать коллеге, отметить `@участника`, ответить в code/diff review или аннотации страницы, передать файл/картинку/URL/выделение.
- Лента проекта совмещает сообщения и активность: «работает над фичей», «мерджит ветку», «нужен ответ». Статус задаётся вручную или поступает из подтверждённого источника с временем/сроком актуальности. Git даёт commits/branches/PR/завершённый merge; неопубликованную работу и прогресс передаёт приложение с opt-in. Начатый merge не равен завершённому.
- Обсуждение привязано к объекту, а не обязательно к задаче. Код: repository + commit/base revision + path/range/excerpt; web: URL/элемент/контекст и при необходимости snapshot. Устаревший/неоднозначный anchor показывать явно.
- Уведомления прежде всего для личных сообщений, упоминаний и ответов. Не рассылать каждый tool call. Prompts, содержимое файлов, diff и подробный progress не публикуются автоматически.
- «Напрямую коллеге» — адресная отправка без обязательного workflow назначения задач, не требование P2P. Сервер доставки, durable outbox и дедупликация обеспечивают offline/reconnect. Ссылка localhost не открывает сервер отправителя у коллеги: передавать snapshot или доступный deployment URL; туннель не включать скрыто.
- Полученное сообщение/вложение не запускает агент или файл. Передача своему агенту — отдельное явное действие.

### 10.3. Обмен MCP, skills, кодом и ресурсами

Одинаковое действие «Поделиться → человек/проект → комментарий». Типизированное вложение сохраняет источник, автора, версию и привязку к проекту:

| Объект | Передаваемое содержимое и действие получателя |
|---|---|
| Код | Snippet с подсветкой, ссылка на commit/file либо patch с base commit; просмотр и обсуждение до применения |
| Файл/картинка | Предпросмотр, скачивание, обсуждение; только выбранные файлы, не доступ ко всей папке |
| MCP | Описание сервиса/пакета, transport, закреплённая версия локального пакета, команда/URL и необходимые параметры; отдельное подключение |
| Skill | `SKILL.md`, необходимые scripts/resources, версия, источник, зависимости; просмотр и добавление в проект |
| Reproduction recipe | Выбранные шаги, версия кода, команда, ожидаемый/фактический результат без автоматического дампа всей сессии |

- MCP передаётся как рецепт подключения, не копия секретов. Имена требуемых переменных и placeholders допустимы; tokens, cookies, `.env`, SSH keys и персональные пути не переносятся. Получатель авторизуется сам; общий service account требует отдельной управляемой политики доступа.
- Предпочитать существующие форматы MCP `server.json` и skills `SKILL.md`, адаптируя их к текущим возможностям omp. Не создавать второй независимый менеджер его конфигурации.
- Установка, активация и разрешения — разные действия. Показывать исполняемые scripts/команды и зависимости; участие автора в команде не выдаёт присланному коду права. Secret scanning — дополнительная проверка, не гарантия отсутствия всех секретов.
- Версии локальных ресурсов закреплены по неизменяемому содержимому; обновление показывает diff и не подменяет активную сессию молча. Удалённый MCP может изменить поведение при том же URL: адрес/объявленная версия не доказывают неизменность, изменения capabilities требуют пересмотра разрешений.
- В «Ресурсах проекта» различать «коллега поделился», «рекомендовано проектом», «подключено у меня». Пересылка не меняет настройки всей команды.
- Git хранит версионируемые skills/templates/MCP descriptions и состав рекомендованного набора; сервис — сообщения/активность/уведомления/вложения; локальный secure storage — секреты и персональные настройки. Библиотека отображает эти источники, не создаёт расходящуюся копию.
- Улучшение рекомендованного ресурса идёт через diff/PR с обычными правами repository. Обмен не должен обходить ACL исходного приватного репозитория; учитывать лицензии сторонних ресурсов.

Развитие поверх тех же объектов: «Настроить окружение проекта» показывает рекомендуемый набор, зависимости и необходимые входы; «Сохранить как skill» создаёт просматриваемый черновик из выбранных шагов, а не публикует весь разговор. Эти расширения не требуют marketplace и не должны задерживать базовый обмен.

### 10.4. Запрос на merge без write-доступа

- Действие «Попросить слить изменения» адресуется человеку с подходящими полномочиями. Это может быть write/maintain/admin в зависимости от провайдера и branch rules. Read-only не определяется автоматически бесплатным тарифом.
- При существующем PR/MR отправляется ссылка с контекстом и обсуждением; provider остаётся источником истины о review/checks/merge. Нативные review comments по возможности сохраняются там, без второго независимого ревью.
- При локальных изменениях: fork + PR, если разрешено; иначе выбранный patch с base commit отправляется уполномоченному коллеге. После просмотра тот создаёт ветку/PR под своими правами. Не выдавать отправленный patch за уже созданный PR.
- Получатель видит diff, base/head, целевую ветку и результаты проверок. Слияние требует явного подтверждения, актуальных прав, соблюдения checks/branch protection и проверки ожидаемого head; изменение diff после review не наследует старое подтверждение автоматически.
- Нет передачи токена администратора, привилегированного auto-merge от имени read-only участника или обхода запрета forks. Результат операции берётся из provider; при сетевом обрыве сначала выяснить исход, не повторять merge вслепую.

### 10.5. Приёмка и источники

1. Два участника одного Git-проекта находят друг друга без повторной регистрации команды; отсутствие разрешения на полный список явно показано.
2. Сообщение, mention в code/web, картинка и файл доходят между Nightly/Ombra, включая offline/reconnect, без дублей и утечки в другой проект.
3. MCP/skill пересылается без синтетических секретов; просмотр не запускает код; получатель подключает свою авторизацию. Обновление ресурса показывает diff и сохраняет локальные разрешения.
4. Read-only участник отправляет существующий PR или patch; уполномоченный коллега рассматривает и сливает его по правилам provider. Проверены запрет fork, конфликт/base drift, новый head, revoked access и недоступный API.
5. Изменение рекомендованного skill проходит review; опубликованная версия и локально активированная различимы. Общение и обмен работают без обязательных статусов задач.

Первичные источники: [GitHub roles](https://docs.github.com/en/organizations/managing-user-access-to-your-organizations-repositories/repository-roles-for-an-organization), [collaborators API и ограничения видимости](https://docs.github.com/en/rest/collaborators/collaborators#list-repository-collaborators), [MCP server.json](https://github.com/modelcontextprotocol/registry/blob/main/docs/reference/server-json/generic-server-json.md), [Anthropic skills](https://github.com/anthropics/skills). Проверены документы и текущие `OmpMcp.cs`/локальные `SKILL.md`; сетевой обмен, роли реального аккаунта, установка и merge не выполнялись. Это спецификация, не отчёт о реализованной функциональности.
