# Akasha — Deployment Guide

## 1. Server (VPS, Docker) — рекомендуемый путь

### docker-compose

```yaml
services:
  postgres:
    image: pgvector/pgvector:pg16
    environment:
      POSTGRES_DB: akasha
      POSTGRES_USER: akasha
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
    volumes: ["akasha-pg:/var/lib/postgresql/data"]
    restart: unless-stopped

  akasha:
    build: .
    ports: ["127.0.0.1:5077:5077"]   # наружу только через reverse-proxy (Caddy/nginx + TLS)
    environment:
      AKASHA_MODE: server
      ConnectionStrings__Akasha: "Host=postgres;Database=akasha;Username=akasha;Password=${POSTGRES_PASSWORD}"
      AKASHA_OWNER_EMAIL: ${AKASHA_OWNER_EMAIL}
      AKASHA_OWNER_PASSWORD: ${AKASHA_OWNER_PASSWORD}
      AKASHA_MASTER_KEY: ${AKASHA_MASTER_KEY}          # base64, 32 байта — шифрование секретов
      AKASHA_TELEGRAM_TOKEN: ${AKASHA_TELEGRAM_TOKEN}  # опционально — бот аппрувов
      AKASHA_TELEGRAM_CHAT_ID: ${AKASHA_TELEGRAM_CHAT_ID}
    depends_on: [postgres]
    restart: unless-stopped

volumes: { akasha-pg: {} }
```

### Подготовка

```bash
# master key
openssl rand -base64 32            # → AKASHA_MASTER_KEY
docker compose up -d
docker compose logs akasha          # схема создаётся автоматически (EnsureCreated/миграции)
curl -s localhost:5077/healthz     # {"status":"ok"}
```

Открыть `https://host/` → login владельцем → создать агента (UI Agents или `POST /api/admin/agents`) — ключ показывается один раз.

### Reverse-proxy (Caddy)

```
your.domain {
    reverse_proxy 127.0.0.1:5077
}
```

## 2. Portable (флешка/ноутбук, офлайн)

Self-contained публикация под три ОС:

```bash
dotnet publish src/Akasha.Server -c Release -r win-x64   --self-contained -o dist/win
dotnet publish src/Akasha.Server -c Release -r linux-x64 --self-contained -o dist/linux
dotnet publish src/Akasha.Server -c Release -r osx-arm64 --self-contained -o dist/macos
```

Запуск: `./akasha portable` — ищет `akasha.db` рядом с бинарником (SQLite WAL), стартует на `http://127.0.0.1:5077`, открывает браузер. Внешних зависимостей нет; эмбеддинги — локальный hashed-ngram fallback (ONNX-модель опционально, каталог `models/`).

Синхронизация с сервером:

```bash
./akasha sync --server https://your.domain --key ak_xxx_yyy
# pull→apply→push→commit cursor; секреты/агенты не участвуют (см. sync-protocol.md)
```

## 3. Конфигурация (ENV)

| Переменная | Режим | Назначение |
|---|---|---|
| `AKASHA_MODE` | оба | `server` \| `portable` |
| `ConnectionStrings__Akasha` | server | Npgsql строка |
| `AKASHA_SQLITE_PATH` | portable | путь к akasha.db (default: рядом с exe) |
| `AKASHA_OWNER_EMAIL` / `AKASHA_OWNER_PASSWORD` | server | bootstrap владельца (Argon2id при первом старте) |
| `AKASHA_MASTER_KEY` | оба | AES-GCM ключ секретов (base64 32B). **Без него секреты недоступны** |
| `AKASHA_URLS` | оба | bind address (portable жёстко 127.0.0.1) |
| `AKASHA_PORT` | docker-compose | порт веб-интерфейса (внутри контейнера и на хосте), default `8080` |
| `AKASHA_HOST` | docker-compose | адрес хоста для проброса порта: `127.0.0.1` (только reverse-proxy, default) или `0.0.0.0` |
| `AKASHA_TELEGRAM_TOKEN` / `AKASHA_TELEGRAM_CHAT_ID` | server | бот-аппрувы |

## 3.1 Настройка портов (docker-compose)

Порт задаётся в `.env`, менять `docker-compose.yml` не нужно:

```bash
# .env
AKASHA_PORT=9090          # если 8080 занят другим сервисом
AKASHA_HOST=127.0.0.1     # default; 0.0.0.0 — открывать напрямую без reverse-proxy
```

Применить: `docker compose up -d` (пересоздастся только контейнер akasha).

Проверить, кто занял порт на хосте: `ss -ltnp | grep 8080`.

Если Akasha должна быть доступна напрямую по IP VPS (без домена/Caddy):
`AKASHA_HOST=0.0.0.0 AKASHA_PORT=9090` → `http://ВАШ_IP:9090` (не забудьте пароль владельца понадежнее и фаервол: `ufw allow 9090/tcp`).

## 4. Backup & restore

- PostgreSQL: `pg_dump akasha` cron'ом + копия на объект хранения.
- Portable: файл `akasha.db` (+WAL checkpoint) — просто скопировать.
- Переносимый формат: `GET /api/admin/export` → zip `akasha-export-v1` (JSON+Markdown, читаем без Akasha); импорт идемпотентен (`POST /api/admin/import`). Секреты в экспорт попадают только зашифрованными и только по явному флагу.

## 5. Обновление

```bash
docker compose build && docker compose up -d   # миграции EF применяются на старте
```
Схема обратно совместима внутри v1; breaking-изменения сопровождает export→import путь.

## 6. Требования

- .NET SDK 9 для сборки; runtime не нужен при self-contained.
- 1 CPU / 512 MB RAM достаточно (личный объём данных); pgvector расширение входит в образ.
- TLS обязателен для server-режима (ключи агентов ходят в Authorization header).
