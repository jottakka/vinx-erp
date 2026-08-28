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

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
execution_root=""
execution_project_id=""
execution_start_attempted=0

prepare_execution_project() {
  execution_root="$(mktemp -d "${TMPDIR:-/tmp}/vinx-storage-verifier.XXXXXX")"
  execution_project_id="vinx-storage-$(basename "$execution_root" | tr -cd '[:alnum:]' | tr '[:upper:]' '[:lower:]')"
  cp -R "$repository_root/supabase" "$execution_root/"

  local rewritten_config="$execution_root/config.toml"
  if ! awk -v project_id="$execution_project_id" '
    /^project_id = / {
      print "project_id = \"" project_id "\""
      found = 1
      next
    }
    { print }
    END { exit !found }
  ' "$execution_root/supabase/config.toml" > "$rewritten_config"; then
    printf '%s\n' 'Supabase Storage execution configuration could not be prepared.' >&2
    exit 1
  fi

  mv "$rewritten_config" "$execution_root/supabase/config.toml"
}

cleanup() {
  unset api_url service_role_key SUPABASE_STORAGE_URL SUPABASE_STORAGE_SERVICE_ROLE_KEY
  if [[ "$execution_start_attempted" == "1" ]]; then
    npx supabase --workdir "$execution_root" stop --project-id "$execution_project_id" >/dev/null 2>&1 || true
  fi
  if [[ -n "$execution_root" ]]; then
    rm -rf -- "$execution_root"
  fi
}

trap cleanup EXIT

select_docker_context
npm ci

# Fronteira de propriedade: se a stack local já está no ar, recusa sem tocar
# nela (status ambíguo ou falho também impede o start — nunca assume posse).
if npx supabase --workdir "$repository_root" status >/dev/null 2>&1; then
  printf '%s\n' 'A Supabase local stack is already running; this verifier refuses to take ownership of a pre-existing stack. Stop it or run the Storage category against it explicitly.' >&2
  exit 2
fi

prepare_execution_project

if npx supabase --workdir "$execution_root" status >/dev/null 2>&1; then
  printf '%s\n' 'Supabase Storage execution project is already running; this verifier refuses to take ownership of it.' >&2
  exit 2
fi

execution_start_attempted=1
if ! npx supabase --workdir "$execution_root" start >/dev/null 2>&1; then
  printf '%s\n' 'Supabase local startup failed; check Docker availability and context selection.' >&2
  exit 1
fi

api_url="$(npx supabase --workdir "$execution_root" status -o env 2>/dev/null | sed -n 's/^API_URL=//p' | tail -n 1)"
service_role_key="$(npx supabase --workdir "$execution_root" status -o env 2>/dev/null | sed -n 's/^SERVICE_ROLE_KEY=//p' | tail -n 1)"
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
