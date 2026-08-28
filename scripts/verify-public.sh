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
