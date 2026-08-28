#!/usr/bin/env bash
set -euo pipefail

npm ci
npm run test:projectmem-pilot
npm --workspace apps/web run lint
npm --workspace apps/web run format:check
npm --workspace apps/web run test -- --run
npm --workspace apps/web run build
npm run test:no-direct-supabase
dotnet tool restore
dotnet restore --locked-mode
dotnet format analyzers --severity warn --verify-no-changes
dotnet format style --severity warn --verify-no-changes
dotnet format whitespace --verify-no-changes
dotnet build --no-restore
./scripts/verify-foundation-database.sh
./scripts/verify-ef-negative-guards.sh

while IFS= read -r public_test_project; do
  dotnet test "$public_test_project" --no-build --filter 'Category!=SupabaseStorage'
done < <(git ls-files 'tests/public/**/*.csproj' | sort)

# Tooling de migração legada é gate público do PR (Testcontainers/Docker no
# runner); sem isto a suíte compila no `dotnet build` e nunca roda no CI.
dotnet test tests/tools/Vinx.Erp.LegacyImport.IntegrationTests/Vinx.Erp.LegacyImport.IntegrationTests.csproj \
  --no-build --filter 'Category!=SupabaseStorage'

# Contrato executável dos scripts de captura legada: roda o snapshot.sh REAL
# com bins falsos e mock local da Storage API (python3) — sem Docker, sem rede;
# bash -n/shellcheck não cobrem status de processo, hierarquia da API nem
# corridas de publicação concorrente.
tools/legacy-snapshot/tests/run-tests.sh
