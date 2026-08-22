# Campanha de correção da revisão da `main`

## Status

Desenho aprovado em 2026-08-22. Este documento autoriza planejamento e
decomposição; não transforma múltiplas lanes em um único PR.

## Objetivo

Corrigir primeiro falhas de autorização, isolamento por empresa, documentos,
IA e efeitos externos já presentes na `main`; depois reaproveitar as
simplificações válidas do PR #277 e reduzir código manual usando recursos
nativos ou dependências maduras já instaladas.

## Restrições

- Nunca escrever em `main`.
- Um comportamento observável por issue/PR vertical.
- Dividir acima de 25 arquivos, 800 linhas manuais ou subsistemas independentes.
- Não acessar `quality/private`.
- Não alterar provider/model durante a campanha.
- Não registrar PII, conteúdo de documento, prompt, token, segredo ou payload
  bruto em logs, testes ou Markdown.
- Preservar tenant, capability, audit, correlation e RLS em toda simplificação.

## Autoridades

| Decisão | Fonte autoritativa |
|---|---|
| Estado do código | Git e estado vivo do GitHub. |
| Isolamento e autorização | Contexto explícito de empresa, capability canônica e RLS. |
| Efeito financeiro | PostgreSQL + command/audit transacional; IA produz apenas candidato. |
| Documento | Bytes originais, checksum, scan, storage privado e estado durável. |
| Retry | Estado persistido e classificação explícita; nunca retry cego de efeito externo. |
| Revisão | Testes públicos, manifesto de verificação e revisão independente. |

## Disposição dos PRs existentes

### PR #278

Permanece documental e passa a conter:

- handoff validado;
- este desenho;
- decisões de decomposição e adiamentos.

### PR #277

Não será mesclado como unidade. Mudanças podem ser reaplicadas seletivamente:

- `AllowedUriPolicy`, após rebase e testes;
- transporte BFF compartilhado, em fatia frontend;
- hardening Supabase, com teste TOML e runbook;
- CI/Dependabot, após corrigir precondições e escopo.

Não reaproveitar:

- `StrictJsonFieldSet` como solução final;
- `AddOpenTelemetry` vazio;
- CodeQL tratado como controle ativo sem licença/billing/smoke run;
- commits inteiros que misturam backend, frontend, config e CI.

## Lane 1 — autoridade e Pagaí

### Invariantes

- A empresa do candidato e a empresa do comando são a mesma autoridade.
- Capability deve ser avaliada nessa empresa, nunca em outra membership.
- Role apagado ou relação cross-company não autoriza.
- Um documento gera no máximo um rascunho com resposta estável para replay ou
  conflito.
- Denial audit usa action estável e reason específico.

### Primeira entrega

Novo PR de hotfix com:

- migration que corrige os dois predicados de capability;
- filtro da função de leitura por `app.company_id`;
- leitura/checagem explícita do `company_id` retornado;
- tratamento da constraint de source document;
- classificação segura de falhas e auditoria action/reason;
- testes multi-membership, revogação, concorrência e falha de dependência.

## Lane 2 — fronteira de quarentena

### Ordem obrigatória

1. Limitar streaming de bytes.
2. Validar envelope/MIME sem parser complexo.
3. Obter veredito antimalware.
4. Validar magic bytes e dimensões.
5. Parsear/decode somente após `Clean`.
6. Reutilizar o resultado de uma única extração.

Se houver requisito de timeout rígido de parser síncrono, isolar parse em outro
processo/container; cancelamento cooperativo não interrompe `PdfDocument.Open`.

## Lane 3 — storage e fechamento durável

### Estado

O provider key continua determinístico e `upsert=false`.

- Objeto existente com mesmo tamanho, MIME e SHA-256 é replay válido.
- Objeto existente divergente é conflito de integridade terminal.
- Upload recebe receipt/stage durável antes de trabalho posterior.
- Falha ou revogação agenda cleanup/reconciliação.
- Candidato, documento, status e audit fecham em uma transação/função.
- Bucket provisioning é readiness/deploy, não trabalho por mensagem.

## Lane 4 — IA e providers

### Extração

- Política em system message ou `ChatOptions.Instructions`.
- Caption e transcrição como estrutura de dados explícita.
- Structured output do Microsoft.Extensions.AI quando suportado.
- Deserialização e validação semântica locais permanecem obrigatórias.
- Campos financeiros precisam de evidência/grounding e confirmação humana.

### Transporte

- Caller cancellation continua `OperationCanceledException`.
- Timeout interno e 429/5xx recebem classificação explícita.
- `SocketsHttpHandler.ConnectCallback` valida A/AAAA antes da conexão.
- Redirects e proxy possuem decisão explícita.
- Allowlist de host/porta é obrigatória fora de Development.

## Lane 5 — Meta, frontend e hosts

### WhatsApp

- Enviar somente para contato validado no momento do envio.
- Webhook lê no máximo 24 KiB + 1, usa rate limit e proxy confiável.
- Um único client Meta tipado produz `Delivered`, `Rejected` ou
  `Ambiguous`.
- Estado ambíguo é reconciliado; não dispara resend automático.

### Frontend

- Manter `fetch` e Zod.
- Transport compartilhado cuida apenas de invariantes BFF.
- TanStack Query substitui lifecycles manuais por módulo, com keys incluindo
  usuário e empresa e mutation `retry:false`.
- Pagaí mantém decimal como string/BigInt; nunca converter para `number`.
- Atendimento preserva o mesmo `requestId` no retry ambíguo e filtra cada
  ação pela capability correspondente.

### Hosts e CI

- JSON interno usa controles estritos nativos do .NET 10.
- Body reader compartilhado continua limitado; model binding não pode remover
  o teto de bytes.
- OTel só entra com signals, instrumentation, resource e exporter reais.
- Dependabot agrupa minor/patch; majors continuam isolados.
- CodeQL usa versão suportada, permissões mínimas e trigger de PR somente
  depois das precondições externas.

## Estimativa inicial

| Lane | Arquivos não-teste por PR | Linhas manuais por PR | Gerado separado |
|---|---:|---:|---|
| Autoridade e Pagaí | 7–10 | 450–700 | Migration/designer/snapshot quando necessário. |
| Quarentena | 6–10 | 400–650 | Nenhum previsto na primeira fatia. |
| Storage e atomicidade | 6–10 | 450–700 | Migration/snapshot se houver stage/receipt novo. |
| IA e providers | 5–8 | 350–600 | Nenhum previsto. |
| Meta, frontend e hosts | 3–10 por módulo | 100–600 | Lockfiles apenas quando dependência tiver uso real. |

Esses números são limites de planejamento, não autorização para juntar lanes.
Uma fatia será dividida novamente se o diff real ultrapassar os limites do
repositório ou introduzir responsabilidade independente.

## Política para dependências

Ordem de preferência:

1. Recurso nativo do runtime/framework.
2. Dependência já instalada e com consumidor real.
3. SDK oficial.
4. Pacote maduro, mantido e amplamente usado.
5. Código próprio estreito quando nenhuma opção atende o requisito.

Pacote novo precisa registrar:

- opções avaliadas;
- requisito não atendido pelas alternativas;
- licença, manutenção e vulnerabilidades;
- código/risco removido;
- custo operacional aceito.

## Estratégia de testes

### Antes da mudança

- Reproduzir o comportamento ou falha.
- Criar teste de caracterização quando o comportamento funcional for mantido.
- Para bug, executar red/green contra a correção.

### Matriz mínima

| Classe | Cobertura |
|---|---|
| Sucesso | Fluxo principal e variação válida relevante. |
| Validação/erro | Vazio, inválido, ausente, limite e dependência falha. |
| Acesso | Não autenticado, capability ausente e tenant incorreto. |
| Estado/exceções | Permitido, proibido, terminal, cancelamento e retorno. |
| Concorrência | Replay, conflito, corrida, retry e efeito ambíguo. |

### Gate

- Testes focados públicos durante implementação.
- `dotnet format`, Biome/TypeScript/build quando aplicável.
- `./scripts/verify-public.sh` uma vez antes do push comportamental.
- Playwright e Supabase Storage continuam gates separados quando a fatia tocar
  suas superfícies.

## Revisão e correção

- Um revisor independente por PR.
- Segundo revisor para autorização, tenant, arquivo, egress, IA ou integridade
  financeira.
- Consolidar achados em uma única rodada de correção.
- Após contratos, testes e documentação estabilizarem, congelar escopo.
- Refactors, cleanups ou capacidades não bloqueantes viram issue posterior.

## Sequência de entrega

1. Corrigir autoridade/tenant no stack já mesclado.
2. Corrigir quarentena, storage e atomicidade.
3. Corrigir IA/egress/cancelamento.
4. Corrigir Meta delivery/webhook/reconciliação.
5. Reaplicar BFF/URI/Supabase/CI válidos do #277 em PRs separados.
6. Migrar Pagaí, Atendimento, Notifications e shell por módulo.
7. Executar paginação e remoção de superfícies mortas por proprietário.

## Critérios de conclusão da campanha

- Nenhum finding de segurança aberto sem issue, decisão e target.
- PR #277 fechado ou explicitamente marcado como supersedido.
- PR #278 mesclado como documento canônico.
- Cada mudança comportamental possui issue, BDD e manifesto.
- Gates públicos e revisões independentes registrados por PR.
- Código manual substituído somente quando a alternativa reduz complexidade
  sem enfraquecer os invariantes.
- Adiamentos possuem condição objetiva de reavaliação.

## Adiamentos

- Harness de PDF hostil para calibrar impacto concreto do parser.
- Descoberta da origem dos UUIDs internos de candidatos.
- Backend/exporter de telemetria de produção.
- Reconciliação de invitation provider já pertencente ao follow-up #211B.
- Paginação e cleanups mecânicos após os hotfixes de autoridade e documento.
