# Akasha — REST API Reference

Базовый префикс: `/api`. Аутентификация двух типов:

- **Владелец (UI/admin)**: session cookie `akasha_session` (login через `POST /api/auth/login`) — либо Basic/ENV bootstrap.
- **AI-агент**: `Authorization: Bearer ak_<prefix>_<secret>` — Argon2id-верифицируемый ключ с набором скоупов (`read`, `propose`, `scripts`, `sync`).

Формат ошибок (единый): `{"error":{"code":"...","message":"..."}}`
Коды: `401 unauthorized`, `403 forbidden_scope`, `404 not_found`, `409 version_conflict`, `422 validation_failed / sync_rejected`, `429 rate_limited`.

## Auth

| Метод | Путь | Кто | Описание |
|---|---|---|---|
| POST | `/api/auth/login` | любой | `{email,password}` → cookie-сессия владельца |
| POST | `/api/auth/logout` | owner | завершение сессии |
| GET | `/api/auth/me` | любой | текущий principal (role, scopes, name) |

## Чтение (scope: `read`)

| Метод | Путь | Описание |
|---|---|---|
| GET | `/api/projects` | список проектов |
| GET | `/api/projects/{slug}` | проект + последние записи |
| GET | `/api/entries?project=&kind=&tag=&limit=&offset=` | список записей с фильтрами |
| GET | `/api/entries/{id}` | запись целиком |
| GET | `/api/entries/{id}/links` | связи записи (out/in) |
| POST | `/api/search` | `{q, mode: hybrid\|fts\|semantic, limit, project?, tags?}` → гибрид FTS+вектор |
| POST | `/api/query` | Structured JSON Query (см. scripts-spec.md). Whitelist-only, лимит 500 |
| GET | `/api/query/schema` | описание схемы для LLM (таблицы, поля, операторы) |

## Proposals (запись агентами; scope: `propose`)

| Метод | Путь | Кто | Описание |
|---|---|---|---|
| POST | `/api/proposals` | agent | создать предложение `{op: create\|update\|delete, target: entries\|projects\|scripts, target_id?, base_version?, payload}` → 201 pending |
| GET | `/api/proposals/{id}` | agent(owner) | статус своего предложения |
| GET | `/api/proposals?status=pending` | agent | листинг |

Прямых write-endpoint'ов для агентов **не существует** — это архитектурное ограничение ТЗ §6.

## Admin (только владелец)

Группа `/api/admin`:

| Метод | Путь | Описание |
|---|---|---|
| GET | `/admin/proposals?status=` | очередь аппрувов |
| POST | `/admin/proposals/{id}/approve` | применить (409 при version conflict) |
| POST | `/admin/proposals/{id}/reject` | отклонить `{comment}` |
| POST | `/admin/proposals/batch-approve` | `{ids[]}` |
| GET/POST/DELETE | `/admin/agents` | управление агентами; POST возвращает **полный ключ один раз** |
| GET | `/admin/audit?limit=&agent=` | журнал |
| GET/POST/DELETE | `/admin/secrets`, `/admin/secrets/{id}/reveal` | секреты (AES-GCM); reveal только owner |
| POST/PUT/DELETE | `/admin/projects`, `/admin/entries` | CRUD напрямую |
| POST/DELETE | `/admin/entries/links` | добавить/убрать связь |
| POST/PUT/DELETE | `/admin/scripts`, `/admin/scripts/{slug}`, `/admin/scripts/{slug}/invalidate` | реестр скриптов |
| GET | `/admin/export` | zip «akasha-export-v1» |
| POST | `/admin/import` | импорт zip (multipart/raw body), идемпотентно |

## Scripts (scope: `scripts`)

| Метод | Путь | Описание |
|---|---|---|
| GET | `/api/scripts` | список доступных скриптов |
| POST | `/api/scripts/{slug}/run` | `{params{}}` → результат (кэш по version+data_version+params) |

## Sync (scope: `sync` или owner)

| Метод | Путь | Описание |
|---|---|---|
| GET | `/api/sync/pull?since=<iso8601>` | изменения после `since` (buffer 2с), desc by updated_at |
| POST | `/api/sync/push` | `{device_id, changes:[{entity,op,data,updated_at}]}` → `{applied, skipped_older, conflicts[]}`; forbidden entities → 422 |

## Прочее

| Метод | Путь | Описание |
|---|---|---|
| GET | `/healthz` | `{status:"ok"}` без аутентификации |
| POST | `/mcp` | MCP streamable endpoint (см. mcp-tools.md) |

## Rate limiting (§6.4 ТЗ)

Скользящее окно per-principal: query/search — 60/мин, proposals — 20/мин, sync push — 10/мин.
Превышение → `429 {"error":{"code":"rate_limited",...}}` + заголовок `Retry-After`.
