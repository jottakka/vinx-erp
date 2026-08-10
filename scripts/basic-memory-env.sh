#!/usr/bin/env bash

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
  printf '%s\n' 'Source scripts/basic-memory-env.sh from the repository root.' >&2
  exit 1
fi

script_path="${BASH_SOURCE[0]}"
script_directory="${script_path%/*}"
repository_root="$(cd -- "$script_directory/.." && pwd -P)"
current_root="$(pwd -P)"
if [[ "$current_root" != "$repository_root" ]]; then
  printf 'Source this script from repository root: %s\n' "$repository_root" >&2
  return 1
fi

export BASIC_MEMORY_CONFIG_DIR="$repository_root/.cache/basic-memory"
export BASIC_MEMORY_PROJECT_ROOT="$repository_root/docs"
export BASIC_MEMORY_AUTO_UPDATE=false
export BASIC_MEMORY_ENSURE_FRONTMATTER_ON_SYNC=false
export BASIC_MEMORY_DISABLE_PERMALINKS=true
export BASIC_MEMORY_SEMANTIC_EMBEDDING_PROVIDER=fastembed
export BASIC_MEMORY_SEMANTIC_EMBEDDING_MODEL=bge-small-en-v1.5
