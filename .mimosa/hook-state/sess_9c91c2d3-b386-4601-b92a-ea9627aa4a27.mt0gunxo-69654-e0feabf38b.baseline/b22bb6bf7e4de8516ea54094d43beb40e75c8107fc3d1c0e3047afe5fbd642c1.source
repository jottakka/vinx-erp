#!/usr/bin/env bash
set -euo pipefail

readonly container_name="vinx-foundation-verify-$$"
readonly admin_password="$(openssl rand -hex 32)"
readonly migrator_password="$(openssl rand -hex 32)"
temporary_directory=""

cleanup() {
  if [[ -n "$temporary_directory" ]]; then
    rm -rf "$temporary_directory"
  fi

  docker rm --force "$container_name" >/dev/null 2>&1 || true
}
trap cleanup EXIT

temporary_directory="$(mktemp -d)"
readonly normalized_snapshot="$temporary_directory/foundation-schema.snapshot.sql"

echo "Starting disposable Foundation PostgreSQL database."
docker run --detach --rm --name "$container_name" \
  --publish 127.0.0.1::5432 \
  --env POSTGRES_PASSWORD="$admin_password" \
  postgres:17.9-alpine >/dev/null

for _ in {1..60}; do
  if docker exec "$container_name" pg_isready --quiet --username postgres --dbname postgres; then
    break
  fi

  sleep 1
done

if ! docker exec "$container_name" pg_isready --quiet --username postgres --dbname postgres; then
  echo "Disposable Foundation PostgreSQL database did not become ready." >&2
  exit 1
fi

docker exec --env PGPASSWORD="$admin_password" --interactive "$container_name" \
  psql --quiet --set ON_ERROR_STOP=1 --username postgres --dbname postgres \
  < infra/database/bootstrap-foundation-roles.sql

printf "ALTER ROLE vinx_migrator PASSWORD '%s';\n" "$migrator_password" \
  | docker exec --env PGPASSWORD="$admin_password" --interactive "$container_name" \
    psql --quiet --set ON_ERROR_STOP=1 --username postgres --dbname postgres

host_port="$(docker port "$container_name" 5432/tcp | awk -F: 'NR == 1 { print $NF }')"
if [[ -z "$host_port" ]]; then
  echo "Disposable Foundation PostgreSQL database did not expose a port." >&2
  exit 1
fi

migrator_connection="Host=127.0.0.1;Port=$host_port;Database=postgres;Username=vinx_migrator;Password=$migrator_password"

echo "Checking the Foundation EF model and migration history."
FOUNDATION_MIGRATOR_CONNECTION="$migrator_connection" \
  dotnet ef migrations has-pending-model-changes \
    --project src/Foundation/Vinx.Erp.Foundation \
    --startup-project src/Hosts/Vinx.Erp.ApiHost
FOUNDATION_MIGRATOR_CONNECTION="$migrator_connection" \
  dotnet ef database update \
    --project src/Foundation/Vinx.Erp.Foundation \
    --startup-project src/Hosts/Vinx.Erp.ApiHost

docker exec --env PGPASSWORD="$admin_password" "$container_name" \
  pg_dump --schema-only --no-owner --no-privileges \
    --schema=foundation --schema=audit --username postgres --dbname postgres \
  | sed \
      -e '/^-- PostgreSQL database dump$/d' \
      -e '/^-- Dumped from database version /d' \
      -e '/^-- Dumped by pg_dump version /d' \
      -e '/^\\restrict /d' \
      -e '/^\\unrestrict /d' \
      -e '/^--$/d' \
    | awk 'NF { last = NR } { lines[NR] = $0 } END { for (line = 1; line <= last; line++) print lines[line] }' \
    > "$normalized_snapshot"

diff -u docs/database/foundation-schema.snapshot.sql "$normalized_snapshot"
