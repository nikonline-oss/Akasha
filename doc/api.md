# Akasha — REST API Reference

Базовый префикс: **`/api/v1`**. Аутентификация двух типов:

- **Владелец (UI/admin)**: session cookie `akasha_session` (вход через `POST /api/v1/auth/login`) либо
  в portable-режиме — owner автоматически по localhost.
- **AI-агент / устройство синхронизации**: `Authorization: Bearer ak_<prefix>_<secret>` — Argon2id-ключ
  со скоупами `read`, `propose`, `scripts`.

Формат ошибок (единый): `{"error":"<code>","detail":"<...>"}`.
Коды: `401 unauthorized`, `403 forbidden`, `404 not_found`, `409 conflict`, `422 validation / invalid_query / sync_rejected`, `429 rate_limited`.

## Auth

| Метод | Путь | Кто | Описание |
|---|---|---|---|
| POST | `/api/v1/auth/login` | публичный | `{email,password}` → cookie-сессия владельца |
| POST | `/api/v1/auth/logout` | owner | завершение сессии |
| GET | `/api/v1/auth/me` | любой | текущий principal (role, scopes, name) |

## Чтение (scope: `read`)

| Метод | Путь | Описание |
|---|---|---|
| GET | `/api/v1/projects` | список проектов |
| GET | `/api/v1/projects/{slug}` | проект + последние записи |
| GET | `/api/v1/entries?project=&kind=&limit=&cursor=` | список записей с фильтрами |
| GET | `/api/v1/entries/{id}` | запись целиком |
| GET | `/api/v1/entries/{id}/links` | связи записи |
| POST | `/api/v1/search` | `{query, mode: hybrid\|fts\|vector, limit, project?, kind?, tags?}` |
| POST | `/api/v1/query` | одноразовый structured JSON Query (whitelist, лимит 1000) |
| GET | `/api/v1/query/schema` | JSON Schema языка запросов + примеры |

## Proposals (запись агентами; scope: `propose`)

| Метод | Путь | Кто | Описание |
|---|---|---|---|
| POST | `/api/v1/proposals` | agent | `{op: create\|update\|delete, target: entries\|projects\|entry_links\|scripts, target_id?, base_version?, payload}` → pending |
| GET | `/api/v1/proposals/{id}` | agent(owner) | статус своего предложения |
| GET | `/api/v1/proposals?status=` | agent | листинг (агент видит только свои) |

Прямых write-эндпоинтов для **основного хранилища** (`entries`/`projects`) не существует — только proposals.

## Context store (прямая запись агентами; scope: `context`)

Отдельная рабочая область, куда агенты пишут **напрямую, без апрувов**. Не пересекается с `entries`
(основное хранилище по-прежнему меняется только через proposals).

| Метод | Путь | Scope | Описание |
|---|---|---|---|
| GET | `/api/v1/context?project_id=&agent_id=&q=&limit=` | `read` | список заметок (агент видит только свои) |
| GET | `/api/v1/context/{id}` | `read` | одна заметка |
| POST | `/api/v1/context` | `context` | `{title, body?, project_id?, tags?}` → заметка |
| DELETE | `/api/v1/context/{id}` | `context` | удалить свою заметку |

## Admin (только владелец)

Группа `/api/v1/admin`:

| Метод | Путь | Описание |
|---|---|---|
| GET | `/admin/proposals?status=` | очередь аппрувов (с diff) |
| POST | `/admin/proposals/{id}/approve` | применить (409 при version conflict) |
| POST | `/admin/proposals/{id}/reject` | отклонить `{comment}` (комментарий обязателен) |
| POST | `/admin/proposals/batch-approve` | `{ids[]}` |
| GET / POST | `/admin/agents` | список / создание (`POST` возвращает **полный ключ один раз**) |
| DELETE | `/admin/agents/{id}` | revoke |
| GET | `/admin/audit?actor=&target=&since=` | журнал |
| GET / POST | `/admin/secrets` | список (метаданные) / создание |
| GET | `/admin/secrets/{id}/reveal` | расшифровать значение (пишется в audit) |
| DELETE | `/admin/secrets/{id}` | удалить |
| POST / PUT / DELETE | `/admin/projects[/{id}]` | CRUD проектов |
| POST / PUT / DELETE | `/admin/entries[/{id}]` | CRUD записей |
| POST / DELETE | `/admin/entries/links` | добавить/убрать связь |
| POST / PUT | `/admin/scripts[/{slug}]` | создать/обновить скрипт (`PUT` → version++) |
| POST | `/admin/scripts/{slug}/invalidate` | сброс кэша |
| DELETE | `/admin/scripts/{slug}` | удалить скрипт |
| GET | `/admin/export?includeSecrets=` | zip `akasha-export-v1` |
| POST | `/admin/import` | multipart `file` — идемпотентный импорт |

## Scripts (scope: `scripts`)

| Метод | Путь | Описание |
|---|---|---|
| GET | `/api/v1/scripts` | список скриптов |
| POST | `/api/v1/scripts/{slug}/run` | `{params{}}` → результат (кэш по version + data_version) |

## Sync

| Метод | Путь | Описание |
|---|---|---|
| GET | `/api/v1/sync/pull?since=<iso8601>` | изменения после `since` (буфер 2с) → `{changes, serverTime}` |
| POST | `/api/v1/sync/push` | `{device_id, changes:[{entity,op,data,updated_at}]}` → `{applied, skipped_older, conflict_proposals}` |

Forbidden entities (`secrets`, `audit_log`, `agents`, `owners`) → `422 sync_rejected`.

## Прочее

| Метод | Путь | Описание |
|---|---|---|
| GET | `/healthz` | `{"status":"ok"}` без аутентификации |
| POST | `/mcp` | MCP streamable-HTTTP endpoint (JSON-RPC 2.0), **требует Bearer/сессии** (см. [mcp-tools.md](mcp-tools.md)) |

## Rate limiting

Скользящее окно на агента (owner не лимитируется):

- `POST /api/v1/proposals` — 10/мин;
- `POST /api/v1/search` — 60/мин;
- `POST /api/v1/query` — 30/мин;
- остальные API/MCP — 60/мин.

Превышение → `429` + заголовок `Retry-After`.
