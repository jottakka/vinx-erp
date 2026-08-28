#!/usr/bin/env bash
# Contrato: INT/TERM no meio da captura descarta o bundle parcial e NAO deixa
# rastro — nem staging .vinx-snapshot.* no pai do destino, nem pgpass/bearer
# (segredos) no TMPDIR privado, nem bundle publicado no destino.
set -u
# shellcheck source=lib-test.sh disable=SC1091
source "$(dirname "$0")/lib-test.sh"

new_scope
make_fake_bins

python3 - "$SCOPE/fixture.json" <<'PY'
import json
import sys

fixture = {
    "service_key": "mock-service-key-ABC",
    "bucket_response": "ok",
    "buckets": [{"name": "docs"}],
    "objects": {"docs": ["root-a.txt", "reports/deep.txt"]},
}
with open(sys.argv[1], "w", encoding="utf-8") as fh:
    json.dump(fixture, fh)
PY
start_mock_server "$SCOPE/fixture.json"

dest=$SCOPE/out/bundle-signaled
snapshot_env
export FAKE_DUMP_SLEEP=3   # captura deterministica em andamento ao sinalizar

bash "$SCRIPT_UNDER_TEST" "$dest" > "$SCOPE/run.log" 2>&1 &
victim=$!

# Espera a captura estar em andamento (pgpass ja criado) e sinaliza TERM.
for _ in $(seq 1 100); do
  [[ $(find "$SCOPE/tmp" -maxdepth 1 -name 'vinx-pgpass.*' -print -quit 2>/dev/null) ]] && break
  sleep 0.05
done
kill -TERM "$victim" 2>/dev/null || die "processo alvo terminou antes do sinal"
wait "$victim"
rc=$?

assert_nonzero "$rc" "TERM nao derrubou a captura (rc=$rc)"
log=$(cat "$SCOPE/run.log")
assert_contains "$log" "captura interrompida" "mensagem de descarte ausente: $log"
assert_path_absent "$dest" "bundle parcial foi publicado apos TERM"
assert_no_temp_leftovers
pass "TERM durante a captura: sem bundle, sem staging, sem pgpass/bearer"

# --- Cenario 2: TERM na fase de STORAGE, com bearer ja criado ---------------
unset FAKE_DUMP_SLEEP
python3 - "$SCOPE/fixture-slow.json" <<'PY'
import json
import sys

fixture = json.load(open(sys.argv[1].replace("-slow", ""), encoding="utf-8"))
fixture["list_sleep"] = 3   # segura a listagem: captura presa com bearer vivo
with open(sys.argv[1], "w", encoding="utf-8") as fh:
    json.dump(fixture, fh)
PY
kill "$MOCK_PID" 2>/dev/null
wait "$MOCK_PID" 2>/dev/null
start_mock_server "$SCOPE/fixture-slow.json"

dest2=$SCOPE/out/bundle-signaled-storage
snapshot_env   # reexporta STORAGE_URL com a porta do mock reiniciado
bash "$SCRIPT_UNDER_TEST" "$dest2" > "$SCOPE/run2.log" 2>&1 &
victim=$!

# Espera o bearer do storage existir (fase de inventario em andamento) e sinaliza.
for _ in $(seq 1 200); do
  [[ -n $(find "$SCOPE/tmp" -maxdepth 1 -name 'vinx-hdr.*' -print -quit 2>/dev/null) ]] && break
  kill -0 "$victim" 2>/dev/null || die "captura terminou antes do bearer existir"
  sleep 0.05
done
kill -TERM "$victim" 2>/dev/null || die "processo alvo terminou antes do sinal (bearer)"
wait "$victim"
rc=$?

assert_nonzero "$rc" "TERM na fase de storage nao derrubou a captura (rc=$rc)"
assert_contains "$(cat "$SCOPE/run2.log")" "captura interrompida" \
  "mensagem de descarte ausente: $(cat "$SCOPE/run2.log")"
assert_path_absent "$dest2" "bundle parcial foi publicado apos TERM na fase de storage"
assert_no_temp_leftovers
pass "TERM com bearer vivo: bearer removido no teardown, sem bundle nem temporarios"

echo "PASS signal_cleanup_leaves_no_temp_or_bearer"
