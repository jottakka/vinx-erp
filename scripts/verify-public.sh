#!/usr/bin/env bash
set -euo pipefail

npm ci
npm --workspace apps/web run lint
npm --workspace apps/web run format:check
npm --workspace apps/web run test -- --run
npm --workspace apps/web run build
npm run test:no-direct-supabase
dotnet tool restore
dotnet restore --use-lock-file
dotnet restore --locked-mode
dotnet format whitespace --verify-no-changes
dotnet build --no-restore
dotnet test --no-build --filter 'FullyQualifiedName!~PrivateQuality'
