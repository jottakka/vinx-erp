#!/usr/bin/env bash
set -euo pipefail

: "${FOUNDATION_MIGRATOR_CONNECTION:?FOUNDATION_MIGRATOR_CONNECTION must be configured}"

FOUNDATION_RUNTIME_CONNECTION="$FOUNDATION_MIGRATOR_CONNECTION" \
  dotnet run --project src/Hosts/Vinx.Erp.ApiHost --no-launch-profile -- db-apply

# Grants flow through Npgsql inside the deploy tool: neither the connection
# string nor its password ever reaches a process argument list, and no psql
# connection-string dialect conversion is needed.
dotnet run --project src/Tools/Vinx.Erp.PlatformBootstrap --no-launch-profile -- wolverine-grants

FOUNDATION_RUNTIME_CONNECTION="$FOUNDATION_MIGRATOR_CONNECTION" \
  dotnet run --project src/Hosts/Vinx.Erp.ApiHost --no-launch-profile -- db-assert
