# АРМ руководителя — аналитика процессов (standalone)

Read-only дашборд над БД Directum RX: стратегический обзор региона, аналитика по процессам
(поручения, обращения граждан, НПА), ИИ-сводка + чат и раздел «Бэк-офис» для настройки
подключений. .NET 10, без установки в RX. Только запросы `SELECT` к БД, ничего не пишет.

## Запуск

**На любой машине Windows x64 (готовый бандл, .NET ставить не нужно):**
1. Собрать бандл: `dotnet publish -c Release -r win-x64 --self-contained true -o dist`
2. Скопировать папку `dist/` (плюс при необходимости `config.json` — см. ниже) на целевую машину.
3. Запустить `dist\armgov-standalone.exe` или **`run.bat`** (он откроет браузер сам).
4. Открыть `http://localhost:5080/`.

**`run.bat`** — лаунчер: запускает сборку из `dist/` (или `bin/Release/...`) и открывает браузер.

Сетевые требования (данные тянутся вживую): доступ к PostgreSQL и к LLM-эндпоинту
(адреса задаются в Бэк-офисе). Без сети страница откроется, но метрики/ИИ не загрузятся.

## Бэк-офис и конфигурация

Подключения берутся из **`config.json`** рядом с exe (в `.gitignore`, в репозиторий не попадает).
Редактируется прямо в вебе: раздел **«⚙ Бэк-офис»** — адрес/порт/база/пользователь/пароль БД,
URL/модель/токен LLM, ссылка в RX. Кнопка «Проверить подключение» пингует БД и модель.
Секреты (пароль БД, токен) в браузер не возвращаются (маскируются); пустое поле = «не менять».

В исходном коде секретов нет — дефолты в `Program.cs` без пароля/токена.
Можно переопределить через переменные окружения (удобно для Docker):
`ARMGOV_PREFIX`, `ARMGOV_DB_PASSWORD`, `ARMGOV_LLM_TOKEN`.

Пример `config.json`:
```json
{
  "Prefix": "http://localhost:5080/",
  "RxBase": "http://<rx-host>/Client/#/",
  "Db":  { "Host": "<host>", "Port": "5432", "Database": "<db>", "Username": "<user>", "Password": "<pass>" },
  "Llm": { "Url": "https://<llm>/v1/chat/completions", "Model": "<model>", "Token": "<token>" }
}
```

## Docker

```
docker build -t armgov-dashboard .
docker run -p 5080:5080 \
  -e ARMGOV_DB_PASSWORD=*** \
  -e ARMGOV_LLM_TOKEN=*** \
  armgov-dashboard
```
или `docker compose up --build` (см. `docker-compose.yml`; секреты — через env хоста или `.env`).
В контейнере слушается `http://*:5080/` (ENV `ARMGOV_PREFIX`). Секреты в образ не зашиваются.

## Сборка для разработки

Нужен .NET 10 SDK.
```
dotnet build -c Release
bin\Release\net10.0\armgov-standalone.exe
```
`index.html` читается при каждом запросе — после правки фронта достаточно скопировать его
в выходную папку: `copy index.html bin\Release\net10.0\index.html`.

## Структура

- `Program.cs` — backend: HTTP-сервер (HttpListener), все `/api/*`-эндпоинты, конфиг, вызовы LLM (server-side).
- `index.html` — весь фронтенд (SPA, офлайн SVG/CSS): обзор, процессы, ИИ-сводка/чат, Бэк-офис.
- `tokens.css`, `logo-directum.svg` — дизайн-система Platform.ОГВ/Directum.
- `lib/` — вендоренные сборки (`Npgsql`, `Microsoft.Extensions.Logging.Abstractions`), в репозитории.
- `run.bat` — лаунчер. `Dockerfile`, `docker-compose.yml`, `.dockerignore` — контейнеризация.
- `config.json` — подключения с секретами (в `.gitignore`; задаётся через Бэк-офис).
- `docs/specs/` — проектные спецификации. `dist/` — собранный бандл (артефакт, не в git).
