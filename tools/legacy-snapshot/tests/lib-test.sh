#!/usr/bin/env bash
# Biblioteca dos testes de contrato do shell de tools/legacy-snapshot.
# Cada teste sourced deste arquivo roda o snapshot.sh REAL com bins
# psql/pg_dump falsos no PATH e um mock local da Storage API — sem Postgres,
# sem Docker, sem rede externa.

TESTS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
SCRIPT_UNDER_TEST="$TESTS_DIR/../snapshot.sh"
MOCK_SERVICE_KEY='mock-service-key-ABC'
MOCK_DB_PASS='faketopsecret'
MOCK_CONN="postgresql://snapshot:${MOCK_DB_PASS}@127.0.0.1:1/legacydb"

pass() { echo "  ok: $*"; }

die() { echo "  FALHOU: $*" >&2; exit 1; }

assert_eq() { # esperado obtido mensagem
  [[ "$1" == "$2" ]] || die "$3 (esperado=[$1] obtido=[$2])"
}

assert_contains() { # palheiro agulha mensagem
  [[ "$1" == *"$2"* ]] || die "$3 — ausente: [$2] em [${1:0:400}]"
}

assert_path_absent() { # caminho mensagem
  [[ ! -e "$1" ]] || die "$2 — caminho nao deveria existir: $1"
}

assert_nonzero() { # status mensagem
  [[ "$1" -ne 0 ]] || die "$2 (status=$1, esperado != 0)"
}

assert_zero() { # status mensagem
  [[ "$1" -eq 0 ]] || die "$2 (status=$1, esperado 0)"
}

assert_no_secrets_in() { # diretorio-do-bundle
  local bundle=$1 hits
  hits=$(grep -Rl -e "$MOCK_SERVICE_KEY" -e "$MOCK_DB_PASS" "$bundle" 2>/dev/null || true)
  [[ -z "$hits" ]] || die "segredo vazado no bundle: $hits"
}

# Escopo isolado por teste: TMPDIR privado (pgpass/bearer/mktemp do script) e
# pai do destino fora da arvore do repositorio.
new_scope() {
  SCOPE=$(mktemp -d "${TMPDIR:-/tmp}/vinx-snapshot-test.XXXXXX")
  mkdir -p "$SCOPE/tmp" "$SCOPE/out"
  trap 'scope_teardown' EXIT
}

scope_teardown() {
  [[ -n "${MOCK_PID:-}" ]] && { kill "$MOCK_PID" 2>/dev/null; wait "$MOCK_PID" 2>/dev/null; }
  [[ -n "${SCOPE:-}" ]] && rm -rf "$SCOPE"
}

# Bins falsos: psql materializa os alvos \o da sessao consistente; pg_dump
# grava DDL sintetico, com portao (gate) e/ou atraso para orquestrar corridas.
make_fake_bins() {
  mkdir -p "$SCOPE/bin"
  cat > "$SCOPE/bin/psql" <<'FAKE_PSQL'
#!/usr/bin/env bash
# psql falso: interpreta apenas os alvos "\o <caminho>" do script recebido em -f.
set -u
sql_file=""
args=("$@")
for ((i = 0; i < ${#args[@]}; i++)); do
  if [[ "${args[$i]}" == "-f" ]]; then
    sql_file=${args[$((i + 1))]}
  fi
done
[[ -n "$sql_file" && -f "$sql_file" ]] || { echo "psql-falso: -f ausente" >&2; exit 1; }
while IFS= read -r line || [[ -n "$line" ]]; do
  case "$line" in
    '\o '*)
      target=${line#'\o '}
      case "$target" in
        *.counts.raw) printf 'clientes|3\nempresa|2\n' > "$target" ;;
        *.counts.gen) printf "SELECT 'clientes' AS t;\n" > "$target" ;;
        *.session-done) printf 'ok\n' > "$target" ;;
        *) printf '[]\n' > "$target" ;;
      esac
      ;;
  esac
done < "$sql_file"
exit 0
FAKE_PSQL
  cat > "$SCOPE/bin/pg_dump" <<'FAKE_DUMP'
#!/usr/bin/env bash
# pg_dump falso: espera o portao opcional, atrasa opcionalmente e grava DDL.
set -u
if [[ -n "${FAKE_DUMP_GATE:-}" ]]; then
  deadline=$((SECONDS + 90))
  while [[ ! -e "$FAKE_DUMP_GATE" && $SECONDS -lt $deadline ]]; do
    sleep 0.1
  done
fi
if [[ -n "${FAKE_DUMP_SLEEP:-}" ]]; then
  sleep "$FAKE_DUMP_SLEEP"
fi
out=""
prev=""
for arg in "$@"; do
  if [[ "$prev" == "--file" ]]; then out=$arg; fi
  prev=$arg
done
[[ -n "$out" ]] || { echo "pg_dump-falso: --file ausente" >&2; exit 1; }
printf -- '-- DDL sintetico do teste\n' > "$out"
FAKE_DUMP
  chmod +x "$SCOPE/bin/psql" "$SCOPE/bin/pg_dump"
}

# Sobe o mock da Storage API com a fixture; define MOCK_PORT/MOCK_PID/MOCK_LOG.
start_mock_server() { # caminho-da-fixture
  rm -f "$SCOPE/mock-port" "$SCOPE/mock-requests.log"
  MOCK_PORT_FILE="$SCOPE/mock-port" MOCK_LOG="$SCOPE/mock-requests.log" \
    python3 "$TESTS_DIR/mock-storage-server.py" "$1" &
  MOCK_PID=$!
  local _
  for _ in $(seq 1 200); do
    [[ -s "$SCOPE/mock-port" ]] && break
    kill -0 "$MOCK_PID" 2>/dev/null || die "mock da storage API morreu ao subir"
    sleep 0.05
  done
  [[ -s "$SCOPE/mock-port" ]] || die "mock da storage API nao reportou a porta"
  MOCK_PORT=$(cat "$SCOPE/mock-port")
  # shellcheck disable=SC2034  # consumido pelos testes que sourced esta lib
  MOCK_LOG="$SCOPE/mock-requests.log"
}

snapshot_env() { # comando... — exporta o ambiente de uma execucao do script
  export TMPDIR="$SCOPE/tmp"
  export PATH="$SCOPE/bin:$PATH"
  export LEGACY_SNAPSHOT_CONN="$MOCK_CONN"
  export LEGACY_SNAPSHOT_STORAGE_URL="http://127.0.0.1:$MOCK_PORT/storage/v1"
  export LEGACY_SNAPSHOT_SERVICE_KEY="$MOCK_SERVICE_KEY"
}

run_snapshot() { # destino — execucao foreground completa
  snapshot_env
  bash "$SCRIPT_UNDER_TEST" "$1"
}

assert_no_temp_leftovers() {
  local leftover
  leftover=$(find "$SCOPE/tmp" -maxdepth 1 \( -name 'vinx-pgpass.*' -o -name 'vinx-hdr.*' -o -name 'tmp.*' \) -print 2>/dev/null)
  [[ -z "$leftover" ]] || die "temporarios/segredos sobraram em TMPDIR privado: $leftover"
  leftover=$(find "$SCOPE/out" -maxdepth 1 -name '.vinx-snapshot.*' -print 2>/dev/null)
  [[ -z "$leftover" ]] || die "staging .vinx-snapshot.* sobrou no pai do destino: $leftover"
}
