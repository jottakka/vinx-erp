#!/usr/bin/env bash
# Contrato de publicacao concorrente: duas execucoes disputando o MESMO
# destino — o perdedor falha limpo (claim atomico), o vencedor publica um
# bundle integro e NENHUM caminho alheio é apagado/sobreposto/aninhado.
set -u
# shellcheck source=lib-test.sh disable=SC1091
source "$(dirname "$0")/lib-test.sh"

new_scope
make_fake_bins

python3 - "$SCOPE/fixture.json" <<'PY'
import json
import sys

objects = ["root-a.txt", "reports/2026/deep.txt"]
objects += [f"s{i:04d}.txt" for i in range(1204)]
fixture = {
    "service_key": "mock-service-key-ABC",
    "bucket_response": "ok",
    "buckets": [{"name": "docs"}],
    "objects": {"docs": objects},
}
with open(sys.argv[1], "w", encoding="utf-8") as fh:
    json.dump(fixture, fh)
PY
start_mock_server "$SCOPE/fixture.json"

dest=$SCOPE/out/contested-bundle

# --- Cenario deterministico: A publica; B (ja em captura) perde o claim -----
snapshot_env
export FAKE_DUMP_GATE=$SCOPE/gate-a
bash "$SCRIPT_UNDER_TEST" "$dest" > "$SCOPE/run-a.log" 2>&1 &
run_a=$!

export FAKE_DUMP_GATE=$SCOPE/gate-b
FAKE_DUMP_GATE=$SCOPE/gate-b bash "$SCRIPT_UNDER_TEST" "$dest" > "$SCOPE/run-b.log" 2>&1 &
run_b=$!
unset FAKE_DUMP_GATE

# Ambos passaram pela checagem inicial de destino inexistente (portoes fechados
# seguram a captura ANTES da publicacao): a corrida é real.
wait_both_blocked() { # pid-a pid-b
  local pg_a pg_b
  for _ in $(seq 1 300); do
    pg_a=$(pgrep -P "$1" -f pg_dump 2>/dev/null || true)
    pg_b=$(pgrep -P "$2" -f pg_dump 2>/dev/null || true)
    [[ -n "$pg_a" && -n "$pg_b" ]] && return 0
    kill -0 "$1" 2>/dev/null || die "execucao $1 terminou antes de bloquear no portao"
    kill -0 "$2" 2>/dev/null || die "execucao $2 terminou antes de bloquear no portao"
    sleep 0.05
  done
  die "execucoes nao bloquearam no pg_dump falso (pgrep nao achou filhos)"
}
wait_both_blocked "$run_a" "$run_b"

touch "$SCOPE/gate-a"    # A conclui captura e publica
wait "$run_a"
rc_a=$?
assert_zero "$rc_a" "vencedor A falhou (rc=$rc_a): $(cat "$SCOPE/run-a.log")"
[[ -f "$dest/manifest.sha256" ]] || die "bundle do vencedor ausente: $(cat "$SCOPE/run-a.log")"

manifest_before=$(cat "$dest/manifest.sha256")

touch "$SCOPE/gate-b"    # B chega ao destino ja publicado: claim falha
wait "$run_b"
rc_b=$?
assert_nonzero "$rc_b" "perdedor B deveria falhar fechado (rc=$rc_b): $(cat "$SCOPE/run-b.log")"
assert_contains "$(cat "$SCOPE/run-b.log")" "destino já existe" \
  "perdedor B nao falhou por claim do destino: $(cat "$SCOPE/run-b.log")"

# Integridade do vencedor: manifesto identico e digests conferem.
assert_eq "$manifest_before" "$(cat "$dest/manifest.sha256")" "manifesto do vencedor foi alterado"
verify_manifest() { # bundle
  python3 - "$1" <<'PY' || die "digests do manifesto nao conferem: $1"
import hashlib
import os
import sys

root = sys.argv[1]
with open(os.path.join(root, "manifest.sha256"), encoding="utf-8") as fh:
    for line in fh:
        digest, rel = line.rstrip("\n").split("  ", 1)
        with open(os.path.join(root, rel), "rb") as target:
            actual = hashlib.sha256(target.read()).hexdigest()
        assert actual == digest, f"digest divergente: {rel}"
PY
}
verify_manifest "$dest"
nested=$(find "$dest" -name '.vinx-snapshot*' -print 2>/dev/null)
assert_eq "" "$nested" "bundle do perdedor aninhado dentro do destino do vencedor"
assert_no_secrets_in "$dest"
pass "perdedor falha limpo; bundle do vencedor integro (manifesto confere; sem aninhamento)"

assert_no_temp_leftovers
pass "nenhuma das execucoes deixou staging ou segredo para tras"

# --- Cenario simultaneo: ambos liberados juntos; exatamente um vence --------
dest2=$SCOPE/out/simultaneous-bundle
snapshot_env
FAKE_DUMP_GATE=$SCOPE/gate-c bash "$SCRIPT_UNDER_TEST" "$dest2" > "$SCOPE/run-c.log" 2>&1 &
run_c=$!
FAKE_DUMP_GATE=$SCOPE/gate-d bash "$SCRIPT_UNDER_TEST" "$dest2" > "$SCOPE/run-d.log" 2>&1 &
run_d=$!
wait_both_blocked "$run_c" "$run_d"
touch "$SCOPE/gate-c" "$SCOPE/gate-d"
wait "$run_c"; rc_c=$?
wait "$run_d"; rc_d=$?

winners=0
[[ $rc_c -eq 0 ]] && winners=$((winners + 1))
[[ $rc_d -eq 0 ]] && winners=$((winners + 1))
assert_eq 1 "$winners" "publicacao simultanea deve ter exatamente um vencedor (rc_c=$rc_c rc_d=$rc_d)"
verify_manifest "$dest2"
nested=$(find "$dest2" -name '.vinx-snapshot*' -print 2>/dev/null)
assert_eq "" "$nested" "aninhamento no cenario simultaneo"
assert_no_temp_leftovers
pass "cenario simultaneo: um vencedor integro, perdedor limpo"

echo "PASS competing_publishers_never_corrupt_or_nest_into_winner"
