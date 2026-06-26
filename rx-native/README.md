# rx-native — нативная реализация ARMGov в Directum RX

Отдельная ветка работы: перенос ценности ARMGov в **нативный модуль Directum RX**
(Remote Component в обложке модуля), **с нуля**. Standalone-прототип в корне репозитория
(`Program.cs`/`index.html`) **не трогаем** — он используется для демо.

Документация и разработка — по пайплайну в `.pipeline/`:

| Этап | Папка | Статус |
|------|-------|--------|
| Research (среда + антипаттерны RX) | `.pipeline/01-research/research.md` | ✅ |
| Design (архитектура, решения) | `.pipeline/02-design/design.md` | ✅ на ревью |
| Plan (план реализации скелета) | `.pipeline/03-plan/` | ⬜ далее |
| Implementation (код модуля + RC) | `.pipeline/04-implementation/` | ⬜ |

Цель первой итерации — **сквозной скелет**: модуль `DirRX.ARMGovDash` + 1 server-функция данных +
RC в обложке, рендерящий её. Деплой — позже (нужен стенд + admin-UAC + бэкапы).
