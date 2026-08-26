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
set -o errtrace
# shellcheck source=lib-db.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)/lib-db.sh"

fail() { echo "erro: $*" >&2; exit 1; }

[[ $# -eq 1 ]] || { echo "uso: LEGACY_SNAPSHOT_CONN=... $0 <diretorio-de-saida>" >&2; exit 2; }
SCHEMA="${LEGACY_SNAPSHOT_SCHEMA:-public}"
: "${LEGACY_SNAPSHOT_CONN:?LEGACY_SNAPSHOT_CONN é obrigatória}"

for bin in pg_dump psql; do
  command -v "$bin" >/dev/null || fail "$bin não encontrado no PATH"
done

digest_of() {
  # Cadeia de fallbacks; ambientes sem utilitário sha256 são rejeitados.
  local d=""
  if command -v shasum >/dev/null 2>&1; then
    d=$(shasum -a 256 "$1" | awk '{print $1}')
  elif command -v sha256sum >/dev/null 2>&1; then
    d=$(sha256sum "$1" | awk '{print $1}')
  elif command -v openssl >/dev/null 2>&1; then
    d=$(openssl dgst -sha256 -r "$1" | awk '{print $1}')
  fi
  [[ -n "$d" ]] || fail "nenhum utilitário SHA-256 encontrado no ambiente"
  printf '%s' "$d"
}

dest_parent=$(dirname "$1")
dest_name=$(basename "$1")
mkdir -p "$dest_parent"
dest_abs="$(cd "$dest_parent" && pwd -P)/$dest_name"

if repo_root=$(git rev-parse --show-toplevel 2>/dev/null); then
  repo_root_phys=$(cd "$repo_root" && pwd -P)
  case "$dest_abs" in
    "$repo_root_phys"|"$repo_root_phys"/*)
      fail "diretório de saída não pode ficar dentro da árvore do repositório: $dest_abs"
      ;;
  esac
fi
[[ "$dest_abs" =~ [[:space:]\'\"] ]] &&
  fail "diretório de saída não pode conter espaços ou aspas: $dest_abs"
[[ -e "$dest_abs" ]] && fail "destino já existe (bundles são imutáveis): $dest_abs"

TMP_DIR=$(mktemp -d)
cleanup_all() {
  db_teardown_secrets
  rm -rf "$TMP_DIR"
}
discard_partial_bundle() {
  if [[ -d "${dest_abs:-}" && "${BUNDLE_OPENED:-0}" == 1 ]]; then
    rm -rf "$dest_abs"
    echo "erro: captura interrompida; bundle parcial descartado (evitar capturas mistas)" >&2
    exit 1
  fi
}
set -o errtrace
trap cleanup_all EXIT
trap discard_partial_bundle ERR

db_setup   # converte a connection string em pgpass 0600; nunca em argv
psql_conn() { db_psql "$@"; }

main() {
  mkdir -m 0700 "$dest_abs"
  mkdir "$dest_abs/sql" "$dest_abs/json" "$dest_abs/commands"
  chmod 700 "$dest_abs/sql" "$dest_abs/json" "$dest_abs/commands"
  BUNDLE_OPENED=1


  # Sessão única REPEATABLE READ READ ONLY grava todos os agregados ordenados.
  # Delimitador QUOTED: o corpo contém dollar-quoting plpgsql ($fn$) que JAMAIS
  # pode passar pela expansão do shell; caminhos entram por token __DIR__ e o
  # destino é validado sem espaços/aspas (\o não interpreta aspas).
  cat > "$TMP_DIR/consistent.tpl" <<'CONSISTENT_SQL'
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SELECT set_config('vinx.snapshot.schema', :'schema', true);
\o __DIR__/json/server.json
SELECT json_build_object('current_database', current_database(), 'server_version', current_setting('server_version'))::text;

\o __DIR__/json/extensions.json
SELECT COALESCE(json_agg(t ORDER BY t.extname), '[]'::json)::text
FROM (SELECT extname, extversion FROM pg_extension) t;

\o __DIR__/json/roles.json
SELECT COALESCE(json_agg(t ORDER BY t.rolname), '[]'::json)::text
FROM (
  SELECT rolname, rolsuper, rolbypassrls, rolcreaterole, rolcreatedb, rolcanlogin
  FROM pg_roles WHERE rolname NOT LIKE 'pg\_%'
) t;

\o __DIR__/json/grants.json
SELECT COALESCE(json_agg(t ORDER BY t.table_name, t.grantee, t.privilege_type), '[]'::json)::text
FROM (
  SELECT grantee, table_name, privilege_type
  FROM information_schema.role_table_grants
  WHERE table_schema = :'schema'
) t;

\o __DIR__/json/sequences.json
SELECT COALESCE(json_agg(t ORDER BY t.name), '[]'::json)::text
FROM (
  SELECT schemaname || '.' || sequencename AS name, start_value, min_value, max_value, increment_by, last_value
  FROM pg_sequences WHERE schemaname = :'schema'
) t;

\o __DIR__/json/policies.json
SELECT COALESCE(json_agg(t ORDER BY t.tablename, t.policyname), '[]'::json)::text
FROM (
  SELECT tablename, policyname, permissive, roles, cmd
  FROM pg_policies WHERE schemaname = :'schema'
) t;

\o __DIR__/json/cron_jobs.json
-- Condicional no CLIENTE: o planner resolve relações mesmo em ramos não tomados.
SELECT to_regclass('cron.job') IS NOT NULL AS has_cron \gset
\if :has_cron
SELECT json_build_object('pg_cron_exposed', true, 'jobs',
  COALESCE(json_agg(j ORDER BY j.jobid), '[]'::json)::text)::text
FROM (SELECT jobid, schedule, command, database, username FROM cron.job) j;
\else
SELECT json_build_object('pg_cron_exposed', false)::text;
\endif

\o __DIR__/json/.counts.gen
-- Descoberta das contagens na MESMA transação repeatable read; os comandos
-- gerados (ordenados) rodam em seguida na própria sessão via \gexec.
SELECT format('SELECT %L AS t, count(*)::bigint AS n FROM %I.%I', c.relname, :'schema', c.relname)
FROM pg_class c JOIN pg_namespace cn ON cn.oid = c.relnamespace
WHERE cn.nspname = :'schema' AND c.relkind = 'r'
ORDER BY c.relname;
\o __DIR__/json/.counts.raw
\gexec

\o __DIR__/json/.session-done
SELECT 'ok';
\o
COMMIT;
CONSISTENT_SQL
  while IFS= read -r line; do
    printf '%s\n' "${line//__DIR__/$dest_abs}"
  done < "$TMP_DIR/consistent.tpl" > "$TMP_DIR/consistent.sql"
  psql_conn -qAt -v schema="$SCHEMA" -f "$TMP_DIR/consistent.sql"
  [[ -f "$dest_abs/json/.session-done" ]] || fail "sessão consistente não concluiu"
  rm -f "$dest_abs/json/.session-done"

  # Converte o fluxo bruto t|n (mesma sessão consistente) em JSON ordenado.
  local raw="$dest_abs/json/.counts.raw" gen="$dest_abs/json/.counts.gen"
  [[ -f "$raw" && -f "$gen" ]] || fail "fluxo de contagens ausente da sessão consistente"
  {
    printf '{'
    local sep="" line t n
    while IFS='|' read -r t n; do
      [[ -n "$sep" ]] && printf ','
      printf '"%s":%s' "$t" "$n"
      sep=1
    done < "$raw"
    printf '}'
  } > "$dest_abs/json/row_counts.json"
  rm -f "$raw" "$gen"

  # Dump único do schema: DDL, políticas, funções, triggers, extensões.
  db_pg_dump --schema-only --no-owner --no-privileges \
    --schema="$SCHEMA" --file "$dest_abs/sql/schema.sql"
  chmod 600 "$dest_abs/sql/schema.sql"

  # Manifest de Storage: somente listagens; opcional.
  if [[ -n "${LEGACY_SNAPSHOT_STORAGE_URL:-}" ]]; then
    : "${LEGACY_SNAPSHOT_SERVICE_KEY:?LEGACY_SNAPSHOT_SERVICE_KEY é obrigatória com LEGACY_SNAPSHOT_STORAGE_URL}"
    base_url=${LEGACY_SNAPSHOT_STORAGE_URL%/}
    auth_header=$(db_bearer_header_file "$LEGACY_SNAPSHOT_SERVICE_KEY")
    curl -sS -f --header @"$auth_header" \
      -o "$dest_abs/json/storage_buckets.json" "$base_url/bucket"
    local bucket
    while IFS= read -r bucket; do
      [[ "$bucket" =~ ^[A-Za-z0-9._-]+$ ]] || fail "nome de bucket inesperado: $bucket"
      curl -sS -f --header @"$auth_header" \
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

BUNDLE_OPENED=0
main

unset BUNDLE_OPENED

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
      d=$(digest_of "$f") || fail "falha ao calcular digest de $f"
      [[ -n "$d" ]] || fail "digest vazio para $f"
      printf '%s  %s\n' "$d" "$f" >> manifest.sha256
    done
)

file_count=$(find "$dest_abs/sql" "$dest_abs/json" "$dest_abs/commands" -type f | wc -l | tr -d ' ')
echo "bundle pronto: $dest_abs ($file_count arquivos); manifesto: manifest.sha256"
