#!/usr/bin/env bash
# Akasha: проверка после деплоя (HTTP + БД + API).
#
#   ./scripts/verify.sh                       # http://127.0.0.1:8080
#   ./scripts/verify.sh http://host:9090      # другой адрес
#   AKASHA_KEY=ak_... ./scripts/verify.sh     # + проверки API ключом агента
#
# Запускать из каталога с docker-compose.yml (для psql-проверок).
set -uo pipefail

BASE="${1:-http://127.0.0.1:8080}"
KEY="${AKASHA_KEY:-}"
ok=0
fail=0
pass() { echo "  OK   $1"; ok=$((ok + 1)); }
bad() { echo "  FAIL $1"; fail=$((fail + 1)); }

echo "== HTTP ($BASE) =="
if curl -fsS --max-time 5 "$BASE/healthz" 2>/dev/null | grep -q '"ok"'; then pass "healthz"; else bad "healthz"; fi
code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 "$BASE/login")
[ "$code" = "200" ] && pass "login (200)" || bad "login ($code)"

echo "== База (docker compose) =="
q() { docker compose exec -T db psql -U akasha -d akasha -tAc "$1" 2>/dev/null | tr -d '\r'; }
[ "$(q 'select 1')" = "1" ] && pass "psql" || bad "psql"

ext=$(q "select extname from pg_extension where extname='vector'")
[ "$ext" = "vector" ] && pass "расширение pgvector" || bad "расширение pgvector (${ext:-нет})"

for t in context_notes schema_migrations; do
  v=$(q "select to_regclass('public.$t')")
  [ -n "$v" ] && pass "таблица $t" || bad "таблица $t"
done

mig=$(q "select string_agg(\"Id\", ',') from schema_migrations")
[ -n "$mig" ] && pass "миграции: $mig" || bad "миграции"

idx=$(q "select indexname from pg_indexes where tablename='entries' and indexname='ix_entries_embedding'")
[ -n "$idx" ] && pass "ivfflat-индекс" || bad "ivfflat-индекс"

nulls=$(q 'select count(*) from entries where "Embedding" is null and "DeletedAt" is null')
[ "$nulls" = "0" ] && pass "эмбеддинги: NULL нет" || bad "эмбеддинги: $nulls NULL (Настройки → Пересчитать)"

echo "== API =="
if [ -z "$KEY" ]; then
  echo "  SKIP (задай AKASHA_KEY=ak_... чтобы прогнать API)"
else
  H="Authorization: Bearer $KEY"
  curl -fsS --max-time 5 -H "$H" "$BASE/api/v1/auth/me" 2>/dev/null | grep -q '"role"' \
    && pass "auth/me" || bad "auth/me"
  curl -fsS --max-time 5 -H "$H" "$BASE/api/v1/projects" 2>/dev/null | grep -q '\[' \
    && pass "projects" || bad "projects"
  for mode in fts vector hybrid; do
    r=$(curl -fsS --max-time 10 -H "$H" -H 'Content-Type: application/json' \
      -d "{\"query\":\"test\",\"mode\":\"$mode\",\"limit\":1}" "$BASE/api/v1/search" 2>/dev/null)
    echo "$r" | grep -q '"hits"' && pass "search $mode" || bad "search $mode"
  done
  curl -fsS --max-time 5 -H "$H" "$BASE/api/v1/context" 2>/dev/null | grep -q '\[' \
    && pass "context list" || bad "context list"
fi

echo
echo "PASS=$ok FAIL=$fail"
[ "$fail" -eq 0 ]
