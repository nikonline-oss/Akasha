# Akasha — Structured JSON Query & Scripts Specification

Скрипты Akasha — это **декларативные JSON-объекты**, а не код. Никакого DSL/интерпретатора: запрос транслируется QueryEngine напрямую в LINQ/SQL. Это даёт безопасность (whitelist), AI-генерируемость и визуальный конструктор.

## 1. Формат запроса

```json
{
  "from": "entries | projects | entry_links",
  "where": { "<field>": <scalar | operator-object>, ... },
  "select": ["id", "title", "..."],
  "order_by": [{ "field": "updated_at", "dir": "desc" }],
  "limit": 50,
  "offset": 0,
  "group_by": ["project_id"],
  "aggregate": { "count": { "$count": "*" }, "avg_len": { "$avg": "body_len" } },
  "transform": "<JMESPath, опционально — применяется к результату>"
}
```

### Whitelist
- **Таблицы**: только `entries`, `projects`, `entry_links`. `secrets` физически недоступны.
- **Поля**: на каждую таблицу — фиксированный набор (см. `GET /api/query/schema`). `embedding`, ключи агентов исключены.
- Неизвестное поле/таблица/оператор → `422 validation_failed` (без частичного выполнения).

### Операторы `where`

| Оператор | Пример | Значение |
|---|---|---|
| скаляр | `"kind": "decision"` | $eq |
| `$eq $ne $gt $gte $lt $lte` | `{"$gte": "2026-01-01"}` | сравнения |
| `$in $nin` | `{"$in": ["note","source"]}` | множество |
| `$contains` | tags: `{"$contains":"auth"}` | строка/тег внутри |
| `$contains_any $contains_all` | теги | ANY/ALL по массиву тегов |
| `$like` | `{"$like":"%redis%"}` | SQL LIKE по title/body |
| `$is` | `{"$is":"null"|"not_null"}` | null-проверки |
| `$relative` | `{"$gte":{"$relative":"-7d"}}` | `-1h,-7d,-1M,today,start_of_week,start_of_month,start_of_year` |
| `$param` | `{"$eq":{"$param":"project"}}` | подстановка из params скрипта |

Логические combinators: массив условий в значении трактуется как AND; OR задаётся через `$in`.

## 2. Скрипт (таблица `scripts`)

```json
{
  "slug": "recent-decisions",
  "name": "Свежие решения",
  "description": "Последние N решений проекта",
  "query": { "from": "entries", "where": { "kind": "decision", "project": {"$eq":{"$param":"project"}}, "deleted_at": {"$is":"null"} }, "order_by": [{"field":"created_at","dir":"desc"}], "limit": {"$param":"n"} },
  "params_schema": { "type":"object", "properties": { "project": {"type":"string"}, "n": {"type":"integer","default":10,"maximum":100 } }, "required": ["project"] },
  "transform": "items[].{id: id, title: title}",
  "output_format": "table | json | markdown",
  "cache_ttl": 300,
  "version": 1
}
```

### Валидация параметров
`params_schema` — упрощённый JSON Schema (`type`, `required`, `enum`, `default`, `minimum/maximum`, `minLength/maxLength`, `pattern`). Несоответствие → `422`. Обязательные поля проверяются до исполнения запроса.

### Кэширование
Ключ кэша = SHA-256(canonical JSON `{script_slug, script.version, params, meta.data_version}`).
Инвалидация: автоматическая при изменении данных (data_version), вручную — `POST /api/admin/scripts/{slug}/invalidate`. TTL (`cache_ttl`, сек) — верхняя граница протухания; `ExpiresAt` сравнивается в unix-ms (SQLite-safe).

### Версионирование
Любое обновление скрипта инкрементирует `version` → старый кэш проигнорирован автоматически. История предложений правок — в proposals (target=`scripts`).

## 3. JMESPath transform

Опциональный post-processing результата (библиотека `JmesTransform.Apply`): проекция `items[].{...}`, сортировка `sort_by`, агрегаты `length`, flatten. Только read-only выражения; ошибка синтаксиса → `422` при сохранении скрипта (валидация заранее).

## 4. Конструктор в UI

Blazor-страница «Scripts → New/Edit» строит тот же JSON визуально (из справочника SchemaProvider): выбор таблицы → фильтры-строки (поле/оператор/значение) → сортировка/лимит → предпросмотр результата кнопкой «Run». Сохранение = `POST /api/admin/scripts`. AI может предлагать скрипты через MCP `akasha_propose_script` — владелец аппрувит как обычный proposal.

## 5. Безопасность

- Нет raw SQL, нет eval, нет сетевых вызовов из запроса.
- Лимит строк жёсткий (max 1000, default 500) даже если `limit` не задан.
- Запросы пишутся от имени principal; scope `read` обязателен; audit_log фиксирует каждый запуск.
