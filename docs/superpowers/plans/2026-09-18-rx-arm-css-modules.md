# rx-arm: переход на CSS Modules (как example-react)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Изоляция стилей через `*.module.css` + `eslint-plugin-css-modules`, как в [sungero-remote-component-example-react](https://github.com/DirectumCompany/sungero-remote-component-example-react), без потери визуала макета и `--theme_*`.

**Architecture:** Отказываемся от монолитного `arm.css` + `scope_css.py` как единственного источника. Стили живут рядом с компонентами (`org-orders.module.css`, `employee-picker.module.css`, …). Глобальные куски (Tabler `@font-face`, токены на `.rx-arm-root`) — в `src/shared/styles/tokens.css` / `tabler-icons.css`, подключаются из `arm-root` или loader. Классы макета (`armk`, `arme-c`, `dcp-*`) становятся локальными модулями; в JSX — `styles.armk` / `styles['arme-c']` или camelCase aliases.

**Tech Stack:** Webpack `css-loader` modules (как в примере), `eslint-plugin-css-modules`, Prettier, существующие `--theme_*` фоллбэки.

---

## File map

| Файл | Роль |
|---|---|
| `webpack.config.js` | правило `/\.module\.css$/` + обычный `.css` (как example) |
| `eslint.config.mjs` | `css-modules/no-undef-class` |
| `src/shared/styles/tokens.css` | `:root` / `.rx-arm-root` токены + Night |
| `src/shared/styles/tabler-icons.css` | `@font-face` + `.rx-arm-ti-*` |
| `src/shared/arm-root.tsx` | импорт tokens + tabler |
| `src/**/*.module.css` | стили виджетов/шелов/модалки |
| `tools/scope_css.py` | deprecate → удалить после миграции |
| `tools/assets/rx_style_block.css` | архив/референс, не runtime |
| `WORKAROUND.md` | закрыть WORKAROUND-06 и -07 (portal возможен) |

---

### Task 1: Webpack + ESLint под Modules

**Files:**
- Modify: `rx-arm/webpack.config.js`
- Modify: `rx-arm/package.json` (eslint-plugin-css-modules)
- Modify: `rx-arm/eslint.config.mjs`
- Create: `rx-arm/src/types/css-modules.d.ts` (как в примере)

- [ ] **Step 1:** Добавить в webpack два правила CSS (module / global), скопировать options `localIdentName` из example-react.
- [ ] **Step 2:** `npm i -D eslint-plugin-css-modules`, правило `css-modules/no-undef-class`.
- [ ] **Step 3:** `css-modules.d.ts` + include в tsconfig.
- [ ] **Step 4:** `npm run check` — зелёный (пока без миграции стилей).
- [ ] **Step 5:** Commit: `build(rx-arm): CSS Modules в webpack и eslint`

---

### Task 2: Вынести токены и Tabler из arm.css

**Files:**
- Create: `src/shared/styles/tokens.css`, `tabler-icons.css`
- Modify: `arm-root.tsx` / `widget-loader.tsx` — импорт
- Modify: `index.js` standalone — те же импорты

- [ ] **Step 1:** Вырезать блок `:root` + Night + font-face из `rx_style_block` / `arm.css` в новые файлы.
- [ ] **Step 2:** Подключить из `arm-root` (или loader до render).
- [ ] **Step 3:** Standalone + Night Preview — визуально без регрессии.
- [ ] **Step 4:** Commit: `refactor(rx-arm): токены и Tabler в shared/styles`

---

### Task 3: Мигрировать shared shell + dashboard layout

**Files:**
- Create: `card-shell.module.css`, `dashboard.module.css` (сетка `.rx-arm-cfgw-grid`, wrap, footer-link)
- Modify: `card-shell.tsx`, `dashboard.tsx`

- [ ] **Step 1:** Перенести правила карточки/сетки в modules; JSX на `styles.*`.
- [ ] **Step 2:** Скрин Default + Night vs текущий.
- [ ] **Step 3:** Commit: `refactor(rx-arm): CSS Modules для shell и dashboard`

---

### Task 4: Виджеты w1–w4 + employee-picker

**Files:** per-widget `*.module.css` + TSX

- [ ] **Step 1:** w1 `org-orders.module.css`
- [ ] **Step 2:** w2 `employees.module.css` + `employee-picker.module.css` (после Modules модалку можно вынести в `createPortal` + токены — закрыть WORKAROUND-07)
- [ ] **Step 3:** w3 `my-control.module.css`
- [ ] **Step 4:** w4 `process-health.module.css`
- [ ] **Step 5:** `npm run check:full` + визуал standalone
- [ ] **Step 6:** Commit: `refactor(rx-arm): CSS Modules для виджетов`

---

### Task 5: Убрать старый пайплайн

**Files:**
- Delete or archive: runtime dependency on `arm.css` / `scope_css.py` generation
- Modify: `WORKAROUND.md` — снять 06 (и 07 если portal сделан)
- Modify: `README.md` — описать Modules вместо scope_css

- [ ] **Step 1:** Удалить импорт `arm.css`; оставить `rx_style_block.css` только как референс в `tools/` (опционально).
- [ ] **Step 2:** Обновить README / WORKAROUND.
- [ ] **Step 3:** `npm run check:full`
- [ ] **Step 4:** Commit: `chore(rx-arm): убрать scope_css runtime, CSS Modules канон`

---

## Out of scope

- Полный редизайн под чистые `--theme_*` без макетных акцентов (`--create` navy).
- Переименование CSS-классов в BEM «с нуля» — сохраняем семантику `armk`/`arme`/`dcp` внутри modules.
