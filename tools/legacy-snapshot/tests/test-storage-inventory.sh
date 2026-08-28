#!/usr/bin/env bash
# Contratos do inventario de Storage:
#  1. objeto ANINHADO atras de pastas (reports/2026/q1/deep.txt), com paginacao
#     multipla no nivel raiz (>1000 objetos), entra no manifesto — a listagem
#     da API é hierarquica e o inventario percorre os prefixos de pasta.
#  2. JSON de buckets MALFORMADO aborta o bundle (antes: substituicao de
#     processo engolia o erro e o bundle finalizava sem inventario).
#  3. JSON de buckets fora de forma (objeto, nao lista) também aborta.
# Ainda prova: bearer do storage removido no fim e nenhum segredo no bundle.
set -u
# shellcheck source=lib-test.sh disable=SC1091
source "$(dirname "$0")/lib-test.sh"

new_scope
make_fake_bins

# Fixture: 1204 objetos sinteticos no raiz (forca 2 paginas de 1000) +
# relatorio aninhado a tres niveis de pasta + arquivo raiz.
python3 - "$SCOPE/fixture.json" <<'PY'
import json
import sys

objects = ["root-a.txt", "reports/2026/q1/deep.txt"]
objects += [f"s{i:04d}.txt" for i in range(1204)]
fixture = {
    "service_key": "mock-service-key-ABC",
    "bucket_response": "ok",
    "buckets": [{"name": "docs"}, {"name": "empty-bucket"}],
    "objects": {"docs": objects, "empty-bucket": []},
}
with open(sys.argv[1], "w", encoding="utf-8") as fh:
    json.dump(fixture, fh)
PY

start_mock_server "$SCOPE/fixture.json"
dest=$SCOPE/out/legacy-bundle

# --- Caso 1: inventario hierarquico completo com paginacao multipla --------
output=$(run_snapshot "$dest" 2>&1)
rc=$?
assert_zero "$rc" "captura falhou (rc=$rc): $output"
[[ -f "$dest/manifest.sha256" ]] || die "bundle nao publicado: $output"

inventory=$dest/json/storage_objects_docs.json
[[ -f "$inventory" ]] || die "inventario do bucket docs ausente no bundle"

python3 - "$inventory" <<'PY' || die "inventario hierarquico incorreto (ver log acima)"
import json
import sys

items = json.load(open(sys.argv[1], encoding="utf-8"))
names = {item["name"]: item for item in items}
deep = names.get("reports/2026/q1/deep.txt")
assert deep is not None and deep.get("id"), "objeto aninhado ausente/sem id"
folders = [n for n, item in names.items() if item.get("id") is None]
for folder in ("reports", "reports/2026", "reports/2026/q1"):
    assert folder in folders, f"placeholder de pasta ausente: {folder}"
objects = [n for n, item in names.items() if item.get("id") is not None]
assert len(objects) == 1206, f"objetos capturados: {len(objects)} (esperado 1206)"
assert list(names) == sorted(names), "manifesto nao esta ordenado por nome"
PY
pass "objeto aninhado reports/2026/q1/deep.txt capturado via travessia de pastas"

grep -q "prefix='' offset=1000" "$MOCK_LOG" ||
  die "paginacao multipla nao ocorreu no nivel raiz (log sem offset=1000): $(cat "$MOCK_LOG")"
grep -q "prefix='reports/'" "$MOCK_LOG" ||
  die "listagem recursiva de pasta nao ocorreu: $(cat "$MOCK_LOG")"
pass "raiz paginou alem de 1000 objetos e pastas foram listadas recursivamente"

# Bearer/pgpass removidos no teardown; nenhum segredo no bundle publicado.
assert_no_temp_leftovers
assert_no_secrets_in "$dest"
pass "bearer e pgpass removidos; nenhum segredo no bundle"

# --- Casos 2 e 3: bucket JSON invalido aborta o bundle inteiro --------------
for mode in garbage notalist; do
  kill "$MOCK_PID" 2>/dev/null
  wait "$MOCK_PID" 2>/dev/null
  python3 - "$SCOPE/fixture-$mode.json" <<PY
import json
import sys

fixture = json.load(open("$SCOPE/fixture.json", encoding="utf-8"))
fixture["bucket_response"] = "$mode"
with open(sys.argv[1], "w", encoding="utf-8") as fh:
    json.dump(fixture, fh)
PY
  start_mock_server "$SCOPE/fixture-$mode.json"
  bad_dest=$SCOPE/out/bundle-$mode
  output=$(run_snapshot "$bad_dest" 2>&1)
  rc=$?
  assert_nonzero "$rc" "resposta de buckets $mode nao abortou (rc=$rc): $output"
  assert_contains "$output" "resposta de buckets invalida" \
    "abort veio de outra causa, nao do parse dos buckets ($mode): $output"
  assert_path_absent "$bad_dest" "bundle finalizado apesar de buckets $mode"
  assert_no_temp_leftovers
  pass "buckets $mode aborta sem publicar bundle nem deixar temporarios"
done

echo "PASS nested_multipage_inventory_captured_and_malformed_bucket_json_aborts"
