# ONI-01 — подготовка для Ombra

Начать с [HANDOFF.md](HANDOFF.md) и [задачи агенту](AGENT_TASK.md). Активная версия: 15 октября 2026, радость v11. Поставляемый автономный каталог — `handoff-candidate/`; название сохранено для совместимости рабочего процесса.

`node serve-preview.cjs` открывает просмотрщик на localhost:4186; `/review` содержит четыре записи с перемоткой. Экспорт сохраняется в `exports/` проекта.

[Аудит подготовки](HANDOFF_PLAN.md), [технический review](REVIEW.md), [контракт runtime](runtime-contract.json). Нативное приложение не изменено; его приёмочные проверки описаны в HANDOFF.

Авторская пересборка: `node build-states.cjs` (нужен Sharp). Поставка: `node build-handoff.cjs`, затем `node handoff-candidate/verify-package.cjs`. Для запуска поставки npm-зависимости не нужны.

Старые build-preview/build-rig/build-frames, QA, HISTORY и studies/archive — история, не текущая поставка. Не запускать старые сборщики поверх preview.html. Исходные требования и ограничения сохранены в BRIEF и approved/README.

Компактный production-набор: `runtime/`, сборка `node build-runtime.cjs`. Его не следует путать с полным handoff с проверочными видео. Оптимизация — OPTIMIZATION.md.
