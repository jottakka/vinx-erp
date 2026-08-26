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
#   tabela.coluna
#
# Comportamento por entrada `tabela.coluna` encontrada no schema:
#   - nível de tabela: contagem exata, aproximada (reltuples) e tamanho total;
#   - nível de coluna: contagem de nulos e taxa de nulos;
#   - colunas texto/booleano/enum: TOP 100 valores distintos com contagem
#     (lista de valores distinta; usada também como group-count do status);
#   - colunas data/hora: min e max;
#   - outras categorias: apenas agregados de contagem — nenhum valor bruto sai;
#   - entradas ausentes no schema não aparecem na saída (compare com o config).
#
# Garantia anti-exfiltração: TODA consulta é agregação com limite fixo
# (LIMIT 100); nenhuma consulta deste profiler retorna linha completa.
# A SQL gerada é gravada na saída para auditoria.

set -euo pipefail

fail() { echo "erro: $*" >&2; exit 2; }

[[ $# -eq 2 ]] || { echo "uso: LEGACY_SNAPSHOT_CONN=... $0 <config.txt> <diretorio-de-saida>" >&2; exit 2; }
SCHEMA="${LEGACY_SNAPSHOT_SCHEMA:-public}"
CONFIG_FILE=$1
: "${LEGACY_SNAPSHOT_CONN:?LEGACY_SNAPSHOT_CONN é obrigatória}"
[[ -r "$CONFIG_FILE" ]] || fail "config não legível: $CONFIG_FILE"
command -v psql >/dev/null || fail "psql não encontrado"

dest_parent=$(dirname "$2")
dest_name=$(basename "$2")
mkdir -p "$dest_parent"
dest_abs="$(cd "$dest_parent" && pwd)/$dest_name"
if repo_root=$(git rev-parse --show-toplevel 2>/dev/null); then
  [[ "$dest_abs" == "$repo_root"/* ]] &&
    fail "diretório de saída não pode ficar dentro da árvore do repositório: $dest_abs"
fi
[[ -e "$dest_abs" ]] && fail "destino já existe: $dest_abs"

TMP_DIR=$(mktemp -d)
trap 'rm -rf "$TMP_DIR"' EXIT

psql_conn() { psql "$LEGACY_SNAPSHOT_CONN" -X -v ON_ERROR_STOP=1 "$@"; }

main() {
  mkdir -m 0700 "$dest_abs"
  mkdir "$dest_abs/json" "$dest_abs/sql"
  chmod 700 "$dest_abs/json" "$dest_abs/sql"

  : > "$TMP_DIR/pairs.txt"
  while IFS= read -r raw || [[ -n "$raw" ]]; do
    line=${raw%%\#*}
    line=${line//[[:space:]]/}
    [[ -z "$line" ]] && continue
    [[ "$line" =~ ^[a-z_][a-z_0-9]*\.[a-z_][a-z_0-9]*$ ]] ||
      fail "entrada inválida no config (esperado tabela.coluna): '$line'"
    printf '%s\n' "$line" >> "$TMP_DIR/pairs.txt"
  done < "$CONFIG_FILE"
  [[ -s "$TMP_DIR/pairs.txt" ]] || fail "config sem entradas válidas"

  # Template plano: um único nível de format(), comportamento condicionado por
  # typcategory dentro do próprio SQL; identificadores sempre citados por %I.
  cat > "$TMP_DIR/block.template.sql" <<'TPL'
SELECT format($f$  SELECT %L AS k, jsonb_build_object('approx_rows', GREATEST(c.reltuples, 0)::bigint, 'total_bytes', pg_total_relation_size(c.oid), 'exact_rows', (SELECT count(*) FROM %I.%I), 'null_count', (SELECT count(*) FROM %I.%I WHERE %I IS NULL), 'null_rate', round((SELECT count(*) FROM %I.%I WHERE %I IS NULL)::numeric / GREATEST((SELECT count(*) FROM %I.%I), 1), 4), 'value_top', CASE WHEN t.typcategory IN ('S','B','E') THEN (SELECT COALESCE(jsonb_agg(jsonb_build_object('v', x.v, 'n', x.n) ORDER BY x.n DESC, x.v), '[]'::jsonb) FROM (SELECT q0.%I AS v, count(*) AS n FROM %I.%I q0 WHERE q0.%I IS NOT NULL GROUP BY q0.%I LIMIT 100) x) ELSE NULL END, 'min_ts', CASE WHEN t.typcategory = 'D' THEN (SELECT min(q1.%I)::text FROM %I.%I q1) ELSE NULL END, 'max_ts', CASE WHEN t.typcategory = 'D' THEN (SELECT max(q1.%I)::text FROM %I.%I q1) ELSE NULL END) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace AND n.nspname = :'schema' JOIN pg_attribute a ON a.attrelid = c.oid AND a.attname = :'col' AND a.attnum > 0 AND NOT a.attisdropped JOIN pg_type t ON t.oid = a.atttypid;$f$,
  :'tbl' || '.' || :'col',
  :'schema', :'tbl',
  :'schema', :'tbl', :'col',
  :'schema', :'tbl', :'col', :'schema', :'tbl', :'col',
  :'col', :'schema', :'tbl', :'col', :'col',
  :'col', :'schema', :'tbl',
  :'col', :'schema', :'tbl');
TPL

  {
    echo "-- Agregados gerados por tools/legacy-snapshot/profile-tables.sh; schema '$SCHEMA'."
    echo "-- Somente agregações e limites fixos; nenhuma linha completa pode ser retornada."
    echo "BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;"
    echo "\\o \"$dest_abs/json/profile-aggregates.json\""
    echo "WITH blocks AS ("

    first=1
    emitted=0
    while IFS= read -r pair; do
      tbl=${pair%%.*}
      col=${pair##*.}
      # Se tabela/coluna sumiu entre configurar e rodar, o JOIN interno do bloco
      # não emite linha: a entrada simplesmente não aparece nos agregados.
      if [[ $first -eq 1 ]]; then first=0; else echo "UNION ALL"; fi
      psql_conn -qAt -v schema="$SCHEMA" -v tbl="$tbl" -v col="$col" \
        -f "$TMP_DIR/block.template.sql" || fail "falha ao gerar bloco $tbl.$col"
      emitted=$((emitted + 1))
    done < "$TMP_DIR/pairs.txt"

    [[ $emitted -gt 0 ]] || fail "nenhum bloco gerado"
    echo ")"
    echo "SELECT COALESCE(jsonb_object_agg(k, payload ORDER BY k), '{}'::jsonb)::text FROM blocks;"
    echo "COMMIT;"
  } > "$dest_abs/sql/profile-aggregates.sql"
  cp "$dest_abs/sql/profile-aggregates.sql" "$TMP_DIR/run.sql"

  psql_conn -qAt -v schema="$SCHEMA" -f "$TMP_DIR/run.sql" > "$TMP_DIR/out.json"
  [[ -s "$TMP_DIR/out.json" ]] || fail "execução não produziu agregados"
  mv "$TMP_DIR/out.json" "$dest_abs/json/profile-aggregates.json"
}

if ! main "$@"; then
  rm -rf "$dest_abs"
  fail "profiler interrompido; saída parcial descartada"
fi

file_count=$(find "$dest_abs/sql" "$dest_abs/json" -type f | wc -l | tr -d ' ')
echo "perfil pronto: $dest_abs ($file_count arquivos)"
