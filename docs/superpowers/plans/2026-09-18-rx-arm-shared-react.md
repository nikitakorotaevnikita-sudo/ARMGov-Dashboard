# rx-arm: shared React со стратегией (шаблон example-react)

**Goal:** Вернуть `shared: { react, react-dom }` как в sungero-remote-component-example-react, не роняя Cover на стенде.

## Факт сейчас

- RC зависит от React **18.2.0** (`createRoot`).
- В `webpack.config.js` React **не** в `shared` (WORKAROUND-05): на стенде RX **26.2** ранее ловили singleton React **17** → `createRoot` undefined.
- Пример Directum шарит React 18.2.0 без `singleton: true` явно (webpack всё равно часто схлопывает).

## Проверка на стенде (сделать до кода)

1. Открыть обложку RX с любым рабочим RC или пустую.
2. DevTools Console:
   ```js
   // если хост что-то выставил в shared scope — варианты:
   window.React?.version
   // или в Sources найти chunk хоста с react.production
   ```
3. Network: JS хоста, поиск `react.version` / `17.` / `18.`.
4. Зафиксировать: **версия React хоста** + **есть ли already shared singleton**.

## Стратегии

| # | Условие | Действие |
|---|---|---|
| A | Хост **≥ 18** и отдаёт shared | Включить `shared` как в примере; удалить WORKAROUND-05 |
| B | Хост **17** | Либо оставить WORKAROUND-05, либо даунгрейд RC на React 17 + `ReactDOM.render` (отход от шаблона API) |
| C | Хост 17, но нужен шаблонный shared | Невозможно безопасно без (B) или обновления платформы |

**Рекомендация до проверки:** не включать shared «вслепую». После замера на стенде — A или оставить 05.

## Если стратегия A (чеклист)

```js
shared: {
  react: { requiredVersion: dependencies.react },
  'react-dom': { requiredVersion: dependencies['react-dom'] },
},
```

- [ ] `dependencies` из `package.json` в webpack (как example)
- [ ] `build:release` + загрузка на стенд
- [ ] Smoke: контрол монтируется, тема Night, модалка сотрудников
- [ ] Удалить WORKAROUND-05 из `WORKAROUND.md` и комментария webpack
- [ ] Commit: `fix(rx-arm): shared react/react-dom как в example-react`

## Если стратегия B (хост 17 надолго)

- Зафиксировать в WORKAROUND-05 версию стенда и дату проверки.
- Не считать отклонение от шаблона «недоделкой» — блокер платформы.
- Следить за релизом RX-веб с React 18 → тогда A.
