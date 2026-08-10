# projectmem - projectmem-pilot-vinx

_Last updated: 2026-08-10_

## Project purpose
VINX ERP é um monólito modular .NET com hosts de API, MCP e worker, PostgreSQL
e uma aplicação web React. A base inicial concentra contratos Foundation,
identidade, autorização, auditoria, observabilidade e limites de provedor.

## Recent issues
- [OPEN] #0001 Não implementar o bootstrap autenticado do GitHub #110 isoladamente; verificar dependências de autorização, feature flags e OpenAPI. [docs/runbooks/github-memory-import.md] (open)

## Decisions
- Usar projectmem somente como memória operacional opcional do desenvolvimento; Git e Markdown versionado permanecem canônicos e o runtime/MCP do produto ficam fora do escopo. [docs/runbooks/projectmem.md]
- GitHub #127 é o snapshot atual da harness agentic: construir harness interna primeiro, separar contexto confiável de input externo e resolver policy antes dos argumentos da ferramenta. [docs/runbooks/github-memory-import.md]

## Notes
- Benchmark local Apple Silicon com 20 amostras: projectmem warm p95 ficou entre 42 e 76 ms, escrita em store sintético de 100 eventos mediu 54 ms p95, rg mediu 9 ms p95 e Basic Memory CLI híbrido mediu 2123 ms p95. [docs/runbooks/projectmem.md]
- GitHub #108 coordena as trilhas V1 e agentic; revalidar a issue para obter ordem atual, sem copiar o corpo. [docs/runbooks/github-memory-import.md]
- GitHub #21 ainda tem gate HTTP de admissão e revoke-all incompleto; usar o design de sessões como fonte local. [docs/runbooks/github-memory-import.md]
- GitHub #40 é capacidade separada de autenticação, catálogo e auditoria MCP; não substitui o núcleo da harness #127. [docs/runbooks/github-memory-import.md]

## Key files
- No key files logged yet.

## Open questions
- None logged yet.
