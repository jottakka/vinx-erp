#!/usr/bin/env bash
# Runner dos testes de contrato do shell de tools/legacy-snapshot: cada caso
# executa o snapshot.sh REAL com bins falsos e mock local da Storage API
# (python3 http.server) — sem Postgres, sem Docker, sem rede externa.
# Uso: tools/legacy-snapshot/tests/run-tests.sh
set -u

TESTS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
LOG_DIR=$(mktemp -d "${TMPDIR:-/tmp}/vinx-snapshot-test-logs.XXXXXX")
trap 'rm -rf "$LOG_DIR"' EXIT

command -v python3 >/dev/null 2>&1 || { echo "erro: python3 é obrigatório" >&2; exit 1; }
command -v curl >/dev/null 2>&1 || { echo "erro: curl é obrigatório" >&2; exit 1; }

echo "== contrato shell de tools/legacy-snapshot =="
total=0
failed=0
for test_file in "$TESTS_DIR"/test-*.sh; do
  total=$((total + 1))
  name=$(basename "$test_file" .sh)
  log="$LOG_DIR/$name.log"
  if bash "$test_file" > "$log" 2>&1; then
    echo "PASS $name"
  else
    failed=$((failed + 1))
    echo "FAIL $name"
    sed 's/^/    /' "$log"
  fi
done

if (( failed > 0 )); then
  echo "== $failed de $total testes FALHARAM =="
  exit 1
fi
echo "== $total/$total testes de contrato shell passaram =="
