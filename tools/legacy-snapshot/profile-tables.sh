#!/usr/bin/env bash
# Profiling AGREGADO do estate legado, executado pelo responsável, para apoiar o
# preenchimento das ledgers (ex.: vocabulário de status).
#
# Uso:
#   LEGACY_SNAPSHOT_CONN='postgresql://...' \
#     tools/legacy-snapshot/profile-tables.sh <config.txt> <diretorio-de-saida>
#
# Formato do config (texto simples, uma entrada por linha):
#   # comentários e linhas vazias são ignorados
#   tabela.coluna   (ordene o arquivo para saída reprodutível)
#
# Comportamento por entrada `tabela.coluna` encontrada no schema:
#   - nível de tabela: contagem exata, aproximada (reltuples) e tamanho total;
#   - nível de coluna: contagem de nulos e taxa de nulos;
#   - colunas texto/booleano/enum: TOP 100 valores distintos com contagem;
#   - colunas data/hora: min e max;
#   - outras categorias: apenas agregados de contagem — nenhum valor bruto sai;
#   - entradas ausentes no schema não aparecem na saída (compare com o config).
#
# Garantias: TODO bloco emitido é autossuficiente (subselects de catálogo com
# joins próprios, sem alias externo); toda consulta é agregação com limite fixo
# (LIMIT 100); nenhum caminho retorna linha completa; a SQL executada é gravada
# na saída para auditoria; qualquer falha descarta a saída parcial inteira.

set -euo pipefail
set -o errtrace

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
# shellcheck source=lib-db.sh disable=SC1091  # caminho resolvido em runtime
source "$SCRIPT_DIR/lib-db.sh"

# Falha fechada descartando saída parcial: fail() e sinais/erros cobertos.
fail() {
  echo "erro: $*" >&2
  if [[ -d "${dest_abs:-}" && "${BUNDLE_OPENED:-0}" == 1 ]]; then
    rm -rf "$dest_abs"
    echo "erro: profiler interrompido; saída parcial descartada" >&2
  fi
  exit 2
}
validate_name() {
  [[ "$1" =~ ^[a-z_][a-z_0-9]*$ ]] || fail "nome fora do padrão seguro: $2"
}

[[ $# -eq 2 ]] || { echo "uso: LEGACY_SNAPSHOT_CONN=... $0 <config.txt> <diretorio-de-saida>" >&2; exit 2; }
SCHEMA="${LEGACY_SNAPSHOT_SCHEMA:-public}"
validate_name "$SCHEMA" LEGACY_SNAPSHOT_SCHEMA
CONFIG_FILE=$1
: "${LEGACY_SNAPSHOT_CONN:?LEGACY_SNAPSHOT_CONN é obrigatória}"
[[ -r "$CONFIG_FILE" ]] || fail "config não legível: $CONFIG_FILE"
command -v psql >/dev/null || fail "psql não encontrado"

dest_parent=$(dirname "$2")
dest_name=$(basename "$2")
mkdir -p "$dest_parent"
dest_abs="$(cd "$dest_parent" && pwd -P)/$dest_name"
# Raiz do repositório resolvida a partir do DIRETÓRIO DO SCRIPT: o cwd de quem
# chama não pode burlar a guarda (probe a partir de /tmp é detectado).
if repo_root=$(git -C "$SCRIPT_DIR" rev-parse --show-toplevel 2>/dev/null); then
  repo_root_phys=$(cd "$repo_root" && pwd -P)
  case "$dest_abs" in
    "$repo_root_phys"|"$repo_root_phys"/*)
      fail "diretório de saída não pode ficar dentro da árvore do repositório: $dest_abs"
      ;;
  esac
fi
[[ -e "$dest_abs" ]] && fail "destino já existe: $dest_abs"

TMP_DIR=$(mktemp -d)
cleanup_all() {
  db_teardown_secrets
  rm -rf "$TMP_DIR"
}
discard_partial_bundle() {
  if [[ -d "${dest_abs:-}" && "${BUNDLE_OPENED:-0}" == 1 ]]; then
    rm -rf "$dest_abs"
    echo "erro: profiler interrompido; saída parcial descartada" >&2
  fi
  exit 2
}
trap cleanup_all EXIT
trap discard_partial_bundle ERR INT TERM HUP

db_setup   # converte a connection string em pgpass 0600; nunca em argv
psql_conn() { db_psql "$@"; }

# Átomos citados no servidor; voltam prontos para colar como identificadores/literais.
# Entradas já validadas com o charset [a-z_][a-z_0-9]* — literais seguros.
q_ident() { psql_conn -qAt -c "SELECT quote_ident('$1');"; }
q_table() { psql_conn -qAt -c "SELECT quote_ident('$1') || '.' || quote_ident('$2');"; }
q_text() { psql_conn -qAt -c "SELECT quote_literal('$1');"; }


classify_pair() {
  psql_conn -qAt -v schema="$SCHEMA" -v tbl="$1" -v col="$2" <<'SQL'
SELECT CASE
  WHEN n.nspname IS NULL OR c.relname IS NULL OR a.attnum IS NULL THEN 'missing'
  WHEN t.typcategory IN ('S', 'B', 'E') THEN 'values'
  WHEN t.typcategory = 'D' THEN 'timestamp'
  ELSE 'other'
END
FROM (SELECT :'schema'::name AS sn, :'tbl'::name AS tn) x
LEFT JOIN pg_class c ON c.relname = x.tn
LEFT JOIN pg_namespace n ON n.oid = c.relnamespace AND n.nspname = x.sn
LEFT JOIN pg_attribute a ON a.attrelid = c.oid AND a.attname = :'col' AND a.attnum > 0 AND NOT a.attisdropped
LEFT JOIN pg_type t ON t.oid = a.atttypid;
SQL
}

table_identity_subselect() {
  # Subselect autossuficiente que devolve o oid da tabela alvo.
  printf '(SELECT c.oid FROM pg_class c JOIN pg_namespace cn ON cn.oid = c.relnamespace WHERE cn.nspname = %s AND c.relname = %s)' \
    "$(q_text "$SCHEMA")" "$(q_text "$1")"
}

main() {
  : > "$TMP_DIR/pairs.txt"
  while IFS= read -r raw || [[ -n "$raw" ]]; do
    line=${raw%%\#*}
    line=${line//[[:space:]]/}
    [[ -z "$line" ]] && continue
    [[ "$line" =~ ^[a-z_][a-z_0-9]*\.[a-z_][a-z_0-9]*$ ]] ||
      fail "entrada inválida no config (esperado tabela.coluna): '$line'"
    printf '%s\n' "$line" >> "$TMP_DIR/pairs.txt"
  done < <(LC_ALL=C sort "$CONFIG_FILE")
  [[ -s "$TMP_DIR/pairs.txt" ]] || fail "config sem entradas válidas"

  BUNDLE_OPENED=0
  mkdir -m 0700 "$dest_abs"
  mkdir "$dest_abs/json" "$dest_abs/sql"
  chmod 700 "$dest_abs/json" "$dest_abs/sql"
  BUNDLE_OPENED=1

  local aggregate_doc="$dest_abs/sql/profile-aggregates.sql"
  {
    echo "-- Agregados gerados por tools/legacy-snapshot/profile-tables.sh; schema '$SCHEMA'."
    echo "-- Somente agregações e limites fixos; blocos autossuficientes; nada retorna linha completa."
    echo "BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;"
    echo "WITH blocks AS ("
  } > "$aggregate_doc"

  local first=1 emitted=0 pair tbl col category block
  while IFS= read -r pair; do
    tbl=${pair%%.*}
    col=${pair##*.}
    category=$(classify_pair "$tbl" "$col") || fail "falha ao classificar $pair"
    if [[ "$category" == "missing" ]]; then
      echo "aviso: ignorado (não encontrado): $pair" >&2
      continue
    fi

    local tblref colref oidsub
    tblref=$(q_table "$SCHEMA" "$tbl") || fail "falha ao citar tabela $tbl"
    colref=$(q_ident "$col") || fail "falha ao citar coluna $col"
    oidsub=$(table_identity_subselect "$tbl")

    block="  SELECT '$SCHEMA.$tbl.$col' AS k, jsonb_build_object("
    block+=" 'exact_rows', (SELECT count(*) FROM $tblref),"
    block+=" 'approx_rows', GREATEST(COALESCE((SELECT c.reltuples::bigint FROM pg_class c WHERE c.oid = $oidsub), 0), 0),"
    block+=" 'total_bytes', COALESCE(pg_total_relation_size($oidsub), 0),"
    block+=" 'null_count', (SELECT count(*) FROM $tblref WHERE $colref IS NULL),"
    block+=" 'null_rate', COALESCE(round((SELECT count(*) FROM $tblref WHERE $colref IS NULL)::numeric / NULLIF((SELECT count(*) FROM $tblref), 0), 4), 0)"

    if [[ "$category" == "values" ]]; then
      block+=", 'value_top', (SELECT COALESCE(jsonb_agg(jsonb_build_object('v', x.v, 'n', x.n) ORDER BY x.n DESC, x.v), '[]'::jsonb)"
      block+=" FROM (SELECT q.$colref AS v, count(*) AS n FROM $tblref q WHERE q.$colref IS NOT NULL GROUP BY q.$colref LIMIT 100) x)"
    elif [[ "$category" == "timestamp" ]]; then
      block+=", 'min_ts', (SELECT min($colref)::text FROM $tblref)"
      block+=", 'max_ts', (SELECT max($colref)::text FROM $tblref)"
    fi
    block+=') AS payload'

    if [[ $first -eq 1 ]]; then first=0; else echo "UNION ALL" >> "$aggregate_doc"; fi
    printf '%s\n' "$block" >> "$aggregate_doc"
    emitted=$((emitted + 1))
  done < "$TMP_DIR/pairs.txt"
  [[ $emitted -gt 0 ]] || fail "nenhuma entrada válida encontrada no schema '$SCHEMA'"

  {
    echo ")"
    echo "SELECT COALESCE(jsonb_object_agg(k, payload ORDER BY k), '{}'::jsonb)::text AS aggregates FROM blocks;"
    echo "COMMIT;"
  } >> "$aggregate_doc"
  cp "$aggregate_doc" "$TMP_DIR/run.sql"

  # Coleta via stdout (\o removido): resultado único ordenado em um só objeto.
  psql_conn -qAt -f "$TMP_DIR/run.sql" > "$TMP_DIR/out.json"
  [[ -s "$TMP_DIR/out.json" ]] || fail "execução não produziu agregados"
  mv "$TMP_DIR/out.json" "$dest_abs/json/profile-aggregates.json"
}

main "$@"

file_count=$(find "$dest_abs/sql" "$dest_abs/json" -type f | wc -l | tr -d ' ')
echo "perfil pronto: $dest_abs ($file_count arquivos)"
