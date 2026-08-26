#!/usr/bin/env bash
# Captura determinística e somente-leitura do estate Supabase legado em um
# bundle versionado com manifesto SHA-256.
#
# Uso:
#   LEGACY_SNAPSHOT_CONN='postgresql://...' tools/legacy-snapshot/snapshot.sh <saída>
#
# Variáveis de ambiente:
#   LEGACY_SNAPSHOT_CONN         connection string somente-leitura (obrigatória)
#   LEGACY_SNAPSHOT_SCHEMA       schema de aplicação capturado (padrão: public)
#   LEGACY_SNAPSHOT_STORAGE_URL  base da Storage API, ex. https://<ref>.supabase.co/storage/v1
#   LEGACY_SNAPSHOT_SERVICE_KEY  chave com acesso de leitura à Storage
#
# Postura de segurança:
#   - Saída recusada dentro da árvore do repositório; diretório criado com 0700.
#   - A connection string nunca é impressa nem entra no bundle.
#   - O bundle contém metadados de identidade e política: confidencial; destino,
#     retenção e destruição são decididos e registrados pelo responsável.
#   - Consistência: todos os agregados saem de uma única transação
#     REPEATABLE READ READ ONLY; o DDL vem de uma única passada do pg_dump.
#     Capturas parciais misturadas invalidam as ledgers e nunca são aproveitadas:
#     qualquer falha descarta o diretório inteiro.

set -euo pipefail

fail() { echo "erro: $*" >&2; exit 1; }

[[ $# -eq 1 ]] || { echo "uso: LEGACY_SNAPSHOT_CONN=... $0 <diretorio-de-saida>" >&2; exit 2; }
SCHEMA="${LEGACY_SNAPSHOT_SCHEMA:-public}"
: "${LEGACY_SNAPSHOT_CONN:?LEGACY_SNAPSHOT_CONN é obrigatória}"

for bin in pg_dump psql; do
  command -v "$bin" >/dev/null || fail "$bin não encontrado no PATH"
done

digest_of() {
  if command -v shasum >/dev/null; then
    shasum -a 256 "$1" | awk '{print $1}'
  else
    openssl dgst -sha256 -r "$1" | awk '{print $1}'
  fi
}

dest_parent=$(dirname "$1")
dest_name=$(basename "$1")
mkdir -p "$dest_parent"
dest_abs="$(cd "$dest_parent" && pwd)/$dest_name"

if repo_root=$(git rev-parse --show-toplevel 2>/dev/null); then
  [[ "$dest_abs" == "$repo_root"/* ]] &&
    fail "diretório de saída não pode ficar dentro da árvore do repositório: $dest_abs"
fi
[[ -e "$dest_abs" ]] && fail "destino já existe (bundles são imutáveis): $dest_abs"

TMP_DIR=$(mktemp -d)
cleanup_all() { rm -rf "$TMP_DIR"; }
trap cleanup_all EXIT

psql_conn() { psql "$LEGACY_SNAPSHOT_CONN" -X -v ON_ERROR_STOP=1 "$@"; }

main() {
  mkdir -m 0700 "$dest_abs"
  mkdir "$dest_abs/sql" "$dest_abs/json" "$dest_abs/commands"
  chmod 700 "$dest_abs/sql" "$dest_abs/json" "$dest_abs/commands"

  # SQL ordenado de contagem por tabela com identificadores citados.
  local counts_sql
  counts_sql=$(psql_conn -qAt -v schema="$SCHEMA" \
    -c "SELECT COALESCE(string_agg(format('SELECT %L AS t, count(*)::bigint AS n FROM %I.%I', c.relname, :'schema', c.relname), E'\nUNION ALL\n' ORDER BY c.relname), 'SELECT NULL::text, NULL::bigint WHERE false') FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = :'schema' AND c.relkind = 'r';")

  [[ "$counts_sql" != *"WHERE false"* ]] || fail "nenhuma tabela visível no schema '$SCHEMA'; verifique o papel somente-leitura"
  [[ -n "$counts_sql" ]] || fail "nenhuma tabela visível no schema '$SCHEMA'"

  # Sessão única REPEATABLE READ READ ONLY grava todos os agregados ordenados.
  {
    echo "BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;"
    cat <<CONSISTENT_SQL
\\o "$dest_abs/json/server.json"
SELECT json_build_object('current_database', current_database(), 'server_version', current_setting('server_version'))::text;

\\o "$dest_abs/json/extensions.json"
SELECT COALESCE(json_agg(t ORDER BY t.extname), '[]'::json)::text
FROM (SELECT extname, extversion FROM pg_extension) t;

\\o "$dest_abs/json/roles.json"
SELECT COALESCE(json_agg(t ORDER BY t.rolname), '[]'::json)::text
FROM (
  SELECT rolname, rolsuper, rolbypassrls, rolcreaterole, rolcreatedb, rolcanlogin
  FROM pg_roles WHERE rolname NOT LIKE 'pg\_%'
) t;

\\o "$dest_abs/json/grants.json"
SELECT COALESCE(json_agg(t ORDER BY t.table_name, t.grantee, t.privilege_type), '[]'::json)::text
FROM (
  SELECT grantee, table_name, privilege_type
  FROM information_schema.role_table_grants
  WHERE table_schema = :'schema'
) t;

\\o "$dest_abs/json/sequences.json"
SELECT COALESCE(json_agg(t ORDER BY t.name), '[]'::json)::text
FROM (
  SELECT schemaname || '.' || sequencename AS name, start_value, min_value, max_value, increment_by, last_value
  FROM pg_sequences WHERE schemaname = :'schema'
) t;

\\o "$dest_abs/json/policies.json"
SELECT COALESCE(json_agg(t ORDER BY t.tablename, t.policyname), '[]'::json)::text
FROM (
  SELECT tablename, policyname, permissive, roles, cmd
  FROM pg_policies WHERE schemaname = :'schema'
) t;

\\o "$dest_abs/json/cron_jobs.json"
SELECT CASE
  WHEN to_regclass('cron.job') IS NULL THEN json_build_object('pg_cron_exposed', false)::text
  ELSE (
    SELECT json_build_object('pg_cron_exposed', true, 'jobs',
      COALESCE(json_agg(j ORDER BY j.jobid), '[]'::json)::text)
    FROM (SELECT jobid, schedule, command, database, username FROM cron.job) j
  )
END;

\\o "$dest_abs/json/row_counts.json"
SELECT COALESCE((SELECT json_object_agg(t, n ORDER BY t) FROM ($counts_sql) q WHERE t IS NOT NULL), '{}'::json)::text;
COMMIT;
CONSISTENT_SQL
  } | psql_conn -qAt -v schema="$SCHEMA" -f -

  # Dump único do schema: DDL, políticas, funções, triggers, extensões.
  pg_dump "$LEGACY_SNAPSHOT_CONN" --schema-only --no-owner --no-privileges \
    --schema="$SCHEMA" --file "$dest_abs/sql/schema.sql"
  chmod 600 "$dest_abs/sql/schema.sql"

  # Manifest de Storage: somente listagens; opcional.
  if [[ -n "${LEGACY_SNAPSHOT_STORAGE_URL:-}" ]]; then
    : "${LEGACY_SNAPSHOT_SERVICE_KEY:?LEGACY_SNAPSHOT_SERVICE_KEY é obrigatória com LEGACY_SNAPSHOT_STORAGE_URL}"
    base_url=${LEGACY_SNAPSHOT_STORAGE_URL%/}
    curl -sS -f -H "Authorization: Bearer $LEGACY_SNAPSHOT_SERVICE_KEY" \
      -o "$dest_abs/json/storage_buckets.json" "$base_url/bucket"
    local bucket
    while IFS= read -r bucket; do
      [[ "$bucket" =~ ^[A-Za-z0-9._-]+$ ]] || fail "nome de bucket inesperado: $bucket"
      curl -sS -f -H "Authorization: Bearer $LEGACY_SNAPSHOT_SERVICE_KEY" \
        -H 'Content-Type: application/json' \
        -d '{"prefix":"","limit":10000,"offset":0,"sortBy":{"column":"name","order":"asc"}}' \
        -o "$dest_abs/json/storage_objects_${bucket}.json" \
        "$base_url/object/list/$bucket"
    done < <(sed -nE 's/.*"name"[[:space:]]*:[[:space:]]*"([A-Za-z0-9._-]+)".*/\1/p' "$dest_abs/json/storage_buckets.json" | LC_ALL=C sort -u)
  else
    printf '%s\n' "etapa de storage pulada: LEGACY_SNAPSHOT_STORAGE_URL não definida" \
      > "$dest_abs/json/storage_skipped.txt"
  fi

  # Comandos de export Auth para o responsável (nunca executados aqui).
  cat > "$dest_abs/commands/auth-export.sql" <<'AUTHSQL'
-- Export Auth somente-leitura; executa com papel com SELECT em auth.*.
-- Alimenta docs/migration/ledgers/identity-mapping.md.
-- Nunca cole a saída em logs, issues ou commits.
\pset pager off
\echo '== auth.users (colunas minimas de identidade) =='
SELECT id, email, email_confirmed_at, created_at, updated_at
FROM auth.users ORDER BY id;

\echo '== auth.identities (issuer/provider subject) =='
SELECT user_id, provider, provider_id, last_sign_in_at
FROM auth.identities ORDER BY user_id, provider;
AUTHSQL
  cat > "$dest_abs/commands/auth-export-rest.sh" <<'AUTHREST'
#!/usr/bin/env bash
# Alternativa via Admin API paginada; apenas o responsável executa.
# Requer AUTH_EXPORT_SUPABASE_URL e AUTH_EXPORT_SERVICE_ROLE no ambiente,
# fornecidos pelo dono. Nunca commitar esses valores nem a resposta completa.
set -euo pipefail
: "${AUTH_EXPORT_SUPABASE_URL:?defina AUTH_EXPORT_SUPABASE_URL}"
: "${AUTH_EXPORT_SERVICE_ROLE:?defina AUTH_EXPORT_SERVICE_ROLE}"
page=1
while :; do
  resp=$(curl -sS -H "apikey: $AUTH_EXPORT_SERVICE_ROLE" \
              -H "Authorization: Bearer $AUTH_EXPORT_SERVICE_ROLE" \
              "$AUTH_EXPORT_SUPABASE_URL/auth/v1/admin/users?page=$page&per_page=200")
  printf '%s\n' "$resp" | grep -oE '"id":"[^"]+"|"email":"[^"]*"' || true
  printf '%s\n' "$resp" | grep -q '"nextPage":"[A-Za-z0-9=-]+"' || break
  page=$((page + 1))
done
AUTHREST
  chmod 600 "$dest_abs/commands/auth-export.sql"
  chmod 700 "$dest_abs/commands/auth-export-rest.sh"
}

if ! main "$@"; then
  rm -rf "$dest_abs"
  fail "captura interrompida; bundle parcial descartado (evitar capturas mistas)"
fi

# Metadados ficam FORA do manifesto (timestamp varia entre execuções).
now_utc=$(date -u +%Y-%m-%dT%H:%M:%SZ)
{
  echo '{'
  echo '  "bundle_schema": "1",'
  echo '  "tool": "tools/legacy-snapshot/snapshot.sh",'
  echo "  \"captured_at_utc\": \"$now_utc\","
  echo '  "note": "payload sob hash: sql/, json/, commands/; ver manifest.sha256; sem credenciais"'
  echo '}'
} > "$dest_abs/bundle_metadata.json"

# Manifesto SHA-256 determinístico sobre caminhos ordenados.
(
  cd "$dest_abs" || exit 1
  : > manifest.sha256
  find sql json commands -type f -print | LC_ALL=C sort |
    while IFS= read -r f; do
      printf '%s  %s\n' "$(digest_of "$f")" "$f" >> manifest.sha256
    done
)

file_count=$(find "$dest_abs/sql" "$dest_abs/json" "$dest_abs/commands" -type f | wc -l | tr -d ' ')
echo "bundle pronto: $dest_abs ($file_count arquivos); manifesto: manifest.sha256"
