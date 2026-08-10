# Basic Memory

O projeto Basic Memory vinx-erp-docs indexa somente docs/ do checkout atual.
O índice SQLite, o cache e a configuração local são descartáveis e nunca são
fonte de decisão. Git e Markdown versionado continuam canônicos.

## Ambiente isolado por checkout

Use Python e o executável bm instalados fora do repositório. Na raiz do
checkout, carregue scripts/basic-memory-env.sh antes de consultar o índice:

~~~bash
source scripts/basic-memory-env.sh
env -u BASIC_MEMORY_PROJECT_ROOT bm project add vinx-erp-docs "$PWD/docs" --default
source scripts/basic-memory-env.sh
bm reindex --full --project vinx-erp-docs
bm status --json --project vinx-erp-docs
scripts/basic-memory-doctor.sh
~~~

O script deriva caminhos de PWD e define:

- BASIC_MEMORY_CONFIG_DIR como .cache/basic-memory;
- BASIC_MEMORY_PROJECT_ROOT como docs/;
- atualização automática e frontmatter automático desativados;
- permalinks desativados para a verificação sem mutação;
- embeddings locais FastEmbed com bge-small-en-v1.5.

Não reutilize a configuração ou o projeto de outro checkout. Para um worktree,
use um slug diferente e o mesmo padrão de caminhos locais. Nunca execute
bm project remove --delete-notes nem bm reset --force.

## Busca e verificação

~~~bash
source scripts/basic-memory-env.sh
bm tool search-notes "company context authorization" --hybrid \
  --project vinx-erp-docs --page-size 5
sed -n '1,220p' docs/path/returned-by-search.md
bm status --json --project vinx-erp-docs
~~~

A busca é apenas navegação. Sempre abrir e confirmar o Markdown retornado e a
branch antes de agir. Se o MCP ou bm não estiver disponível, use rg e leitura
direta dos documentos.

## MCP read-only opcional

Registre um servidor por checkout, com nome e caminhos que apontem para a
mesma raiz pesquisada:

~~~text
bm_executable="$(command -v bm)"
test -x "$bm_executable"
codex mcp add vinx_memory_CHECKOUT_SLUG \
  --env BASIC_MEMORY_CONFIG_DIR=<repository-root>/.cache/basic-memory \
  --env BASIC_MEMORY_PROJECT_ROOT=<repository-root>/docs \
  --env BASIC_MEMORY_AUTO_UPDATE=false \
  --env BASIC_MEMORY_ENSURE_FRONTMATTER_ON_SYNC=false \
  --env BASIC_MEMORY_DISABLE_PERMALINKS=true \
  --env BASIC_MEMORY_SEMANTIC_EMBEDDING_PROVIDER=fastembed \
  --env BASIC_MEMORY_SEMANTIC_EMBEDDING_MODEL=bge-small-en-v1.5 \
  -- "$bm_executable" mcp --project vinx-erp-docs
~~~

Mantenha somente as ferramentas de leitura:

~~~toml
enabled = true
required = false
enabled_tools = [
  "list_memory_projects",
  "search_notes",
  "read_note",
  "build_context",
  "recent_activity",
]
~~~

Verifique o registro com codex mcp get --json
vinx_memory_CHECKOUT_SLUG. O nome do servidor pode usar um slug por checkout;
o projeto Basic Memory continua `vinx-erp-docs` porque isolamento vem da
configuração e da raiz locais. Não habilite ferramentas de escrita ou
gerenciamento de projeto para o fluxo normal.

## Reindexação e rollback

Depois de alterar docs/, execute bm reindex --full --project vinx-erp-docs e
confirme status. O script de doctor usa diretório temporário sob .cache/ e
remove somente aquele diretório ao terminar. Para desabilitar a integração,
remova o servidor MCP; não apague Markdown nem notas canônicas.
