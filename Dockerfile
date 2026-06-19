# ---- Сборка ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish armgov-standalone.csproj -c Release -o /app

# ---- Рантайм ----
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .

# В контейнере слушаем все интерфейсы (на localhost снаружи не достучаться).
ENV ARMGOV_PREFIX=http://*:5080/
# Секреты НЕ зашиты в образ — передавайте при запуске:
#   ENV ARMGOV_DB_PASSWORD, ARMGOV_LLM_TOKEN
# Прочие параметры (host БД, URL модели и т.д.) — дефолты в коде или config.json (volume).

EXPOSE 5080
ENTRYPOINT ["./armgov-standalone"]
