#!/usr/bin/env bash
set -euo pipefail

select_docker_context() {
  if [[ -n "${DOCKER_CONTEXT:-}" ]]; then
    return
  fi

  if [[ "$(uname -s)" == "Darwin" ]] && docker context inspect desktop-linux >/dev/null 2>&1; then
    export DOCKER_CONTEXT=desktop-linux
  fi
}

cleanup() {
  unset api_url service_role_key SUPABASE_STORAGE_URL SUPABASE_STORAGE_SERVICE_ROLE_KEY
  npx supabase stop >/dev/null 2>&1 || true
}

trap cleanup EXIT

select_docker_context
npm ci
if ! npx supabase start >/dev/null 2>&1; then
  printf '%s\n' 'Supabase local startup failed; check Docker availability and context selection.' >&2
  exit 1
fi

api_url="$(npx supabase status -o env 2>/dev/null | sed -n 's/^API_URL=//p' | tail -n 1)"
service_role_key="$(npx supabase status -o env 2>/dev/null | sed -n 's/^SERVICE_ROLE_KEY=//p' | tail -n 1)"
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
