# WORKAROUND.md

Соглашение как в [sungero-remote-component-example-react](https://github.com/DirectumCompany/sungero-remote-component-example-react).
Аудит: `rg "WORKAROUND-" src/ tools/ webpack.config.js`.

## Воркараунды

### WORKAROUND-01: z-index хардкод

- **Файлы:** `src/shared/arm.module.css` (`.dcp-ov`)
- **Проблема:** API хоста не даёт z-index порталов
- **Решение:** модалка `z-index: 10000`
- **Предлагаемое решение:** `--host-zindex-modal`

### WORKAROUND-03: Escape / фокус / скролл в модалке вручную

- **Файлы:** `src/widgets/w2-employees/employee-picker.tsx`
- **Проблема:** нет штатного Modal с focus trap
- **Решение:** Esc + restore focus + `body.overflow`
- **Предлагаемое решение:** хостовый `<Modal>` / `useModal()`

### WORKAROUND-05: React не шарим с хостом (по умолчанию)

- **Файлы:** `webpack.config.js`
- **Проблема:** стенд RX 26.2 → React 17; наш код на 18 (`createRoot`)
- **Решение:** бандл React 18 внутри RC; опционально `ARMGOV_SHARED_REACT=1` после
  проверки хоста ≥18 ([план](../docs/superpowers/plans/2026-09-18-rx-arm-shared-react.md))
- **Предлагаемое решение:** хост React 18+ → включить shared как в примере

## Закрыто

- ~~WORKAROUND-06~~ — CSS Modules (`arm.module.css` + `cx()`), токены/Tabler в `shared/styles/`
- ~~WORKAROUND-07~~ — `createPortal(document.body)`; стили Modules + токены на `:root`
