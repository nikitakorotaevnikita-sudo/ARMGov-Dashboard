# ArmGovDashboard — Remote Component (обложка модуля АРМ Руководителя)

React + Webpack Module Federation, scope **Cover**. Рендерит KPI региона (скелет: всего/в работе/просрочено),
данные — из server-функции `DirRX.ARMGovDash.GetRegionKpi` через `src/host-api.ts`.

## Структура
- `component.manifest.js` — манифест (control `ArmGovDashboard`, loader scope Cover).
- `src/loaders/armgov-dashboard-cover-loader.tsx` — монтирование в DOM обложки (cleanup на unmount).
- `src/controls/armgov-dashboard/armgov-dashboard.tsx` — контейнер (загрузка данных).
- `src/controls/armgov-dashboard/armgov-dashboard-view.tsx` — презентация (KPI-плитки, темы `--rndx-theme_*`).
- `src/host-api.ts` — получение данных (standalone → stub; remote → OData, **точный путь уточнить при интеграции**).
- `webpack.config.js` — Module Federation (`remoteEntry.js`), React singleton, metadata-plugin.

## Сборка (GATED — нужны приватные пакеты `@directum/*`)
В текущей сессии (репо прототипа) `node_modules` нет и приватный реестр `@directum/*` недоступен.
Сборку выполнять в воркспейсе HANDOFF (где настроен доступ к `@directum/*`):
```
npm install
npm run build:release      # → dist/remoteEntry.js + metadata.json
npm run start:dev:standalone   # отладка без стенда (host-api отдаёт stub)
```
Затем `dist/` положить в `Components` модуля и задеплоить вместе с решением (план Task 6/7).

## Открытый пункт
Точный транспорт RC→server (OData-действие над PublicFunction vs PublicApi) — сверить по эталону
`CRM/.../DirRX-CRMComponents` при интеграции; обновить `src/host-api.ts` и зафиксировать в research.
