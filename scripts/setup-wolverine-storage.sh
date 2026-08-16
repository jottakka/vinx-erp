#!/usr/bin/env bash
set -euo pipefail

: "${FOUNDATION_MIGRATOR_CONNECTION:?FOUNDATION_MIGRATOR_CONNECTION must be configured}"

FOUNDATION_RUNTIME_CONNECTION="$FOUNDATION_MIGRATOR_CONNECTION" \
  dotnet run --project src/Hosts/Vinx.Erp.ApiHost --no-launch-profile -- db-apply

psql "$FOUNDATION_MIGRATOR_CONNECTION" --set ON_ERROR_STOP=1 \
  --file infra/database/grant-wolverine-runtime.sql

FOUNDATION_RUNTIME_CONNECTION="$FOUNDATION_MIGRATOR_CONNECTION" \
  dotnet run --project src/Hosts/Vinx.Erp.ApiHost --no-launch-profile -- db-assert
