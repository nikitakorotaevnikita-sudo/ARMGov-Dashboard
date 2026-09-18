# rx-arm — АРМ руководителя как сторонний контрол Directum RX

Экран MVP 1-го этапа («обзор поручений» для руководителя ПТО) в виде штатного стороннего
контрола RX-веб: Remote Component, Webpack Module Federation, scope `Cover`.

Канон инфраструктуры — [sungero-remote-component-example-react](https://github.com/DirectumCompany/sungero-remote-component-example-react).
Отличия от шаблона и намеренные хаки — ниже и в [WORKAROUND.md](./WORKAROUND.md).

**Источник вёрстки — макет `2026-09-04-mvp-screen-rx_v2_fixed` (`fixed (2).mhtml`).** Дизайн,
лэйаут, цвета, отступы и глифы перенесены дословно; правило работы: _макет главнее текста_.
Если в коде что-то расходится с макетом и это не описано ниже в «Отличиях» — это дефект,
а не улучшение.

**Один контрол на весь экран, а не по контролу на блок.** Так порядок блоков и их ширина
(все четыре — во всю ширину, 12 колонок) фиксируются самим компонентом и не зависят от того,
как редактор обложки разложит контролы по колонкам.

## Что внутри

| Блок                                | Папка                            | Содержимое                                   |
| ----------------------------------- | -------------------------------- | -------------------------------------------- |
| 1. Поручения организации            | `src/widgets/w1-org-orders/`     | четыре KPI-плитки по срокам                  |
| 2. Статус исполнения по сотрудникам | `src/widgets/w2-employees/`      | карточки сотрудников + диалог выбора состава |
| 3. Мои контрольные поручения        | `src/widgets/w3-my-control/`     | вкладки-фильтры, 5 строк, «Показать все»     |
| 4. Здоровье процесса «Поручения»    | `src/widgets/w4-process-health/` | таблица с полосой здоровья                   |

Анатомия папки виджета — как в [`rx-cover`](../rx-cover/): `types.ts` (контракт данных),
`data.ts` (демо-датасет и вычислительная логика), `<name>.tsx` (компонент, kebab-case;
eslint `check-file`).

- `src/dashboard/dashboard.tsx` — экран целиком: сетка, четыре блока, диалог.
- `src/dashboard/widget.tsx` — контрол-обёртка для реестра загрузчиков.
- `src/shared/arm.module.css` + `cx()` — вёрстка макета как CSS Modules.
- `src/shared/styles/tokens.css`, `tabler-icons.css` — токены/`--theme_*` и Tabler
  (глобальный CSS, не Modules).
- `src/shared/card-shell.tsx` — карточка с шапкой.
- `src/shared/host-actions.ts` — `safeExecuteAction` (`canExecuteAction` перед вызовом).
- `src/shared/rx-icons/` — цветные SVG шапок из UI kit RX («Обложка»).
- `src/loaders/widget-loader.tsx` — фабрика cover-загрузчиков; `onControlUpdate` → тема/культура.
- `src/standalone/preview.tsx` + `index.js` — превью без хоста (ширина + Default/Night).

Статический HTML-референс (не бандл): `_tmp-mockup/` в корне репозитория
(`arm-mockup-single.html` — один файл для сверки).

## Отличия от макета — осознанные, других нет

1. **Медиа-запрос → контейнерный запрос** (`@container` на `.wrap`). Диалог — `createPortal`
   в `document.body` (CSS Modules + токены на `:root` / `.rx-arm-root`).
2. **Вкладки блока 3 работают, и их счётчики считаются по данным.** В макете вкладки —
   картинка со счётчиками «Все 12 / Просрочено 3 / Срок сегодня 1», а строк всего пять.
   Чтобы метрики сходились друг с другом (нерушимое правило проекта, см. корневой `CLAUDE.md`),
   в демо-датасете лежат все 12 поручений; вид по умолчанию совпадает с макетом строка в строку.
3. **Поведение диалога доведено до рабочего**: черновик выбора, «Применить» перерисовывает
   блок, «Отменить»/Esc/×/клик по фону откатывают, поиск фильтрует, пустой результат —
   «Ничего не найдено».
4. **Подложку рисует хост, не контрол.** Корень `.rx-arm-root` прозрачный.
5. **Ограничение ширины снято** (`max-width:1440` убран) — ширину задаёт область обложки.
6. **В карточке сотрудника нет ссылки «Поручения в RX».** Провал по клику на карточку — позже.
7. **Акцент risk-карточки — левая грань** (`border-left`), как в UI kit RX; в исходном `.mhtml`
   была цветная «шапка» сверху. HTML-макет в `_tmp-mockup` уже с левым акцентом.

`tools/scope_css.py` / `tools/assets/rx_style_block.css` — **архив** пайплайна до CSS Modules
(референс токенов/правил). Runtime-стили — `arm.module.css` + `shared/styles/`. Не править
`arm.css` (удалён): правки UI — в Modules или токенах.

## Отличия от шаблона example-react

| Тема           | Шаблон                     | У нас                                                                                                                                           |
| -------------- | -------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------- |
| Shared React   | в `shared` MF              | **по умолчанию выкл.** (WORKAROUND-05): хост RX 26.2 ≈ React 17, код на 18. Включить: `ARMGOV_SHARED_REACT=1` в `.env` после проверки хоста ≥18 |
| Стили          | CSS Modules                | то же; плюс глобальные `tokens.css` / Tabler                                                                                                    |
| MiniCssExtract | `insert: prepend`          | то же — CSS RC выше стилей хоста                                                                                                                |
| Модалка        | хостовый Modal / свои хаки | `createPortal` + WORKAROUND-01/03 (z-index, Esc/focus/scroll)                                                                                   |
| Имена файлов   | kebab-case                 | то же (`check-file`)                                                                                                                            |
| Хост-действия  | `canExecuteAction`         | `safeExecuteAction` в `host-actions.ts`                                                                                                         |
| Scope          | примеры Card/Cover         | один Cover-контрол на весь MVP-экран                                                                                                            |

## Иконки

Глифы шапок виджетов — цветные SVG из UI kit Directum RX (набор «Обложка»),
`src/shared/rx-icons/`, в бандл как data-URI. Системные
`settings, search, x, check, arrow-narrow-right` — сабсет Tabler Icons 3.11.0 (MIT) в
`shared/styles/tabler-icons.css`. Новый глиф Tabler = пересобрать сабсет и дописать
`.rx-arm-ti-<name>:before`. Новый глиф RX = экспорт из Figma «Обложка» →
`tools/extract_picked_icons.py` → `src/shared/rx-icons/`.

Классы UI через CSS Modules (`cx('arme-c')`); корень `.rx-arm-root` держит токены темы
(`data-theme` / `--theme_*`).

## Сборка и запуск

| Скрипт                         | Назначение                                                   |
| ------------------------------ | ------------------------------------------------------------ |
| `npm run start:dev:standalone` | Hot-reload локально: webpack watch + `serve` на `:3003`      |
| `npm run build:dev:standalone` | Dev-сборка standalone → `dist/`                              |
| `npm run build:dev:remote`     | Dev remote; при `.env` — сразу в папку компонента на сервере |
| `npm run start:dev:remote`     | Watch + remote-сборка (нужен `.env`)                         |
| `npm run build:release`        | Прод → всегда `dist/`                                        |
| `npm run check`                | typecheck + lint + test + `build:dev:remote`                 |
| `npm run check:full`           | prettier + typecheck + lint + test + `build:release`         |
| `npm run test`                 | Jest: loader / `onControlUpdate`, `safeExecuteAction`        |
| `npm run format`               | prettier --write + eslint --fix                              |

```bash
npm install
cp .env.example .env   # для start:dev:remote / build:dev:remote на стенд
npm run start:dev:standalone
```

Нужен доступ к пакетам `@directum/sungero-remote-component-*`.
Намеренные хаки платформы — в [WORKAROUND.md](./WORKAROUND.md) (`rg "WORKAROUND-" src/ tools/ webpack.config.js`).

## Деплой

### Разовый (Dev Studio)

1. `npm run build:release` → `dist/` (`remoteEntry.js`, `metadata.json`, chunks, css).
2. Разместить `dist/` на URL, доступном веб-клиенту RX.
3. Зарегистрировать в RX: **Настройки → Компоненты → Добавить** → URL `…/metadata.json`.
4. На обложке модуля: **Добавить → Специальный контрол** →
   `ARMGov_LeaderCover_1_0.LeaderDashboard` (scope Cover).

### Hot-reload на стенд

1. `.env` из `.env.example` (`SUNGERO_DEPLOY_BASE`, `SUNGERO_SOLUTION_NAME`,
   `SUNGERO_COMPONENT_NAME`; опционально `ARMGOV_SHARED_REACT`).
2. `npm run start:dev:remote` → бандл пишется в
   `{BASE}/{SOLUTION}.Components/{COMPONENT}`; в браузере F5.

## Открытые пункты (проверять на стенде)

- **Данные.** Сейчас всё из демо-пресетов `data.ts`. Контракты (`types.ts`) написаны под
  подключение к источнику; сам источник (API дашборда или прикладной слой RX) не выбран.
  Хост-API Cover: `onControlUpdate` уже синхронизирует тему/культуру (`ArmRoot` /
  `data-theme`); `executeAction` / данные — позже.
- **Рамка карточки.** Как и в `rx-cover`, карточку рисует сам виджет. Проверить, не рисует ли
  группа хоста поверх свою рамку/тень — будет дубль.
- **Внешние отступы.** Корень контрола прозрачный (подложку даёт хост), но
  padding обёртки остался — поверх полей области обложки отступы могут быть шире макетных.
  Если на стенде заметно — обнулить.
- **Тема хоста.** `onControlUpdate` → `data-theme` на `ArmRoot`; поверхности/текст/ссылка
  через `var(--theme_*, …)` (фоллбэки макета). Standalone Preview инжектит те же
  `--theme_*`, что стенд RX. Акцент `--create` (KPI) в Default — макетный navy; в Night —
  осветлённый (`tokens.css`).
- **Shared React.** Не включать `ARMGOV_SHARED_REACT=1`, пока на стенде не подтверждён React ≥18.
- **Фото сотрудников** — стоковые, вшиты в бандл (~80 КБ base64). На стенде заменяются
  ссылками на фото из карточек сотрудников RX.
