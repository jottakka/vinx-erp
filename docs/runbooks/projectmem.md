# Projectmem

Este runbook descreve o piloto local de `projectmem==0.2.0`. Git e Markdown
versionado são a fonte canônica; issues, PRs, testes, ADRs, contratos e
auditoria não são substituídos pelo resumo operacional. O projectmem não entra
no runtime do ERP, no `ApiHost`, `McpHost`, `WorkerHost`, banco, frontend,
migrations ou imagens de produção.

## Instalação isolada

Use um ambiente Python 3.12 fora do repositório. Não adicione projectmem a
`package.json`, `.csproj`, lockfile, solução .NET ou container:

```bash
uv venv --python 3.12 <cache-compartilhado>/projectmem-py312
uv pip install --python <cache-compartilhado>/projectmem-py312/bin/python projectmem==0.2.0
PJM_BIN=<cache-compartilhado>/projectmem-py312/bin/pjm
PJM_VERSION=0.2.0
```

O piloto foi verificado com Python 3.12.13. Não use Python 3.14 como evidência
de compatibilidade desta instalação.

## Inicialização segura

Execute na raiz do checkout. Estes flags fazem parte do contrato:

```bash
"$PJM_BIN" init \
  --no-hooks \
  --no-global \
  --no-watch \
  --no-backfill \
  --no-claude-md \
  --no-stack-detect \
  --no-mcp-config \
  --no-structure
```

Os arquivos revisáveis são `.projectmem/AI_INSTRUCTIONS.md`,
`.projectmem/PROJECT_MAP.md`, `.projectmem/config.toml`, `.projectmem/plan.md`,
`.projectmem/summary.md` e os snapshots curtos em `.projectmem/issues/*.md`.
`events.jsonl`, arquivos de watcher e cache estrutural permanecem ignorados e
descartáveis. Não habilite automações nem gere `CLAUDE.md` sem novo ADR e
revisão.

## Registro e consulta

Registre somente fatos operacionais curtos, sem dados sensíveis:

```bash
"$PJM_BIN" log "Investigar falha do contrato de readiness" --at "tests/..."
"$PJM_BIN" attempt "Comparei o contrato com o teste de integração" --failed --at "tests/..."
"$PJM_BIN" attempt "Atualizei o fixture e confirmei o teste focado" --worked --at "tests/..."
"$PJM_BIN" fix "O fixture agora respeita o contrato aprovado" --at "tests/..."
"$PJM_BIN" decision "Manter Markdown canônico e projectmem operacional" --at "docs/adr/..."
"$PJM_BIN" note "Executar o gate público antes de alterar contratos" --at "docs/..."
```

Use `show`, `brief`, `search` e `context` para recuperar contexto. Abra o
Markdown citado antes de tratar o resultado como requisito. Nunca registre
segredo, credencial, JWT, PII, conteúdo médico, prompt, conteúdo de arquivo,
dados de cliente ou material de `quality/private`.

A seção `Key files` gerada é heurística: tokens de prosa, como uma versão ou o
nome de um arquivo de ponte, podem aparecer ali mesmo sem serem caminhos reais.
Use `PROJECT_MAP.md` e as fontes canônicas abertas como autoridade de navegação.

## Teste de performance

Na raiz do repositório, execute:

```bash
PJM_BIN=<cache-compartilhado>/projectmem-py312/bin/pjm \
PJM_VERSION=0.2.0 \
PJM_ITERATIONS=20 \
node scripts/projectmem-benchmark.mjs
```

O resultado traz cold init, p50/p95 de `show`, `brief`, `context` e `search`,
latência de escrita em store sintético com 100 eventos e baseline de `rg`
sobre `docs/`. Compare execuções na mesma máquina. Projectmem e busca
semântica de documentação são cargas diferentes; não some os resultados nem
trate um como substituto automático do outro.

## Importação curada do GitHub

GitHub é o tracker vivo de status e prioridade, não um segundo banco do
projectmem. Use docs/runbooks/github-memory-import.md como manifesto dry-run
antes de qualquer registro. A allowlist é: repositório, número da issue ou PR,
título, estado, URL, updatedAt ou checked_at, resumo curto sem dado sensível e
caminho Markdown canônico.

Classifique antes de escrever:

- link quando o item já estiver representado por Markdown canônico;
- note para um gotcha observado ou fato de proveniência curto;
- log para uma investigação acionável neste checkout;
- decision somente quando ADR ou Markdown revisado confirmar a regra.

Faça deduplication usando github:repository:issue:number ou
github:repository:pr:number no manifesto e no texto do evento. Revalide issues
abertas antes de cada fatia; o snapshot importado nunca substitui o GitHub.
Nunca copie corpo, comentários, anexos, prompts, segredos, credenciais, PII,
conteúdo médico, quality/private ou o backlog inteiro. Depois de alterar
Markdown, reindexe o Basic Memory deste checkout. Sem a ferramenta, use rg e
leitura direta.

## Fallback e rollback

Se o executável não estiver disponível, use o runbook de Basic Memory ou `rg`
com leitura direta de Markdown. Esse fallback é obrigatório.

Para remover o piloto, retire apenas os arquivos `.projectmem` revisados e as
entradas correspondentes do `.gitignore`, remova `tests/projectmem-pilot.test.mjs`,
`scripts/projectmem-benchmark.mjs`, seus scripts de `package.json` e a linha do
piloto em `scripts/verify-public.sh`, depois remova o ambiente Python externo.
Não remova dados, migrations ou documentação canônica do ERP.
