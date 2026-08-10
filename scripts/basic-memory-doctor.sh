#!/usr/bin/env bash
set -euo pipefail

script_path="${BASH_SOURCE[0]}"
script_directory="${script_path%/*}"
repository_root="$(cd -- "$script_directory/.." && pwd -P)"
current_root="$(pwd -P)"
if [[ "$current_root" != "$repository_root" ]]; then
  exit 1
fi

cache_root="$repository_root/.cache"
mkdir -p -- "$cache_root"
doctor_root="$(mktemp -d "$cache_root/basic-memory-doctor.XXXXXX")"
doctor_config="$doctor_root/config"
doctor_home="$doctor_root/home"
doctor_seed="$doctor_root/seed"
doctor_tmp="$doctor_root/tmp"
doctor_xdg_config="$doctor_root/xdg-config"
doctor_xdg_cache="$doctor_root/xdg-cache"

cleanup() {
  if [[ "$doctor_root" == "$cache_root"/basic-memory-doctor.* ]]; then
    rm -rf -- "$doctor_root"
  fi
}
trap cleanup EXIT

mkdir -p -- "$doctor_config" "$doctor_home" "$doctor_seed" "$doctor_tmp" \
  "$doctor_xdg_config" "$doctor_xdg_cache"

doctor_environment=(
  -u BASIC_MEMORY_PROJECT_ROOT
  -u OPENAI_API_KEY
  -u ANTHROPIC_API_KEY
  -u GEMINI_API_KEY
  "BASIC_MEMORY_CONFIG_DIR=$doctor_config"
  "BASIC_MEMORY_HOME=$doctor_root/default-project"
  "BASIC_MEMORY_AUTO_UPDATE=false"
  "BASIC_MEMORY_ENSURE_FRONTMATTER_ON_SYNC=false"
  "BASIC_MEMORY_DISABLE_PERMALINKS=true"
  "HOME=$doctor_home"
  "TMPDIR=$doctor_tmp"
  "XDG_CONFIG_HOME=$doctor_xdg_config"
  "XDG_CACHE_HOME=$doctor_xdg_cache"
)

env "${doctor_environment[@]}" bm project add basic-memory-doctor-seed \
  "$doctor_seed" --default
env "${doctor_environment[@]}" bm doctor
