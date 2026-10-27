# Akasha — Установка и развёртывание

Полное руководство по установке сервера, сборке portable-версии, синхронизации и бэкапу.
Актуально для кода в репозитории (не сверяйся с README.md — там ТЗ v0.3 с местами устаревшими деталями).

---

## 1. Требования

| Режим | Что нужно |
|---|---|
| **Server (Docker)** | Docker + Docker Compose. PostgreSQL ставится автоматически (образ `pgvector/pgvector:pg16`). |
| **Server (без Docker)** | .NET 9 SDK, PostgreSQL 16 с расширением `pgvector`. |
| **Portable (сборка)** | .NET 9 SDK. Runtime не нужен — бинарник self-contained. |
| **Portable (запуск)** | Ничего, кроме бинарника. Работает офлайн. |

Ресурсы: 1 CPU / 512 MB RAM достаточно для личного объёма данных.

---

## 2. Server — Docker (рекомендуемый путь)

### 2.1 Клонировать и настроить

```bash
git clone https://github.com/nikonline-oss/Akasha.git
cd Akasha
cp .env.example .env
```

Отредактируй `.env`:

```ini
# порт и адрес хоста (см. §7)
AKASHA_PORT=8080
AKASHA_HOST=127.0.0.1

# база данных (PostgreSQL)
AKASHA_DB_PASSWORD=change-me-strong-password

# владелец (создаётся при первом старте)
AKASHA_OWNER_EMAIL=you@example.com
# пусто = сгенерировать временный пароль (напечатается в логах)
AKASHA_OWNER_PASSWORD=

# ключ шифрования секретов (AES-GCM). Необязателен: если пуст,
# сгенерируется случайный ключ и сохранится в /app/data/master.key (том akasha-data).
# Рекомендуется задать явно, чтобы управлять ключом: openssl rand -base64 32
AKASHA_MASTER_KEY=

# Telegram-бот аппрувов (опционально)
AKASHA_TELEGRAM_BOT_TOKEN=
AKASHA_TELEGRAM_CHAT_ID=
```

### 2.2 Запустить

```bash
docker compose up -d
docker compose logs -f akasha          # смотри "owner created: email / password" если пароль не задавал
curl -s http://127.0.0.1:8080/healthz  # {"status":"ok"}
```

### 2.3 Первый вход

Открой `http://127.0.0.1:8080` (или через reverse-proxy, §6) → попадёшь на страницу **/login** →
войди email + паролем из `.env`. Дальше:

1. **Проекты → Новый проект** — заведи первый проект.
2. **Агенты → Создать агента** — выбери скоупы (`read`, `propose`), скопируй ключ `ak_…` (показывается **один раз**).

> Схема БД создаётся автоматически на старте (`EnsureCreated`, миграций пока нет). В образе pgvector
> расширение `vector` подключается best-effort; если `CREATE EXTENSION` упал — включи вручную.

---

## 3. Server — без Docker (ручная установка)

### 3.1 PostgreSQL

```bash
# Debian/Ubuntu, образ уже содержит pgvector
docker run -d --name akasha-pg -e POSTGRES_DB=akasha -e POSTGRES_USER=akasha \
  -e POSTGRES_PASSWORD=secret -p 5432:5432 pgvector/pgvector:pg16
# либо свой PG16 + CREATE EXTENSION IF NOT EXISTS vector;
```

### 3.2 Собрать и запустить

```bash
export ConnectionStrings__Akasha="Host=localhost;Database=akasha;Username=akasha;Password=secret"
export AKASHA_OWNER_EMAIL=you@example.com
export AKASHA_OWNER_PASSWORD=your-password
export AKASHA_MASTER_KEY=$(openssl rand -base64 32)
export ASPNETCORE_URLS=http://127.0.0.1:5077

dotnet run --project src/Akasha.Server -c Release
# → логин на http://127.0.0.1:5077
```

Строку подключения можно задать и как `AKASHA_DATABASE_URL` (алиас `ConnectionStrings__Akasha`).

---

## 4. Portable — сборка self-contained

### 4.1 Собрать под нужные ОС

```bash
# Windows
dotnet publish src/Akasha.Portable -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist/win

# Linux
dotnet publish src/Akasha.Portable -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist/linux

# macOS (Intel / Apple Silicon)
dotnet publish src/Akasha.Portable -c Release -r osx-x64   --self-contained -p:PublishSingleFile=true -o dist/osx-x64
dotnet publish src/Akasha.Portable -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -o dist/osx-arm64
```

> Важно: публикуется проект **`Akasha.Portable`** (а не `Akasha.Server`) — он и есть portable-хост
> с командами CLI. На выходе один исполняемый файл `akasha` (`akasha.exe` на Windows).

### 4.2 Запуск

```bash
./dist/linux/akasha portable
# → [akasha] portable UI: http://127.0.0.1:5173 (SQLite: data/akasha.db)
# браузер откроется автоматически
```

- Слушает **только** `127.0.0.1` (никогда `0.0.0.0`), логина нет — владелец по localhost.
- Порт: по умолчанию **5173**; меняется `--port N` или `AKASHA_PORT=N`.
- `akasha serve` — то же самое (dev-режим).

### 4.3 Файлы данных

Всё лежит в каталоге данных (по умолчанию `./data`, меняется через `AKASHA_DATA_DIR`):

```
data/
├── akasha.db            # SQLite (WAL)
├── master.key           # автосгенерированный ключ шифрования секретов (если не задан AKASHA_MASTER_KEY)
├── sync-config.json     # настройки синхронизации (заполняются из UI «Синхронизация»)
└── sync-state.json      # курсор последней синхронизации
```

Этот каталог и есть «флешка» — переноси его целиком.

### 4.4 Команды CLI

```bash
akasha portable                # локальный сервер + UI
akasha serve                   # то же (dev-режим)
akasha sync                    # pull+push с сервером (см. §5)
akasha export [out.zip] [--secrets]   # полный дамп (секреты только шифрованными)
akasha import <archive.zip>           # идемпотентный импорт
akasha agent-key <name> [read propose scripts...]  # создать ключ агента локально
```

---

## 5. Синхронизация portable ↔ server

Синхронизируются записи/проекты/связи/скрипты; **секреты, агенты, аудит и владелец — никогда**.
Правило: last-write-wins по `updated_at`; если обе стороны меняли одну запись (`state_hash` разошёлся) —
создаётся conflict-proposal, который владелец решает руками.

### 5.1 Через UI (рекомендуется)

Portable → меню **«Синхронизация»** → заполни:

- **URL сервера** — напр. `https://akasha.example.com` (или `http://IP:8080`);
- **ID устройства** — любое имя, напр. `laptop`;
- **Ключ устройства** — `ak_…` (это ключ агента, созданный на сервере: сервер → UI → Агенты).

«Сохранить настройки» → «Синхронизировать». Результат: получено / отправлено / пропущено / конфликты + время последней синхронизации.

### 5.2 Через CLI

```bash
export AKASHA_SERVER_URL=https://akasha.example.com
export AKASHA_DEVICE_KEY=ak_xxxxxxxx_yyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyy
export AKASHA_DEVICE_ID=laptop          # опционально
akasha sync
```

Ключ устройства — тот же ключ агента. CLI и UI используют один и тот же конфиг (`data/sync-config.json`).

---

## 6. Reverse-proxy и TLS

Server-режим **обязан** работать за HTTPS (ключи агентов ходят в `Authorization: Bearer`). Пример Caddy:

```
akasha.example.com {
    reverse_proxy 127.0.0.1:8080
}
```

nginx:

```nginx
server {
    listen 443 ssl;
    server_name akasha.example.com;
    # ... ssl_certificate / ssl_certificate_key ...
    location / { proxy_pass http://127.0.0.1:8080; proxy_set_header Host $host; }
}
```

---

## 7. Порты и сеть (docker-compose)

Управляется из `.env`, менять `docker-compose.yml` не нужно:

```bash
# .env
AKASHA_PORT=8080        # порт и внутри контейнера, и на хосте
AKASHA_HOST=127.0.0.1   # слушать только локально (для reverse-proxy); 0.0.0.0 — открыть напрямую
```

- `AKASHA_HOST=127.0.0.1` + Caddy/nginx — рекомендуемый вариант.
- `AKASHA_HOST=0.0.0.0 AKASHA_PORT=9090` — открыть напрямую (обязательно сильный пароль владельца + фаервол: `ufw allow 9090/tcp`).

---

## 8. Бэкап и восстановление

- **PostgreSQL**: `pg_dump akasha` по cron + копия на объектное хранилище.
- **Portable**: скопировать каталог `data/` целиком (включая `master.key`, иначе секреты не расшифруются).
- **Переносимый формат**: UI владельца (или CLI `akasha export`) → zip `akasha-export-v1` (JSON + Markdown).
  Импорт идемпотентен (`akasha import <archive.zip>`). Секреты попадают в экспорт только с `--secrets` и только зашифрованными.

---

## 9. Обновление

```bash
git pull
docker compose build && docker compose up -d
```

Схема БД создаётся через `EnsureCreated` (миграций нет): новые таблицы появятся на старте, но изменение
существующих полей потребует ручной миграции или путь export → import.

---

## 10. Полный справочник переменных окружения

| Переменная | Режим | Назначение |
|---|---|---|
| `ConnectionStrings__Akasha` / `AKASHA_DATABASE_URL` | server | строка подключения PostgreSQL (Npgsql) |
| `AKASHA_SQLITE_PATH` | portable | путь к `akasha.db` (default `data/akasha.db`) |
| `AKASHA_DATA_DIR` | оба | каталог данных (default `data`) — db, master.key, sync-файлы |
| `AKASHA_PORTABLE` | server | любое непустое значение = portable-режим (SQLite + localhost-owner) |
| `AKASHA_OWNER_EMAIL` / `AKASHA_OWNER_PASSWORD` | server | bootstrap владельца (Argon2id; пустой пароль = сгенерировать) |
| `AKASHA_MASTER_KEY` | оба | ключ шифрования секретов. Пусто = автосгенерировать в `data/master.key` |
| `AKASHA_PORT` | portable / docker | порт веб-интерфейса (portable default 5173, docker default 8080) |
| `AKASHA_HOST` | docker | адрес хоста для проброса порта (`127.0.0.1` / `0.0.0.0`) |
| `AKASHA_DB_PASSWORD` | docker | пароль БД PostgreSQL (используется compose) |
| `AKASHA_TELEGRAM_BOT_TOKEN` / `AKASHA_TELEGRAM_CHAT_ID` | server | Telegram-бот аппрувов (устаревшие алиасы `AKASHA_TG_*`) |
| `AKASHA_SERVER_URL` / `AKASHA_DEVICE_KEY` / `AKASHA_DEVICE_ID` | portable | параметры синхронизации (можно хранить в `data/sync-config.json` через UI) |
| `ASPNETCORE_URLS` | server | адрес/порт прослушивания (в Docker уже задан `http://+:8080`) |
| `AKASHA_ONNX_MODEL` | оба | путь к `.onnx` — включает ONNX-эмбеддинги; размерность берётся из модели |
| `AKASHA_ONNX_VOCAB` | оба | `vocab.txt` (WordPiece) или `sentencepiece.bpe.model` (XLM-R) |
| `AKASHA_ONNX_MAXLEN` | оба | длина последовательности для ONNX (default 256, диапазон 8–512) |
| `AKASHA_EMBEDDING_DIM` | оба | размерность fallback-эмбеддингов без модели (default 384) |
| `AKASHA_AI_API_KEY` | оба | ключ LLM — включает AI-assist «✨ Помочь с запросом» в конструкторе скриптов |
| `AKASHA_AI_MODEL` | оба | модель LLM (default `gpt-4o-mini`) |
| `AKASHA_AI_ENDPOINT` | оба | базовый URL OpenAI-совместимого API (default `https://api.openai.com/v1`) |

### ONNX-эмбеддинги (опционально)

По умолчанию используется детерминированный hashed-ngram fallback (работает офлайн без модели).
Чтобы включить настоящую модель **BGE-Micro-V2** (384 dim):

1. Скачай `bge-micro-v2.onnx` и `vocab.txt` (из модели `BAAI/bge-micro-v2`, ONNX-экспорт) в каталог `models/`.
2. Задай:
   ```bash
   export AKASHA_ONNX_MODEL=/path/to/models/bge-micro-v2.onnx
   export AKASHA_ONNX_VOCAB=/path/to/models/vocab.txt   # необязательно
   ```
3. Перезапусти приложение. Если модель не загрузилась (нет файла/несовместимый формат) — в лог
   уйдёт предупреждение и приложение продолжит работать на hashed-ngram fallback.

### AI-assist (опционально)

Кнопка «✨ Помочь с запросом» в конструкторе скриптов генерирует structured JSON Query по текстовому
описанию через OpenAI-совместимый `/chat/completions` (OpenAI / OpenRouter / DeepSeek / Ollama):

```bash
export AKASHA_AI_API_KEY=sk-...          # без ключа кнопка скрыта
export AKASHA_AI_MODEL=gpt-4o-mini       # необязательно
export AKASHA_AI_ENDPOINT=https://api.openai.com/v1   # необязательно; для Ollama: http://127.0.0.1:11434/v1
```

## 11. Проверка после деплоя

```bash
# из каталога с docker-compose.yml
./scripts/verify.sh                        # http://127.0.0.1:8080
./scripts/verify.sh http://62.217.177.53:9090
AKASHA_KEY=ak_... ./scripts/verify.sh     # + API-проверки (auth, search, context)
```

Скрипт проверяет:

- `GET /healthz` и `GET /login` (200);
- БД: расширение `vector`, таблицы `context_notes` / `schema_migrations`, история миграций,
  ivfflat-индекс `ix_entries_embedding`;
- записи без эмбеддинга (`Embedding IS NULL` → в «Настройки → Пересчитать эмбеддинги»);
- (с ключом) `auth/me`, `projects`, поиск в режимах `fts` / `vector` / `hybrid`, `context`.

### ONNX-модель (опционально)

Размерность эмбеддингов берётся **из самой модели** (last hidden size) и применяется к колонке
`entries."Embedding"` (`vector(N)`) через миграцию `0003_embedding_dim`: при смене модели колонка
пересоздаётся, старые эмбеддинги обнуляются и пересчитываются фоновым worker'ом.

**Для русского контента** (проверено бенчмарком — 5/5) — мультиязычная `distiluse` (768 dim, ~135 МБ):

```bash
mkdir -p models && cd models
curl -L -o model.onnx https://huggingface.co/Xenova/distiluse-base-multilingual-cased-v2/resolve/main/onnx/model_quantized.onnx
curl -L -o vocab.txt https://huggingface.co/Xenova/distiluse-base-multilingual-cased-v2/resolve/main/vocab.txt
cd ..
```

**Для англоязычного контента** — `Xenova/bge-small-en-v1.5` (384 dim, ~34 МБ):

```bash
curl -L -o model.onnx https://huggingface.co/Xenova/bge-small-en-v1.5/resolve/main/onnx/model_quantized.onnx
curl -L -o vocab.txt https://huggingface.co/Xenova/bge-small-en-v1.5/resolve/main/vocab.txt
```

В `docker-compose.yml`:

```yaml
    volumes:
      - ./models:/app/models
    environment:
      AKASHA_ONNX_MODEL: /app/models/model.onnx
      AKASHA_ONNX_VOCAB: /app/models/vocab.txt
```

После старта — **Настройки → «Пересчитать все»** (или `POST /api/v1/admin/embeddings/recompute?all=true`).

> Без модели работает детерминированный hashed-ngram fallback; его размерность задаётся
> `AKASHA_EMBEDDING_DIM` (default 384).
>
> Проверялись, но **не рекомендованы** для русского: `bge-small-en-v1.5` (англоязычная),
> `paraphrase-multilingual-MiniLM-L12-v2` и `multilingual-e5-small` — слабые зазоры на коротких
> текстах (подробности в PR #46).



