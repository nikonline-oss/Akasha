# Akasha — MCP Tools Reference

MCP-сервер доступен на `POST /mcp` (streamable HTTP, JSON-RPC 2.0) и **требует аутентификации**:
`Authorization: Bearer ak_<prefix>_<secret>` (ключ агента) либо session-кука владельца (в portable — owner по localhost).
Все ответы оборачиваются в prompt-injection защиту:

```
[AKASHA_DATA_START]
…json данные…
[AKASHA_DATA_END]
```

> Stdio-режим MCP также доступен: `akasha mcp` (portable) — JSON-RPC поверх stdin/stdout.

## Протокол

```bash
curl -X POST http://127.0.0.1:5173/mcp \
  -H 'Authorization: Bearer ak_xxxxxxxx_yyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyy' \
  -H 'Content-Type: application/json' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
```

Поддерживаются методы: `initialize`, `notifications/initialized`, `ping`, `tools/list`, `tools/call`.

## Список инструментов (14)

### akasha_query_schema
Вернуть JSON Schema данных и синтаксиса Structured Query + примеры. **Вызывать первым**, чтобы строить корректные запросы. Вход: `{}`.

### akasha_list_projects
Список активных проектов. Вход: `{}` → `{projects:[{id,slug,name,status,stack,...}]}`.

### akasha_get_project
Проект + последние записи. Вход: `{"slug":"my-project"}`.

### akasha_get_entry
Запись по id (полное тело). Вход: `{"id":"<uuid>"}`.

### akasha_search
Гибридный поиск (FTS + семантика, RRF-фьюжн). Вход:

```json
{ "query": "как мы выбирали базу", "mode": "hybrid|fts|vector", "limit": 10, "project": "slug?", "kind": "note?", "tags": ["a"]? }
```

### akasha_run_query
Одноразовый structured JSON Query. Вход: `{"query": {...}, "params": {...}?, "transform": "<JMESPath>"?}`.

### akasha_run_script
Запуск сохранённого скрипта. Вход: `{"slug":"weekly-report", "params": {...}?}`.
Параметры валидируются по `params_schema`; результат кэшируется по `script.version + data_version`.

### akasha_propose
Единственный путь изменений (пишет proposal, владелец одобряет). Вход:

```json
{
  "op": "create|update|delete",
  "target": "entries|projects|entry_links",
  "target_id": "<uuid> (для update/delete)",
  "base_version": 3,
  "payload": { "title": "...", "body": "...", "kind": "note", "tags": [], "project": "slug" }
}
```

Ответ: `{id, status: "pending", preview_url}`. `409` при расхождении `base_version`.

### akasha_propose_script
Предложение нового/обновлённого скрипта. Вход:
`{"op":"create|update", "target_id":"<uuid>"?, "payload":{"slug","name","query","transform"?,"output_format"?,"params_schema"?,"cache_ttl"?}, "base_version"?}`.

### akasha_get_proposal_status
Статус своего предложения. Вход: `{"id":"<uuid>"}` → `{id,status,review_comment,reviewed_at}`.

## Хранилище контекста агента (без апрувов)

Отдельная рабочая область, куда агент пишет **напрямую**, без proposal/approval. Нужен scope `context`.
Основное хранилище (`entries`) по-прежнему меняется только через `akasha_propose`.

### akasha_context_write
Создать заметку контекста. Вход: `{"title":"...", "body":"...", "project_id":"<uuid>"?, "tags":["a"]?}`
→ `{id,title,project_id,created_at}`.

### akasha_context_list
Список заметок контекста. Вход: `{"project_id"?, "agent_id"?, "q"?, "limit"?}` → `{notes:[...]}`.
Агент видит только свои заметки; владелец — все.

### akasha_context_get
Заметка по id. Вход: `{"id":"<uuid>"}`.

### akasha_context_delete
Удалить свою заметку контекста. Вход: `{"id":"<uuid>"}`.

## Скоупы

| Инструменты | Требуемый scope |
|---|---|
| `akasha_query_schema`, `akasha_list_projects`, `akasha_get_project`, `akasha_get_entry`, `akasha_search`, `akasha_run_query` | `read` |
| `akasha_propose`, `akasha_propose_script`, `akasha_get_proposal_status` | `propose` |
| `akasha_run_script` | `scripts` |
| `akasha_context_write`, `akasha_context_delete` | `context` |
| `akasha_context_list`, `akasha_context_get` | `read` |

Недостаточный scope → ошибка (`-32001` в JSON-RPC).

## Подключение клиентов

Claude Desktop / Cursor / Cline (transport: streamable HTTP):

```json
{
  "mcpServers": {
    "akasha": {
      "url": "https://akasha.example.com/mcp",
      "headers": { "Authorization": "Bearer ak_xxxxxxxx_yyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyy" }
    }
  }
}
```

Локально (portable): тот же endpoint на `http://127.0.0.1:5173/mcp` (portable-режим позволяет localhost-owner без Bearer-ключа).
