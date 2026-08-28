#!/usr/bin/env bash
# Contrato: destino dentro da arvore do repositorio é recusado ANTES de
# qualquer captura, mesmo quando o script é chamado de um cwd estranho com
# caminho absoluto (a raiz é resolvida a partir do DIRETORIO DO SCRIPT).
set -u
# shellcheck source=lib-test.sh disable=SC1091
source "$(dirname "$0")/lib-test.sh"

repo_root=$(git -C "$TESTS_DIR" rev-parse --show-toplevel) || die "sem repositorio git"
dest_inside="$repo_root/.vinx-snapshot-guard-probe"

new_scope
make_fake_bins

# cwd estranho (fora do repositorio) NAO pode burlar a guarda; storage fora
# do cenario para provar que a recusa vem antes de qualquer captura.
cd "$SCOPE" || die "cd falhou"
output=$(TMPDIR="$SCOPE/tmp" PATH="$SCOPE/bin:$PATH" LEGACY_SNAPSHOT_CONN="$MOCK_CONN" \
  bash "$SCRIPT_UNDER_TEST" "$dest_inside" 2>&1)
rc=$?
cd - >/dev/null || die "cd de volta falhou"

assert_nonzero "$rc" "guarda aceitou destino dentro do repositorio (rc=$rc)"
assert_contains "$output" "árvore do repositório" "mensagem da guarda ausente"
assert_path_absent "$dest_inside" "guarda deixou caminho criado dentro do repositorio"
leftover=$(find "$repo_root" -maxdepth 1 -name '.vinx-snapshot*' -print 2>/dev/null)
assert_eq "" "$leftover" "staging criado dentro do repositorio apesar da guarda"
assert_no_temp_leftovers

echo "PASS repo_path_guard_refuses_dest_inside_repository_from_foreign_cwd"
