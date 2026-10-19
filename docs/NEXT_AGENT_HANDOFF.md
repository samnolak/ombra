# Передача следующему агенту — 2026-10-16

> **Обновление 2026-10-17:** работа возобновлена, всё, что ниже относится к O8/O12/O13, закрыто: 10 Markdown failures (неверные проверки в тестах), EOF/nested-height (native 572 против 570px), P2 cleanup в verifier, native 30-минутный soak. Текущий статус — в [NEXT_RELEASE_ROADMAP.md](NEXT_RELEASE_ROADMAP.md). Инструкция про рой ниже остаётся в силе.

## Почему остановились

Владелец попросил постепенно остановить работу и передать её другому агенту. Новые реализации и длительные прогоны остановлены. **Текущая партия O8/O12/O13 не принята, релиз не готов.** Не считать наличие кода завершённой приёмкой.

## Сначала прочитать

1. `docs/NEXT_RELEASE_ROADMAP.md` — выполненные подзадачи, оставшийся объём, release gates.
2. `docs/ARCHITECTURE_SOLUTIONS.md` — принятые D1/D2/D4 и уточнение D3. Собственный общий движок разрешён; старые формулировки «никогда не форкаем omp» не переопределяют принятое решение.
3. `docs/PRODUCT_DIRECTIONS.md` и `avalonia/docs/PARITY_ROADMAP.md` — требования и построчные статусы.
4. `avalonia/docs/PROJECT_STATE.md`, `avalonia/docs/USER_GUIDE.md` — состояние предыдущих принятых партий.

В рабочем дереве много накопленных изменений. Не делать reset/checkout/clean, не откатывать чужое и не считать весь diff результатом последней сессии. В этой сессии коммиты не создавались.

## Последняя принятая партия до текущих изменений

- O2/O5: persisted profile/root-scoped каталог с invalidation/reconcile и классы RPC deadlines; 162 targeted tests прошли после исправлений review.
- O10/O11: incremental subagent transcript через `fromByte`, bounded preview последних 40 записей, отмена при скрытии/закрытии; usage stage активного аккаунта через RPC, полный provider report только в Settings.
- O10/O11: 134 passed / 3 skipped / 0 failed, native macOS smoke и повторный независимый review. Skipped real-omp случаи не приняты.
- Более ранний полный клиентский прогон: 1716 passed / 40 skipped / 0 failed. **Он предшествует O2/O5, O10/O11 и текущей партии.** Не выдавать его за проверку нынешнего дерева.

## Текущие изменения

### O8 — coalescing tool snapshots

Файлы:
- `avalonia/src/Ombra.Rpc/RpcConnection.cs`
- `avalonia/src/Ombra.Rpc/RpcModels.cs`
- `avalonia/tests/Ombra.Tests/RpcConnectionTests.cs`
- `avalonia/tests/Ombra.Tests/SharedEngineTests.cs`
- `avalonia/tests/Ombra.FakeOmp/Program.cs`

`ToolUpdateReader` оборачивает прежнюю bounded event queue. Схлопывает только уже накопившиеся последовательные text-only `tool_execution_update` с одинаковыми `toolCallId`, `toolName`, `args`. Nonempty details, media, неизвестные поля, отдельные delta events, start/end и другие события сохраняются. `CoalescingBoundary` запрещает переход через consumed response/Intercept; `Sequence` остаётся последней исходной позицией, чтобы history fences работали.

Ограничение: **все wire events по-прежнему разбираются и ставятся в очередь**. Снижается количество доставок reducer при backlog, а не число первичных JSON parses. Не писать обратное в roadmap.

Fixture `tool-flood`: 2048 snapshots, midpoint delta/notice, итог `<prompt>: complete`; два логических чата используют одинаковый `toolCallId=shared-tool`. Используется настоящий subprocess/pipe, но движок fixture синтетический.

### O13 — длинные code blocks

Файлы:
- `avalonia/src/Ombra.App/Controls/MarkdownView.cs`
- `avalonia/src/Ombra.App/Controls/MarkdownHeight.cs`
- `avalonia/tests/Ombra.Tests/MarkdownViewRenderTests.cs`

После 28 строк: preview24 + Show more/Show less. Полное копирование, включая скрытый appended tail; expansion привязана к source position и сохраняется при append/theme/layout, сбрасывается при замене источника. Использованы существующие локализации. Diff/thinking этой партией не менялись.

Нативная проверка macOS **прошла для исходной реализации folding**: 24 строки; invoke expand/collapse; сохранение expansion при stream/theme; настоящий clipboard содержит весь скрытый хвост. Скриншоты просмотрены. Это не подтверждает последующие изменения nested height.

Независимый review нашёл nested-height дефект. Воспроизведён в настоящем окне до исправления: 4000 строк кода внутри quote → raw estimate **18018px**, фактический collapsed MarkdownView **570px**.

Внесена коррекция: parsed lazy blocks обходят существующий AST с fold state каждого descendant, raw line estimator распознаёт nested fence/indent без нового parser-per-row. Агент ожидает572px для этого примера; **после исправления нативный сценарий ещё не запускался**.

При повторном review обнаружен риск open-EOF: `MarkdownHeight.Estimate(Block,...)` использовал `block.Span.Length`, хотя незавершённый блок может иметь неполный Span. Статус последней точечной правки/оставшихся замечаний — в конце этого файла.

### O12 — новый performance executable, НЕ принятые бюджеты

Новые файлы:
- `avalonia/tools/verify/performance/Ombra.Performance.csproj`
- `avalonia/tools/verify/performance/Program.cs`

Изменён `avalonia/tools/mock-model/server.mjs`:
- optional `LONGTASK <minutes> id=<name>` изолирует clock разных запусков, ID сохраняется в compaction handoff;
- GET models отдаёт `x-ombra-stand-in: 1` для явной проверки fixture endpoint.

Verifier берёт из `--config` только executable/prefix, сам создаёт временные HOME/profile/project и notes-файлы, принудительно использует stand-in model. Требует loopback HTTP endpoint с marker header, запрещает redirects. Это предотвращает случайный длительный запрос платной модели из обычного профиля.

Замысел проверок:
- mixed user/assistant JSONL на10000 сообщений → корректный tail100 и первый layout/frame <1500ms;
- реальный общий runtime + scripted LONGTASK; 30s warm-up, затем заданная длительность;
- custom draw probe считает compositor render intervals, callbacks учитываются отдельно;
- native cadence: average>=59fps (погрешность границы60Hz) и p95<=20ms; это не OS presentation latency;
- headless имеет synthetic render tick, **не доказывает native60fps**;
- stall>=5s без новых RPC frames считается ошибкой;
- RSS drift<5% проверяется только при `--seconds 1800`; короткий запуск не закрывает30мин gate.

**Положительных результатов O12 пока нет. CI ещё не подключён. 30-минутный прогон не запускался.**

Первый запуск timeout ожидания history оказался проблемой устаревшего runtime fixture. После добавления fail-fast получена точная ошибка:

```text
Starting omp failed: engine_read_session failed: Unknown engine command: engine_read_session
```

Старый `.temp/runtime-installer-final/.../omp/engine/host.ts` не содержит текущего root read-only RPC. Для продолжения staging должен использовать **текущий `runtime/engine`**, а pinned Bun/node_modules можно взять из установленного fixture. Перед остановкой такой staging был подготовлен, но повторный запуск по нему ещё не выполнялся; временный staging затем удаляется.

## Последняя интеграционная проверка — НЕ зелёная

Сборка клиентского test project прошла. Последний targeted `dotnet test`:

**133 passed / 10 failed / 0 skipped / 143 total; 38s тестов.**

Фильтр:

```text
FullyQualifiedName~RpcConnectionTests|FullyQualifiedName~SharedEngineTests|FullyQualifiedName~MarkdownViewRenderTests|FullyQualifiedName~ChatScrollTests|FullyQualifiedName~ContextMenuUiTests|FullyQualifiedName~PagedHistoryTests|FullyQualifiedName~ReadOnlySessionTests
```

Все10 показанных failures относятся к новым Markdown-тестам:
1. 8 theory cases `Code_fold_boundary_builds_only_visible_lines_and_copies_every_line`: строка86 на момент прогона, `TextLayout.TextLines.Count`: expected24/28, actual1. Предшествующая проверка полного selected code прошла. Не удалять поведенческую проверку вслепую: проверить корректность layout/fixture; incidental implementation assertions не перепривязывать.
2. 2 cases `Nested_code_lazy_height_and_expansion_survive_scrolling_and_theme` (`quote`, `list`): строка197 на момент прогона, `Assert.Contains` marker не найден после scroll. Причина пока не установлена.

Повторно «подтверждать» эти failures не нужно: начать с диагностики/исправления и затем проверить после изменения. Последняя точечная EOF-коррекция могла попасть в исходники уже после этой сборки — считать её непроверенной.

## Как продолжить проверку

На этой машине .NET находится в `/Users/igo.shulga/.dotnet/dotnet`; apphost требует `DOTNET_ROOT=/Users/igo.shulga/.dotnet`. Для fake subprocess tests нужен также `DOTNET_HOST_PATH=/Users/igo.shulga/.dotnet/dotnet`.

Пример targeted команды из корня:

```sh
env DOTNET_ROOT=/Users/igo.shulga/.dotnet \
  DOTNET_HOST_PATH=/Users/igo.shulga/.dotnet/dotnet \
  OMBRA_SHOT_DIR="$PWD/.temp/performance-review/shots" \
  OMBRA_REVIEW_DIR="$PWD/.temp/performance-review/shots" \
  /Users/igo.shulga/.dotnet/dotnet test avalonia/tests/Ombra.Tests/Ombra.Tests.csproj \
  --filter 'FullyQualifiedName~RpcConnectionTests|FullyQualifiedName~SharedEngineTests|FullyQualifiedName~MarkdownViewRenderTests|FullyQualifiedName~ChatScrollTests|FullyQualifiedName~ContextMenuUiTests|FullyQualifiedName~PagedHistoryTests|FullyQualifiedName~ReadOnlySessionTests' \
  --verbosity minimal
```

Для performance создать чистый scratch под `.temp`, скопировать туда текущий `runtime/engine`, подключить его `node_modules` к pinned runtime dependencies, записать локальный config с command=pinned Bun и prefixArgs=`--no-env-file --no-install <staged-current-engine>/host.ts` (при необходимости явный `--config=<installed>/omp/bunfig.toml`). Не изменять установленный fixture на месте. `node` в PATH здесь отсутствует; `bun avalonia/tools/mock-model/server.mjs 18087` работает.

```sh
dotnet run --project avalonia/tools/verify/performance/Ombra.Performance.csproj -- \
  --config <runtime-config.json> --model-url http://127.0.0.1:18087/v1 \
  --work <repo>/.temp/performance-review/runs --headless --seconds 30
```

Сначала довести короткий real-runtime smoke. Затем native запуск без `--headless`; на macOS использовать `caffeinate -diu` для бодрствующего display. Только после него1800s gate. Не выдавать synthetic model за настоящий provider, headless за native, short run за soak.

Native folding lookup: `Button` class `md-code-fold`, AutomationId `MarkdownCodeFold`, `IInvokeProvider` или Space/Enter. Copy selector: `md-copy && !md-wrap` (wrap также имеет md-copy!). Syntax-highlighted `SelectableTextBlock.Text` может быть null: проверять `SelectAll()` / `SelectedText`, а не `.Text`.

CI: `.github/workflows/avalonia-ci.yml` сейчас manual-only из-за явно документированной политики стоимости Actions. `avalonia/tools/ci/real-omp-smoke.sh` подготавливает установленный runtime/stand-in config. Нельзя молча включать pull_request triggers и объявлять perf-lite PR-blocking: это отдельное согласование политики, либо честно оставить этот gate открытым.

## Инструкция про рой

- Один главный агент — владелец интеграции, smoke, docs/roadmap и итогового статуса. План и общие интерфейсы задаёт он.
- Сначала самому прочитать относящиеся к работе файлы. Делить на **2–4 независимые содержательные задачи**, а не запускать рой ради количества.
- Рекомендуемый следующий набор: (A) Markdown height/layout + регрессии; (B) performance harness/runtime/CI. RPC менять только при подтверждённом замечании review. Общие файлы закрепить за одним владельцем.
- В задаче каждому: точные пути, контракт, известный failure, acceptance, non-goals. Явно запретить build/test/lint/formatter во время параллельных edits. После source freeze главный делает единый build/targeted run и настоящий smoke.
- Специализации: `task` для investigate+edit, `reviewer` для независимого read-only review, `security-reviewer` для trust-boundaries, `scout` только для действительно неизвестной подсистемы после первичного собственного scoping.
- Не запускать один агент для мелкой правки/cleanup и не дублировать весь codebase audit. Не поручать двум исполнителям один файл без владельца интеграции.
- Учитывать существующие паттерны. При наличии LSP использовать его для references/definitions и экспортируемых изменений; предыдущая сессия configured server не нашла, новая должна проверить свою доступность.
- Spawn → продолжать собственную независимую работу. Не опрашивать статусы циклически. Результаты приходят автоматически; ждать лишь когда действительно нечего делать.
- После реализации независимый review отдельно от автора. Конкретный дефект воспроизвести до исправления, проверить после; закрывать finding только по фактам. Если пользователь просит остановиться — заморозить работу и записать незавершённое, не запускать новый цикл.
- Главный не принимает «agent says done» за smoke proof. Фиксировать точные passed/failed/skipped и ограничения. Обновлять roadmap после проверенной партии, не закрывая весь большой пункт по одной подзадаче.

Агенты этой сессии: `ToolUpdateCoalescing`, `FoldLongCode`, `CodeFoldReview`, `PerformanceGateReview`, `CoalescingReview`, `PerfFinalReview`. Их `agent://`/`history://` могут быть доступны только в текущей среде; этот файл не должен зависеть от их сохранности.

## Порядок следующего продолжения

1. Прочитать финальные замечания ниже и текущие изменённые участки; не начинать следующую крупную функцию.
2. Разобраться с10 Markdown failures и завершить повторный review height/EOF.
3. Запустить targeted интеграцию после source freeze и повторить native folding/nested-height smoke.
4. Довести O12 на текущем canonical engine; затем реальные бюджеты и CI-политика.
5. После приёмки обновить USER_GUIDE/PROJECT_STATE/PARITY_ROADMAP/NEXT_RELEASE_ROADMAP. Пока текущие O8/O12/O13 там не объявлены завершёнными.
6. Дальше следовать основному roadmap: RPC/performance и safety остатки, затем остальные K/P/B направления. Nightly, платформенные проверки и release gates не отменены.

Временный каталог этой сессии `.temp/performance-review` удалён; stand-in service остановлен, запущенные проверки завершились. Исходники реализации и этот handoff сохранены. Старый pinned runtime fixture и чужие `.temp` каталоги не удалялись.

## Финальные замечания при остановке

- **Source freeze:** все исполнители остановлены; новых проверок не запущено.
- **O8 review:** `CoalescingReview` не нашёл конкретного дефекта в просмотренном коде. Отдельный standalone smoke этой сессией не выполнен; процессные сценарии были в последней targeted выборке, failures в ней относятся к Markdown.
- **O13 EOF:** последняя точечная коррекция внесена, но не проверена: `Unbuilt` передаёт известный конец source slice; recursive estimator ограничивает дочерние узлы началом следующего sibling или концом enclosing source. Не полагается на неполный EOF `Block.Span.Length`. Нет отдельного EOF regression. Review concern основан на существующем комментарии/контракте Markdig, не на выполненном воспроизведении. Повторный reviewer не перечитывал окончательную правку после stop.
- **O13 тесты:** все10 описанных failures остаются открытыми. Не объявлять nested-height/EOF принятыми по ожидаемому числу572px или по ответу автора.
- **O12 последний review, открытый P2:** в `Program.cs` финальный `Directory.Delete(root, recursive: true)` предшествует `stop.Cancel()`. Если cleanup выбросит исключение, dispatcher cancellation не выполнится. Следующему агенту: гарантировать `stop.Cancel()` внутренним `finally`, отдельно сообщать cleanup failure, не заменяя исходную причину. Изменение намеренно не внесено после просьбы остановиться.
- Новый verifier собирался и запускался до последних guard/isolation изменений; успешная end-to-end проверка на текущем runtime отсутствует. Текущий source требует повторной сборки.
- Оставшийся объём релиза сохраняется в основном roadmap; пауза не отменяет ни один K/P/B пункт.
