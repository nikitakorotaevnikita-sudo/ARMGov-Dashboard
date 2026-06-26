---
name: promote-to-rx
description: Use when a feature has been validated/confirmed on the ARMGov web prototype and needs to be ported into the productive Directum RX build (the rx-native/ module). Triggers — "подтверждено, тащим в RX", "перенеси фичу в продуктив", "promote to RX".
---

# Promote feature: web prototype → productive RX

## Overview
Проект ARMGov ведётся в два контура (см. memory `prototype-to-rx-workflow`): web-прототип
(`Program.cs`/`index.html` в корне) — для отработки и демо; продуктив — нативный модуль Directum RX
в `rx-native/` (`DirRX.ARMGovDash`: Remote Component в обложке + server-функции).
Этот скилл переносит **подтверждённую** фичу из веб-прототипа в продуктив **по пайплайну**,
переиспользуя **логику** (не код) и переписывая под нормы RX.

**Главное правило: переносим ЛОГИКУ, а не код. Веб-прототип НЕ трогаем (он для демо).**

## When to use
- Заказчик/пользователь подтвердил фичу на веб-прототипе и просит перенести её в RX-продуктив.
- НЕ использовать для незавершённых/неподтверждённых фич (их продолжаем на вебе).

## Вход
- Имя подтверждённой фичи (например: «тематики обращений», «прогноз по тренду», «исключение уведомлений»).
- При неоднозначности — уточнить у пользователя, какие блоки/эндпоинты прототипа относятся к фиче.

## Процедура

1. **Локализовать фичу в прототипе.** Найти в `Program.cs` server-логику (эндпоинт/Build*-функция, SQL)
   и в `index.html` рендер (что показывает, drill). Зафиксировать: какие данные считаются и как, какой UX.

2. **Поднять нормы RX.** Прочитать `rx-native/.pipeline/01-research/research.md` (чек-лист антипаттернов)
   и `02-design/design.md` (архитектура: RC в обложке, server-функции, SQL-API + Cache, без PrBI).

3. **Написать план** в `rx-native/.pipeline/03-plan/<feature>-plan.md`:
   - какие server/public-функции нужны и их сигнатуры; **DTO = Structures** (не анонимные типы);
   - перенос SQL-логики прототипа → платформенный SQL-API (`SQL.CreateConnection`/`Queries.xml`) + `Cache`;
   - что добавить в Remote Component (React-блок), как RC получает данные;
   - шаги, порядок, критерии готовности (smoke).

4. **Заготовка реализации** в `rx-native/.pipeline/04-implementation/` (или в исходниках модуля, когда он есть):
   server-функция + кусок RC под нормы RX. Без деплоя (если стенд не готов — только код).

5. **Гейт антипаттернов** (обязательно, из research.md): нет `new {}` → Structures; `AppliedCodeException`
   (не native throw); `.Where()` до `.ToList()`; нет `Count/Sum` на `GetAll()` без `Where`; `Calendar.Now`;
   нет `static`/`out`/`is`/`as`/`System.Data`; роли по `Sid`; бюджет запросов; enum без пустых `Code`;
   `.mtd` не править руками; кэш для тяжёлых агрегатов.

6. **Не трогать** `Program.cs`/`index.html` прототипа. **Не деплоить** без готового стенда и admin-UAC + бэкапа.

## Quick reference
| Что | Куда |
|-----|------|
| Логика метрик (источник) | прототип `Program.cs` (Build*-функции, SQL) |
| UX (источник) | прототип `index.html` (render*-функции) |
| Нормы/антипаттерны | `rx-native/.pipeline/01-research/research.md` |
| Архитектура | `rx-native/.pipeline/02-design/design.md` |
| План фичи (выход) | `rx-native/.pipeline/03-plan/<feature>-plan.md` |
| Реализация (выход) | `rx-native/.pipeline/04-implementation/` или исходники модуля |

## Common mistakes
- **Скопировать C#/JS прототипа как есть** → нарушит нормы RX (анонимные типы, ADO.NET, DateTime.Now). Переписывать.
- **Прямой тяжёлый SQL без Cache** → «бьёт по БД» (причина, по которой прототип не берут за основу). Кэшировать.
- **Тронуть веб-прототип** → он нужен для демо; перенос только из него, без правок в нём.
- **enum с пустым `Code`** → краш WebServer (прошлый инцидент). Избегать enum или задавать `Code` через DDS.
