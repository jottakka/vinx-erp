# ADR 0005: Projectmem como piloto de memória operacional de desenvolvimento

## Status

Piloto aprovado para tooling local. Não é dependência do produto nem decisão
de adoção permanente de memória para agentes.

## Contexto

O ERP precisa preservar entre sessões pequenos fatos de execução — issue
investigada, tentativa que falhou, correção confirmada e decisão operacional.
Basic Memory continua útil para buscar Markdown canônico, mas não deve ser
forçado a representar esse histórico operacional. Ao mesmo tempo, a memória
de desenvolvimento não pode atravessar o limite do produto, do `McpHost` ou
dos dados de empresas.

## Decisão

Avaliar `projectmem==0.2.0` como sidecar local opcional para memória
operacional do desenvolvimento. A instalação usa ambiente Python 3.12 isolado
e inicializa cada checkout com todos estes limites explícitos:

- sem hooks Git;
- sem herança de memória global;
- sem watcher de arquivos;
- sem backfill do histórico Git;
- sem ponte `CLAUDE.md`;
- sem detecção automática de stack;
- sem configuração MCP automática; e
- sem cache estrutural gerado.

O piloto usa somente entradas curtas de issue, tentativa, fix, decisão e nota.
É proibido registrar segredo, token, JWT, PII, conteúdo médico, prompt bruto,
conteúdo de arquivo, dado de cliente, material privado de verificação ou
artefato gerado.

Git e Markdown versionado continuam canônicos. O resumo e o mapa do projectmem
apenas aceleram a recuperação da história operacional e nunca substituem ADR,
issue, PR, teste, contrato, auditoria ou decisão revisada. O projectmem não
entra no runtime do ERP, em `ApiHost`, `McpHost`, `WorkerHost`, banco,
migrations, frontend ou imagem de produção. Mem0 permanece adiado até existir
uma necessidade medida de memória conversacional ou por usuário.

## Gate do piloto

O benchmark precisa registrar cold init, p50/p95 de leitura, custo de escrita
e baseline de `rg` na mesma máquina. A adoção futura exige também revisão de
retenção, privacidade, prompt injection, worktrees e rollback. Se o executável
não estiver disponível, `rg` e leitura direta de `docs/` continuam funcionando.

## Consequências

- Sessões locais podem recuperar rapidamente issues e tentativas recentes.
- A ausência de automação reduz risco de hooks compartilhados, captura
  acidental, herança entre projetos e instruções duplicadas.
- O log bruto ignorado não é auditoria durável; decisões importantes precisam
  permanecer em Markdown versionado e no issue tracker.
- O tooling de memória permanece separado do MCP de negócio e não recebe
  contexto de empresa, autenticação ou autorização do ERP.

## Rollback

Remover os arquivos `.projectmem` revisados e as entradas correspondentes do
`.gitignore`, remover `tests/projectmem-pilot.test.mjs`,
`scripts/projectmem-benchmark.mjs`, seus scripts de `package.json` e a linha do
piloto em `scripts/verify-public.sh`, desinstalar o ambiente Python externo e
manter Git, Markdown, Basic Memory e `rg`. Nenhum dado de produto, migration ou
registro do ERP precisa ser apagado.
