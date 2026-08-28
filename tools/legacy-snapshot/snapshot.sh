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
#     diretório irmão privado 0700 com nome mktemp+PID (posse inequívoca) e a
#     publicação REIVINDICA o destino com mkdir atômico: dois publicadores
#     concorrentes nunca aninham nem sobrescrevem caminho alheio, e a limpeza
#     só remove caminhos criados por ESTE processo.
#   - A connection string nunca é impressa nem entra no bundle.
#   - O bundle contém metadados de identidade e política: confidencial; destino,
#     retenção e destruição são decididos e registrados pelo responsável.
#   - Consistência: todos os agregados saem de uma única transação
#     REPEATABLE READ READ ONLY; o DDL vem de uma única passada do pg_dump.
#     Capturas parciais misturadas invalidam as ledgers e nunca são aproveitadas:
#     qualquer falha — inclusive INT/TERM/HUP — descarta o bundle inteiro.
#   - Inventário de Storage parseado estruturalmente (JSON): a listagem da API
#     é HIERÁRQUICA (storage.search devolve placeholders de pasta e só objetos
#     do próprio nível), então o inventário percorre recursivamente os prefixos
#     de pasta, paginando cada nível — sem teto silencioso de 10 mil objetos e
#     sem nível aninhado omitido.

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
# final), com nome mktemp + PID: posse inequívoca para a limpeza. O bundle só
# aparece no destino após a reivindicação atômica da publicação.
TMP_DIR=$(mktemp -d)
STAGE_DIR=$(mktemp -d "$dest_parent/.vinx-snapshot.$$.XXXXXX")
chmod 700 "$STAGE_DIR"
BUNDLE_COMPLETE=0
DEST_CLAIMED=0

# Remove o destino REIVINDICADO por este processo (marca de PID confere).
remove_claimed_dest() {
  if [[ "$DEST_CLAIMED" == 1 && -n "${dest_abs:-}" ]]; then
    if [[ "$(cat "$dest_abs/.vinx-owner" 2>/dev/null || true)" == "$$" ]]; then
      rm -rf "$dest_abs"
    fi
  fi
}

cleanup_all() {
  db_teardown_secrets
  rm -rf "$TMP_DIR" "${STAGE_DIR:-}"
  [[ "$BUNDLE_COMPLETE" == 1 ]] || remove_claimed_dest
}
# Limpeza de abort remove APENAS caminhos criados por este processo: o staging
# mktemp+PID e o destino que este processo reivindicou (marca .vinx-owner).
# Um bundle concorrente concluído — mesmo aparecendo durante a captura — é
# intocável; o antigo rm -rf incondicional do destino podia destruí-lo.
abort_partial() {
  if [[ "$BUNDLE_COMPLETE" != 1 ]]; then
    rm -rf "${STAGE_DIR:-}"
    remove_claimed_dest
    echo "erro: captura interrompida; bundle parcial descartado (evitar capturas mistas)" >&2
  fi
  exit 1
}
trap cleanup_all EXIT
trap abort_partial ERR INT TERM HUP

db_setup   # converte a connection string em pgpass 0600; nunca em argv
psql_conn() { db_psql "$@"; }

# Inventário de um bucket percorrendo a HIERARQUIA da Storage API: cada
# resposta de /object/list devolve placeholders de pasta (id null) e apenas os
# objetos do nível do prefixo, com nomes RELATIVOS a ele (storage.search:
# path_tokens[níveis do prefixo]). O inventário mantém uma fila de prefixos:
# cada pasta descoberta enfileira prefixo+"/" para nova listagem paginada. Sem
# isso, objetos abaixo de pastas ficam silenciosamente fora do manifesto.
# Tetos de segurança (profundidade, pastas, objetos) + guarda de progresso
# (primeiro nome não pode repetir entre páginas do mesmo prefixo).
STORAGE_PAGE_SIZE=1000
STORAGE_MAX_OBJECTS=1000000
STORAGE_MAX_FOLDERS=10000
STORAGE_MAX_PREFIX_DEPTH=32

snapshot_bucket_objects() {
  local base_url=$1 auth_header=$2 bucket=$3 out=$4
  local pages_dir="$TMP_DIR/storage-pages/$bucket"
  mkdir -p "$pages_dir"
  local queue=("")
  local total=0 part=0 folders_seen=0
  local prefix offset page_file folders_file meta count first prev_first child slashes
  while ((${#queue[@]} > 0)); do
    prefix=${queue[0]}
    if ((${#queue[@]} > 1)); then
      queue=("${queue[@]:1}")
    else
      queue=()
    fi
    offset=0
    prev_first="<unset>"
    while :; do
      part=$((part + 1))
      page_file="$pages_dir/page-$part.json"
      folders_file="$pages_dir/page-$part.folders"
      curl -sS -f --header @"$auth_header" \
        -H 'Content-Type: application/json' \
        -d "{\"prefix\":\"$prefix\",\"limit\":$STORAGE_PAGE_SIZE,\"offset\":$offset,\"sortBy\":{\"column\":\"name\",\"order\":\"asc\"}}" \
        -o "$page_file" "$base_url/object/list/$bucket"
      printf '%s' "$prefix" > "$pages_dir/page-$part.prefix"
      # Parse e validação estrutural em comando FOREGROUND: falha do python3
      # (JSON inválido, página/entrada fora de forma) propaga e aborta o bundle
      # — substituição de processo não propagaria o status do filho.
      meta=$(python3 -c 'import json, sys
page = json.load(open(sys.argv[1], encoding="utf-8"))
if not isinstance(page, list):
    raise SystemExit("pagina de objetos inesperada")
prefix = sys.argv[3]
folders = []
for item in page:
    if not isinstance(item, dict):
        raise SystemExit("entrada de objeto invalida")
    raw = item.get("name")
    if not isinstance(raw, str) or not raw:
        raise SystemExit("nome de objeto invalido")
    name = raw[len(prefix):] if prefix and raw.startswith(prefix) else raw
    if item.get("id") is None:
        if "/" in name or not name:
            raise SystemExit("nome de pasta invalido")
        folders.append(name)
with open(sys.argv[2], "w", encoding="utf-8") as fh:
    fh.write("".join(name + "\n" for name in folders))
sys.stdout.write(str(len(page)) + "\t" + (page[0]["name"] if page else ""))' \
        "$page_file" "$folders_file" "$prefix") ||
        fail "resposta de objetos invalida para o bucket $bucket (prefixo $prefix)"
      count=${meta%%$'\t'*}
      first=${meta#*$'\t'}
      [[ "$first" != "$prev_first" ]] ||
        fail "paginacao do storage nao avanca para o bucket $bucket (prefixo $prefix, offset=$offset)"
      prev_first=$first
      total=$((total + count))
      (( total < STORAGE_MAX_OBJECTS )) ||
        fail "inventario do bucket $bucket excede o teto de seguranca de $STORAGE_MAX_OBJECTS objetos"
      while IFS= read -r child; do
        [[ -n "$child" ]] || continue
        child="$prefix$child/"
        slashes=${child//[!/]/}
        ((${#slashes} <= STORAGE_MAX_PREFIX_DEPTH)) ||
          fail "profundidade de pastas excede o teto de $STORAGE_MAX_PREFIX_DEPTH niveis no bucket $bucket"
        folders_seen=$((folders_seen + 1))
        (( folders_seen <= STORAGE_MAX_FOLDERS )) ||
          fail "inventario do bucket $bucket excede o teto de seguranca de $STORAGE_MAX_FOLDERS pastas"
        queue+=("$child")
      done < "$folders_file"
      if (( count < STORAGE_PAGE_SIZE )); then
        break
      fi
      offset=$((offset + STORAGE_PAGE_SIZE))
    done
  done
  # Mesclagem: nomes reabsolutizados por prefixo (nível devolve nome relativo),
  # dedup por nome e ordenação estável — o manifesto é o inventário completo.
  python3 - "$out" "$pages_dir" <<'PY' > /dev/null
import glob
import json
import sys

out_path, pages_dir = sys.argv[1], sys.argv[2]
items = {}
for path in sorted(glob.glob(pages_dir + "/page-*.json")):
    with open(path, encoding="utf-8") as fh:
        page = json.load(fh)
    if not isinstance(page, list):
        raise SystemExit("pagina de objetos inesperada")
    with open(path[: -len(".json")] + ".prefix", encoding="utf-8") as fh:
        prefix = fh.read()
    for item in page:
        if not isinstance(item, dict):
            raise SystemExit("entrada de objeto invalida")
        name = str(item.get("name", ""))
        if prefix and not name.startswith(prefix):
            name = prefix + name
        entry = dict(item)
        entry["name"] = name
        items[name] = entry
with open(out_path, "w", encoding="utf-8") as fh:
    json.dump([items[name] for name in sorted(items)], fh, ensure_ascii=False, separators=(",", ":"))
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
    # Parse dos buckets em comando FOREGROUND com saída em arquivo checado:
    # JSON inválido, resposta não-lista ou entrada fora de forma (sem nome,
    # nome fora do alfabeto seguro) derruba o python3 e o set -e aborta ANTES
    # do manifesto/publicação. Substituição de processo (`< <(...)`) não
    # propaga o status do filho: zero iterações seguiriam como sucesso.
    local bucket_parser="$TMP_DIR/parse-buckets.py"
    cat > "$bucket_parser" <<'PY'
import json
import re
import sys

with open(sys.argv[1], encoding="utf-8") as fh:
    buckets = json.load(fh)
if not isinstance(buckets, list):
    raise SystemExit("resposta de buckets inesperada")
names = set()
for bucket in buckets:
    if not isinstance(bucket, dict) or not isinstance(bucket.get("name"), str) or not bucket["name"]:
        raise SystemExit("entrada de bucket invalida")
    if not re.fullmatch(r"[A-Za-z0-9._-]+", bucket["name"]):
        raise SystemExit("nome de bucket inesperado: " + bucket["name"])
    names.add(bucket["name"])
sys.stdout.write("".join(name + "\n" for name in sorted(names)))
PY
    local bucket_list="$TMP_DIR/storage-buckets.list"
    python3 "$bucket_parser" "$bundle_root/json/storage_buckets.json" > "$bucket_list" ||
      fail "resposta de buckets invalida"
    local bucket
    while IFS= read -r bucket; do
      snapshot_bucket_objects "$base_url" "$auth_header" "$bucket" \
        "$bundle_root/json/storage_objects_${bucket}.json"
    done < "$bucket_list"
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

  # Publicação com REIVINDICAÇÃO atômica de propriedade: mkdir só vence se o
  # destino NÃO existir — dois publicadores concorrentes nunca aninham o bundle
  # um dentro do outro nem sobrescrevem caminho alheio (o mv antigo aninhava
  # silenciosamente e reportava sucesso). A partir da reivindicação o destino
  # pertence a ESTE processo (marca .vinx-owner com PID); o conteúdo preparado
  # é movido para dentro e o bundle só é declarado pronto com manifesto verificado.
  if ! mkdir "$dest_abs"; then
    fail "destino já existe (bundles são imutáveis): $dest_abs"
  fi
  DEST_CLAIMED=1
  chmod 700 "$dest_abs"
  printf '%s\n' "$$" > "$dest_abs/.vinx-owner"
  shopt -s dotglob
  local entry
  for entry in "$STAGE_DIR"/*; do
    mv "$entry" "$dest_abs/"
  done
  shopt -u dotglob
  rmdir "$STAGE_DIR"
  STAGE_DIR=
  [[ -f "$dest_abs/manifest.sha256" ]] ||
    fail "publicação incompleta: manifest.sha256 ausente em $dest_abs"
  rm -f "$dest_abs/.vinx-owner"
  BUNDLE_COMPLETE=1
  DEST_CLAIMED=0
  echo "bundle pronto: $dest_abs ($file_count arquivos); manifesto: manifest.sha256"
}

main
