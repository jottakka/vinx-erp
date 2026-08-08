#!/usr/bin/env bash
set -euo pipefail

cleanup() {
  unset SUPABASE_STORAGE_URL SUPABASE_STORAGE_SERVICE_ROLE_KEY
  DOCKER_CONTEXT=desktop-linux npx supabase stop >/dev/null 2>&1 || true
}

trap cleanup EXIT

npm ci
DOCKER_CONTEXT=desktop-linux npx supabase start >/dev/null 2>&1

supabase_status="$(DOCKER_CONTEXT=desktop-linux npx supabase status -o env 2>/dev/null)"
api_url="$(printf '%s\n' "$supabase_status" | sed -n 's/^API_URL=//p' | tail -n 1)"
service_role_key="$(printf '%s\n' "$supabase_status" | sed -n 's/^SERVICE_ROLE_KEY=//p' | tail -n 1)"
api_url="${api_url#\"}"
api_url="${api_url%\"}"
service_role_key="${service_role_key#\"}"
service_role_key="${service_role_key%\"}"

if [[ -z "$api_url" || -z "$service_role_key" ]]; then
  printf '%s\n' 'Supabase Storage configuration was not available.' >&2
  exit 1
fi

export SUPABASE_STORAGE_URL="$api_url"
export SUPABASE_STORAGE_SERVICE_ROLE_KEY="$service_role_key"
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests --filter 'Category=SupabaseStorage'
