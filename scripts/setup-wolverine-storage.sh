#!/usr/bin/env bash
set -euo pipefail

: "${FOUNDATION_MIGRATOR_CONNECTION:?FOUNDATION_MIGRATOR_CONNECTION must be configured}"

FOUNDATION_RUNTIME_CONNECTION="$FOUNDATION_MIGRATOR_CONNECTION" \
  dotnet run --project src/Hosts/Vinx.Erp.ApiHost --no-launch-profile -- db-apply

# Keep the migration credential out of process arguments: extract the password
# into PGPASSWORD and strip it from the connection string passed to psql.
migrator_password="$(
  printf '%s' "$FOUNDATION_MIGRATOR_CONNECTION" | sed -n -E 's/^.*[Pp]assword=([^;]*).*/\1/p'
)"
migrator_connection_without_password="$(
  printf '%s' "$FOUNDATION_MIGRATOR_CONNECTION" | sed -E 's/[Pp]assword=[^;]*;?//'
)"

if [[ -n "$migrator_password" ]]; then
  export PGPASSWORD="$migrator_password"
fi
psql "$migrator_connection_without_password" --set ON_ERROR_STOP=1 \
  --file infra/database/grant-wolverine-runtime.sql

FOUNDATION_RUNTIME_CONNECTION="$FOUNDATION_MIGRATOR_CONNECTION" \
  dotnet run --project src/Hosts/Vinx.Erp.ApiHost --no-launch-profile -- db-assert
