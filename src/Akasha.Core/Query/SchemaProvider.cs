namespace Akasha.Core.Query;

/// <summary>JSON Schema формата structured query + примеры (ответ MCP-tool akasha_query_schema).</summary>
internal static class SchemaProvider
{
    public const string SchemaJson = """
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "title": "Akasha Structured JSON Query v1",
  "type": "object",
  "required": ["from"],
  "properties": {
    "from": { "enum": ["entries", "projects", "entry_links"] },
    "where": { "type": "object", "description": "Map field -> scalar or operator object. Operators: $eq,$ne,$gt,$gte,$lt,$lte,$in,$nin,$contains,$contains_any,$contains_all,$like,$is(null|not_null),$relative(-1h,-7d,-1M,today,start_of_week,start_of_month,start_of_year),$param" },
    "select": { "type": "array", "items": { "type": "string" }, "description": "Whitelisted fields only" },
    "order_by": { "type": "array", "items": { "type": "object", "properties": { "field": {"type":"string"}, "dir": {"enum":["asc","desc"]} } } },
    "limit": { "type": "integer", "minimum": 1, "maximum": 1000 },
    "offset": { "type": "integer", "minimum": 0 },
    "group_by": { "type": "array", "items": { "enum": ["kind","project_id","created_by"] } },
    "aggregate": { "type": "object", "description": "name -> {$count:'*'} | {$max:field} | {$min:field}; supported fields for max/min: updated_at, version" },
    "include": { "type": "object", "description": "v1: only 'links' subquery over entry_links with $self reference", "additionalProperties": false, "properties": { "links": { "type": "object" } } }
  },
  "examples": [
    { "from": "entries", "where": { "project": "foo", "kind": "decision", "deleted_at": { "$is": null } }, "select": ["id","title","body","updated_at"], "order_by": [{ "field": "updated_at", "dir": "desc" }], "limit": 5 },
    { "from": "entries", "where": { "kind": "source", "tags": { "$contains": "auth" }, "updated_at": { "$gte": { "$relative": "-7d" } } } },
    { "from": "entries", "where": { "deleted_at": { "$is": null } }, "group_by": ["project_id"], "aggregate": { "count": { "$count": "*" } }, "order_by": [{ "field": "count", "dir": "desc" }] },
    { "from": "entries", "where": { "project": { "$eq": { "$param": "project" } }, "kind": { "$eq": { "$param": "kind" } } }, "limit": 10 },
    { "from": "projects", "where": { "status": "active", "deleted_at": null }, "select": ["slug","name","updated_at"] },
    { "from": "entry_links", "where": { "relation": "supersedes" }, "limit": 50 }
  ]
}
""";
}
