# Akasha — Sync Protocol (portable ↔ server)

Цель (§9 ТЗ): portable-клиент работает офлайн на SQLite; при подключении синхронизируется с сервером.
**Source of truth — сервер.** Протокол diff-based, поверх REST (`/api/sync/*`), HTTPS обязателен на сервере.

## 1. Синхронизируемые сущности

| Entity | Включена | Примечание |
|---|---|---|
| `entries` | ✅ | включая soft-delete (`op:"delete"` = tombstone) |
| `projects` | ✅ | |
| `entry_links` | ✅ | PK тройка (from,to,relation) |
| `scripts` | ✅ | definition-level |
| `proposals` | ✅ | только статусы (для истории) |
| `secrets` | ❌ ForbiddenEntities | никогда не покидают устройство |
| `audit_log` | ❌ | локален каждому узлу |
| `agents` | ❌ | ключи не реплицируются |
| `owners` | ❌ | |

Фильтр реализован в `SyncService.ForbiddenEntities` — push с forbidden entity → `422 sync_rejected` (вся пачка отклоняется).

## 2. Pull (сервер → клиент)

```
GET /api/sync/pull?since=2026-10-26T09:00:00Z
→ [ { "entity":"entries", "op":"upsert|delete", "data":{...полная запись...}, "updated_at":"..." }, ... ]
```

- Отдаются все записи с `updated_at > since - 2s` (буфер против clock-skew и миллисекундной квантизации SQLite).
- Сортировка: desc by updated_at. Клиент применяет через `ApplyPulledChangesAsync` (тот же last-write-wins).
- Клиент хранит local cursor = max(updated_at) из последнего ответа.

## 3. Push (клиент → сервер)

```
POST /api/sync/push
{ "device_id":"laptop-a",
  "changes":[ { "entity":"entries", "op":"upsert", "updated_at":"...", "data":{ "id":"...", "title":"...", ..., "state_hash":"..." } } ] }
→ { "applied": 3, "skipped_older": 1, "conflicts": [] }
```

### Разрешение конфликтов: last-write-wins
Сервер сравнивает `updated_at` входящей версии с локальной:
- incoming новее → применяется (version+1, пересчёт embedding/state_hash);
- incoming старее или равна → `skipped_older` (клиент получит истину при следующем pull).

`state_hash` (SHA-256 канонического содержимого) позволяет детектировать ситуацию «оба изменили одно и то же»: при расхождении хешей сервер создаёт **conflict-proposal** с вариантом клиента, а не перезаписывает молча; id предлагаемых конфликтов возвращаются в `conflicts[]`.

## 4. Идемпотентность и порядок

- Все операции upsert/delete идемпотентны по `(id, updated_at)` — повторная отправка безопасна.
- PULL-before-PUSH рекомендованный порядок клиента (`akasha sync` делает именно так): сначала применить серверное состояние, потом отправить локальные дельты.
- Одна сессия = одна транзакция SaveChanges на стороне сервера; при ошибке БД вся пачка откатывается, клиент повторяет позже.

## 5. Аутентификация sync

Device получает agent-ключ со скоупом `sync` (+`read`). Ключ хранится в конфиге portable (`~/.akasha/config.json`, права 0600). Отзыв агента на сервере мгновенно ломает синхронизацию устройства (401).

## 6. Ограничения v1

- Нет CRDT/merge на уровне полей (слить два разных body нельзя — побеждает последний).
- Нет бинарных вложений.
- Конфликт разрешается человеком через proposal — осознанное решение single-user модели.
