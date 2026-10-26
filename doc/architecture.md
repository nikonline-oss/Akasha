# Akasha — Архитектура

> Personal Context Vault: хранилище контекста о владельце и его проектах с контролируемым доступом AI-агентов.
> Полное ТЗ — в [README.md](../README.md). Этот документ описывает фактическую реализацию.

## 1. Компоненты и проекты

```
/workspace
├── src/
│   ├── Akasha.Core/        # общая доменная библиотека (без зависимостей от ASP.NET)
│   └── Akasha.Server/      # ASP.NET Core: REST API, MCP, Blazor UI, Telegram-бот, sync
├── tests/
│   └── Akasha.Tests/       # xUnit: unit + интеграционные тесты (SQLite temp-БД)
├── doc/                    # эта документация
└── README.md               # ТЗ v0.3 (source of truth по требованиям)
```

| Проект | Назначение |
|---|---|
| `Akasha.Core` | Модели, EF Core DbContext (PostgreSQL/Npgsql + SQLite), QueryEngine, ProposalService, ScriptService, SearchService, SyncService, ExportService, DiffService, AuditService, криптография (Argon2id, AES-GCM), эмбеддинги |
| `Akasha.Server` | Minimal API endpoints, аутентификация (Bearer-ключи агентов + cookie для владельца), MCP-tools, Telegram-бот аппрувов, Blazor Server UI (MudBlazor) |
| Portable-режим | Тот же `Akasha.Server` поверх SQLite (`AddAkasha(portable: true)`), слушает только `127.0.0.1`, самодостаточен офлайн |

## 2. Ключевые решения

| Решение | Выбор | Обоснование |
|---|---|---|
| Язык / runtime | C# / .NET 9 | Единая кодовая база server + portable + UI |
| Server БД | PostgreSQL 16 + pgvector | Гибридный поиск, зрелость |
| Portable БД | SQLite (WAL) | Один файл, работает с флешки |
| ORM | EF Core 9, один DbContext, два провайдера | Единая схема; векторы/теги — сериализуемые колонки |
| UI | Blazor Server + MudBlazor | Без Electron; фиолетово-золотая тема (§7.1 ТЗ) |
| Scripts | Structured JSON Query (+ опц. JMESPath transform) | Безопасно, AI-генерируемо, whitelist-операторы |
| Sync | Diff-based JSON over HTTPS, last-write-wins | Проще CRDT, достаточно для single-user |
| Эмбеддинги | Интерфейс `IEmbeddingService`; fallback — hashed ngram (офлайн, без модели) | ONNX/BGE-Micro подключается отдельно |
| Секреты | AES-GCM с ключом из master-key; никогда не синхронизируются, никогда не отдаются агентам | §8 ТЗ |

## 3. Модель данных (Core/Models, Core/Data/AkashaDbContext)

Таблицы: `owners`, `agents`, `projects`, `entries`, `entry_links`, `proposals`, `scripts`, `secrets`, `script_cache`, `meta`, `audit_log`.

Особенности реализации:

- **Soft-delete**: `deleted_at` на entries/projects; глобальные query-filters в DbContext.
- **Версионирование записей**: `entries.version` инкрементируется при каждом изменении; proposals хранят `base_version` → конфликт при approve если запись изменилась (409).
- **Теги**: `List<string>` через конвертер + дублирующая колонка `tags_raw` (формат `\u001Ftag1\u001Ftag2\u001F`), синхронизируется в перехвате `SaveChanges` — даёт переводимый `LIKE`-фильтр по тегам на обоих провайдерах.
- **`meta.data_version`**: монотонный счётчик изменений данных (инкремент в SaveChanges при трогании entries/projects/entry_links) — инвалидация кэша скриптов.
- **Эмбеддинги**: колонка `embedding` (float[] → TEXT в SQLite, real[] в Postgres); косинусная близость считается в памяти (SearchService) — для личных объёмов достаточно.
- **State hash**: `ContentHash.Of(entry)` — SHA-256 канонического содержимого, используется sync-конфликтами.

## 4. Data flow

### Чтение агентом
```
Agent ──Bearer ak_xxx──► POST /api/query ──► QueryEngine.ExecuteAsync
       (whitelist from/filters/transform)          │
                                                   ▼
                        SchemaProvider (описание схемы для LLM) + rate limit + audit
```

### Запись (только через proposal)
```
Agent ──► POST /api/proposals ──► ProposalService.CreateAsync (pending)
Owner ──► UI / Telegram inline-kнопки / POST /api/proposals/{id}/approve
      ──► base_version check (409 conflict) ──► apply к entries/projects/scripts ──► audit
```

### Синхронизация portable ↔ server
```
Portable ──GET /api/sync/pull?since=...──► изменения после since (буфер 2с против clock-skew)
Portable ──POST /api/sync/push───────────► last-write-wins по updated_at
Forbidden entities: secrets, audit_log, agents, owners — исключены на уровне сервиса
Conflict (both sides changed) ──► автоматический conflict-proposal владельцу
```

## 5. Безопасность (кратко; детали — security.md)

- Физическое отсутствие write-endpoint'ов для агентов (только `/api/proposals`).
- Argon2id для ключей агентов и пароля владельца; AES-GCM для секретов.
- Prompt-injection wrapper `[AKASHA_DATA_START]…[AKASHA_DATA_END]` на всех MCP-ответах.
- Rate limiting per-principal; audit-log каждой операции.
- Portable: только loopback, никаких внешних сетевых зависимостей.

## 6. Тесты

`tests/Akasha.Tests` — 51 тест (xUnit, SQLite temp-файл на тест-класс):
QueryEngine (whitelist, фильтры, сортировка, лимиты, теги), ProposalService (lifecycle, конфликты версий),
ScriptService (валидация параметров, кэш, canonical JSON), Hashing/SecretCrypto (roundtrip),
DiffService, SearchService, SyncService (pull buffer, push last-write-wins, forbidden entities),
ExportService (zip roundtrip, идемпотентный импорт), HTTP-эндпоинты.

Запуск: `dotnet test`
