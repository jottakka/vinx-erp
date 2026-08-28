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
#   - Saída recusada dentro da árvore do repositório (raiz resolvida a partir
#     do DIRETÓRIO DO SCRIPT, nunca do cwd de quem chama); construção ocorre em
#     diretório irmão privado 0700 e a publicação é um único rename atômico.
#   - A connection string nunca é impressa nem entra no bundle.
#   - O bundle contém metadados de identidade e política: confidencial; destino,
#     retenção e destruição são decididos e registrados pelo responsável.
#   - Consistência: todos os agregados saem de uma única transação
#     REPEATABLE READ READ ONLY; o DDL vem de uma única passada do pg_dump.
#     Capturas parciais misturadas invalidam as ledgers e nunca são aproveitadas:
#     qualquer falha — inclusive INT/TERM/HUP — descarta o bundle inteiro.
#   - Inventário de Storage parseado estruturalmente (JSON) e paginado: sem
#     teto silencioso de 10 mil objetos por bucket.

set -euo pipefail
set -o errtrace

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
# shellcheck source=lib-db.sh disable=SC1091  # caminho resolvido em runtime
source "$SCRIPT_DIR/lib-db.sh"

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

# A raiz do repositório é resolvida a partir do DIRETÓRIO DO SCRIPT: chamar o
# caminho absolto a partir de /tmp não pode burlar a guarda de confidencialidade.
if repo_root=$(git -C "$SCRIPT_DIR" rev-parse --show-toplevel 2>/dev/null); then
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

# Construção em diretório irmão PRIVADO (mesmo sistema de arquivos do destino
# final): o bundle só aparece no destino completo, por rename atômico único.
TMP_DIR=$(mktemp -d)
STAGE_DIR=$(mktemp -d "$dest_parent/.vinx-snapshot.XXXXXX")
chmod 700 "$STAGE_DIR"
BUNDLE_COMPLETE=0

cleanup_all() {
  db_teardown_secrets
  rm -rf "$TMP_DIR" "${STAGE_DIR:-}"
}
abort_partial() {
  if [[ "$BUNDLE_COMPLETE" != 1 ]]; then
    rm -rf "${STAGE_DIR:-}"
    if [[ -e "${dest_abs:-}" ]]; then
      rm -rf "$dest_abs"
    fi
    echo "erro: captura interrompida; bundle parcial descartado (evitar capturas mistas)" >&2
  fi
  exit 1
}
trap cleanup_all EXIT
trap abort_partial ERR INT TERM HUP

db_setup   # converte a connection string em pgpass 0600; nunca em argv
psql_conn() { db_psql "$@"; }

# Inventário de um bucket: páginas de 1000 objetos até a resposta curta; as
# páginas são mescladas estruturalmente (JSON) em um array ordenado por nome.
# Teto de segurança de 1e6 objetos por bucket evita loop infinito se a API
# ignorar o offset — e a repetição do primeiro nome também falha fechado.
STORAGE_PAGE_SIZE=1000
STORAGE_MAX_OBJECTS=1000000

snapshot_bucket_objects() {
  local base_url=$1 auth_header=$2 bucket=$3 out=$4
  local offset=0 page=0 count=0 total=0 first="" prev_first="<unset>"
  local page_file
  while :; do
    page=$((page + 1))
    page_file="$TMP_DIR/storage-page-${bucket}-${page}.json"
    curl -sS -f --header @"$auth_header" \
      -H 'Content-Type: application/json' \
      -d "{\"prefix\":\"\",\"limit\":$STORAGE_PAGE_SIZE,\"offset\":$offset,\"sortBy\":{\"column\":\"name\",\"order\":\"asc\"}}" \
      -o "$page_file" "$base_url/object/list/$bucket"
    count=$(python3 -c 'import json, sys
page = json.load(open(sys.argv[1], encoding="utf-8"))
if not isinstance(page, list):
    raise SystemExit("pagina de objetos inesperada")
print(len(page))' "$page_file") || fail "resposta de objetos invalida para o bucket $bucket"
    first=$(python3 -c 'import json, sys
page = json.load(open(sys.argv[1], encoding="utf-8"))
print(page[0].get("name", "") if page and isinstance(page[0], dict) else "")' "$page_file") \
      || fail "pagina de objetos invalida para o bucket $bucket"
    [[ "$first" != "$prev_first" ]] ||
      fail "paginacao do storage nao avanca para o bucket $bucket (offset=$offset)"
    prev_first=$first
    total=$((total + count))
    if (( count < STORAGE_PAGE_SIZE )); then
      break
    fi
    offset=$((offset + STORAGE_PAGE_SIZE))
    (( total < STORAGE_MAX_OBJECTS )) ||
      fail "inventario do bucket $bucket excede o teto de seguranca de $STORAGE_MAX_OBJECTS objetos"
  done
  python3 - "$out" "$TMP_DIR/storage-page-${bucket}-" <<'PY' > /dev/null
import glob
import json
import sys

out_path, prefix = sys.argv[1], sys.argv[2]
items = []
for path in sorted(glob.glob(prefix + "*.json")):
    with open(path, encoding="utf-8") as fh:
        page = json.load(fh)
    if not isinstance(page, list):
        raise SystemExit("pagina de objetos inesperada")
    items.extend(page)
items.sort(key=lambda item: str(item.get("name", "")) if isinstance(item, dict) else "")
with open(out_path, "w", encoding="utf-8") as fh:
    json.dump(items, fh, ensure_ascii=False, separators=(",", ":"))
    fh.write("\n")
PY
}

main() {
  local bundle_root=$STAGE_DIR
  mkdir "$bundle_root/sql" "$bundle_root/json" "$bundle_root/commands"
  chmod 700 "$bundle_root/sql" "$bundle_root/json" "$bundle_root/commands"


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
    printf '%s\n' "${line//__DIR__/$bundle_root}"
  done < "$TMP_DIR/consistent.tpl" > "$TMP_DIR/consistent.sql"
  psql_conn -qAt -v schema="$SCHEMA" -f "$TMP_DIR/consistent.sql"
  [[ -f "$bundle_root/json/.session-done" ]] || fail "sessão consistente não concluiu"
  rm -f "$bundle_root/json/.session-done"

  # Converte o fluxo bruto t|n (mesma sessão consistente) em JSON ordenado.
  local raw="$bundle_root/json/.counts.raw" gen="$bundle_root/json/.counts.gen"
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
  } > "$bundle_root/json/row_counts.json"
  rm -f "$raw" "$gen"

  # Dump único do schema: DDL, políticas, funções, triggers, extensões.
  db_pg_dump --schema-only --no-owner --no-privileges \
    --schema="$SCHEMA" --file "$bundle_root/sql/schema.sql"
  chmod 600 "$bundle_root/sql/schema.sql"

  # Manifest de Storage: somente listagens; opcional.
  if [[ -n "${LEGACY_SNAPSHOT_STORAGE_URL:-}" ]]; then
    : "${LEGACY_SNAPSHOT_SERVICE_KEY:?LEGACY_SNAPSHOT_SERVICE_KEY é obrigatória com LEGACY_SNAPSHOT_STORAGE_URL}"
    command -v python3 >/dev/null 2>&1 ||
      fail "python3 é obrigatório para o inventário de storage (parse JSON)"
    local base_url=${LEGACY_SNAPSHOT_STORAGE_URL%/}
    local auth_header
    auth_header=$(db_new_bearer_header_file "$LEGACY_SNAPSHOT_SERVICE_KEY")
    db_track_bearer_file "$auth_header"   # registra no shell PAI; teardown inclui o arquivo
    curl -sS -f --header @"$auth_header" \
      -o "$bundle_root/json/storage_buckets.json" "$base_url/bucket"
    local bucket
    while IFS= read -r bucket; do
      [[ "$bucket" =~ ^[A-Za-z0-9._-]+$ ]] || fail "nome de bucket inesperado: $bucket"
      snapshot_bucket_objects "$base_url" "$auth_header" "$bucket" \
        "$bundle_root/json/storage_objects_${bucket}.json"
    done < <(python3 -c 'import json, sys
buckets = json.load(open(sys.argv[1], encoding="utf-8"))
if not isinstance(buckets, list):
    raise SystemExit("resposta de buckets inesperada")
names = sorted({str(b["name"]) for b in buckets if isinstance(b, dict) and "name" in b})
sys.stdout.write("".join(name + "\n" for name in names))' "$bundle_root/json/storage_buckets.json")
  else
    printf '%s\n' "etapa de storage pulada: LEGACY_SNAPSHOT_STORAGE_URL não definida" \
      > "$bundle_root/json/storage_skipped.txt"
  fi

  # Comandos de export Auth para o responsável (nunca executados aqui).
  cat > "$bundle_root/commands/auth-export.sql" <<'AUTHSQL'
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
  cat > "$bundle_root/commands/auth-export-rest.sh" <<'AUTHREST'
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
  chmod 600 "$bundle_root/commands/auth-export.sql"
  chmod 700 "$bundle_root/commands/auth-export-rest.sh"

  # Metadados ficam FORA do manifesto (timestamp varia entre execuções).
  local now_utc
  now_utc=$(date -u +%Y-%m-%dT%H:%M:%SZ)
  {
    echo '{'
    echo '  "bundle_schema": "1",'
    echo '  "tool": "tools/legacy-snapshot/snapshot.sh",'
    echo "  \"captured_at_utc\": \"$now_utc\","
    echo '  "note": "payload sob hash: sql/, json/, commands/; ver manifest.sha256; sem credenciais"'
    echo '}'
  } > "$bundle_root/bundle_metadata.json"

  # Manifesto SHA-256 determinístico sobre caminhos ordenados.
  : > "$bundle_root/manifest.sha256"
  local rel d
  while IFS= read -r rel; do
    d=$(digest_of "$bundle_root/$rel") || fail "falha ao calcular digest de $rel"
    [[ -n "$d" ]] || fail "digest vazio para $rel"
    printf '%s  %s\n' "$d" "$rel" >> "$bundle_root/manifest.sha256"
  done < <(cd "$bundle_root" && find sql json commands -type f -print | LC_ALL=C sort)
  [[ -s "$bundle_root/manifest.sha256" ]] || fail "manifesto vazio"

  local file_count
  file_count=$(find "$bundle_root/sql" "$bundle_root/json" "$bundle_root/commands" -type f | wc -l | tr -d ' ')

  # Publicação atômica: até este rename o destino NUNCA existiu; bundle parcial
  # é estruturalmente invisível para consumidores.
  mv "$STAGE_DIR" "$dest_abs"
  STAGE_DIR=
  BUNDLE_COMPLETE=1
  echo "bundle pronto: $dest_abs ($file_count arquivos); manifesto: manifest.sha256"
}

main
