# Handoff — revisão validada da `main`

## Estado e autoridade

Este documento substitui o inventário estático original por uma revisão
validada contra código, testes públicos, contratos arquiteturais e o estado
vivo do GitHub em 2026-08-22.

- A revisão inicial usou `7f6d745`, depois ampliada até a `main`
  `df905b7`, que inclui os PRs #274, #275 e #276.
- O PR #278 é o inventário documental canônico desta campanha.
- O PR #277 está supersedido como unidade de entrega: mistura subsistemas,
  excede os limites de arquivos/linhas e falha no gate público completo.
- `quality/private` não foi listado, lido nem executado.
- Achado original é candidato, não verdade, até receber evidência e
  disposição explícita.

O desenho aprovado da correção está em
`docs/superpowers/specs/2026-08-22-main-review-correction-campaign-design.md`.

## Decisão sobre os PRs

| PR | Decisão |
|---|---|
| #278 | Manter documental, corrigido e limitado a handoff + desenho da campanha. |
| #277 | Não mesclar; reaproveitar somente mudanças selecionadas em PRs verticais novos. |
| #274–#276 | Já mesclados; corrigir por novos PRs, sem reescrever histórico da `main`. |

O #277 e o #278 foram criados como irmãos e adicionam versões incompatíveis
do mesmo handoff. A versão curta do #277 não deve substituir este inventário.

## Bloqueadores validados na `main`

1. **Role revogado continua autorizando documento.** As funções novas de
   Notifications não exigem `role.deleted_at IS NULL` nem a igualdade de
   empresa entre role, assignment e grant. O predicado canônico da Foundation
   contém essas garantias.
2. **Empresa autoritativa do candidato é ignorada.**
   `PaguaiDraftService` seleciona `company_id`, mas começa a ler na coluna
   seguinte. Um ator multiempresa pode combinar candidato de B com capability
   de A caso obtenha o identificador interno.
3. **Política da extração por IA não é enviada.**
   `InboundInvoiceExtractor.Instruction` é código morto; caption e
   transcrição controladas pelo remetente são a única mensagem do modelo.
4. **PDF é parseado antes do scan.** PdfPig abre bytes não confiáveis antes do
   veredito antimalware; o mesmo PDF é parseado novamente na extração.
5. **Storage/candidato precedem o recheck final.** Revogação durante o worker
   pode deixar objeto privado, disclosure para IA e candidato persistido,
   embora a mensagem termine como `capability_missing`.

## Riscos importantes validados

1. Download Meta usa buffer completo antes do teto real em resposta chunked ou
   sem `Content-Length`.
2. Upload determinístico de quarentena não reconcilia objeto já existente;
   retry após crash/falha posterior pode repetir 409 até DLQ e deixar órfão.
3. Constraint de documento único no Pagaí não entra na reconciliação de
   idempotência; perdedores podem receber 503 em vez de replay/409 estável.
4. Falhas do reader de candidato escapam do contrato seguro do serviço; a
   auditoria de negação usa reason code como `Action`.
5. Egress AI valida URI, mas não liga a validação DNS ao transporte; cancelamento
   do caller é embrulhado como indisponibilidade do modelo.

## Baseline de Notifications e frontend

- Envio WhatsApp outbound não exige contato `is_validated=true`.
- Webhook anônimo lê além do teto de 24 KiB antes de rejeitar e não possui
  rate limit por IP confiável.
- Os dois caminhos de envio Meta divergem na classificação de 2xx e não
  possuem estado durável `Ambiguous`/reconciliação.
- Atendimento pode gerar dois `requestId` para o mesmo intento e a interface
  não filtra ações por capability.
- A seleção de detalhes Pagaí tem corrida dentro da mesma empresa; resposta
  antiga pode substituir a seleção mais nova.

## Simplificações validadas

| Área | Escolha |
|---|---|
| JSON interno .NET | DTOs tipados + controles estritos do System.Text.Json; remover `StrictJsonFieldSet`. |
| Provider JSON | DTOs tolerantes a campos aditivos, com shape obrigatório validado localmente. |
| Frontend HTTP | Manter `fetch`; compartilhar somente invariantes BFF e manter resultados de domínio locais. |
| Estado frontend | Usar TanStack Query/RHF/Zod já instalados onde reduzem estado manual; não reescrever fluxos simples. |
| Durabilidade | Manter Wolverine/PostgreSQL; persistir reconciliação para efeitos externos ambíguos. |
| Storage/scan | Manter Supabase REST, ClamAV e PdfPig; corrigir ordem, limites, idempotência e cleanup. |
| Observabilidade | Configurar tracing/metrics/exporter reais ou remover `AddOpenTelemetry` vazio e suas alegações. |

Nenhuma dependência nova de JSON, HTTP, retries, dinheiro, paginação, máquina
de estados ou outbox foi justificada. Pacote novo exige uso atual, manutenção
ativa, licença compatível, ausência de vulnerabilidade conhecida e redução
material de código/risco.

## Achados originais rejeitados ou corrigidos

| Afirmação original | Disposição |
|---|---|
| Falha do flush pós-commit perde evento inbound | Rejeitada: o envelope Wolverine já está persistido; o flush acelera dispatch. |
| Logout 503 deixa cookie utilizável | Rejeitada: middleware expira o cookie local em `OnStarting`. |
| `/kanban` não deveria usar Atendimento | Rejeitada: esse compartilhamento pertence ao MVP aprovado de #228. |
| People deve ser substituído por TanStack Query | Recalibrada: já usa Query; complexidade restante protege identidade, autorização e commit ambíguo. |
| Paguai read transaction é desnecessária | Rejeitada: o contexto RLS usa `set_config(..., true)` e exige transação. |
| Status anônimo da policy protegida nega outros tenants | Rejeitada no grafo atual: rotas protegidas exigem autenticação e usam `sub`. |
| Signed media URL é SSRF comprovada | Recalibrada: hardening condicional; controle direto do destino pelo remetente não foi provado. |

## Sequência da campanha

1. **Hotfix de autoridade:** role/empresa canônicos, candidate company binding,
   teste multiempresa, constraint/replay e audit de negação.
2. **Quarentena:** streaming limitado, scan antes do parse/decode, magic bytes,
   uma única extração PDF e política explícita para imagens.
3. **Storage e atomicidade:** replay por checksum, mismatch terminal, receipt de
   upload, cleanup/reconciliação e fechamento candidato/documento/audit.
4. **IA/providers:** instrução real, structured output tipado, grounding,
   cancelamento, egress DNS e classificação transitória segura.
5. **WhatsApp/frontend/hosts:** contato validado, webhook, estado Ambiguous,
   BFF compartilhado, Atendimento/Pagaí/Notifications, CI e observabilidade.

Cada item acima é uma lane, não necessariamente um único PR. Dividir novamente
quando houver mais de 25 arquivos, mais de 800 linhas manuais ou subsistemas
independentes.

## Gates obrigatórios

- Issue antes do código, cobrindo sucesso, validação/erro, acesso,
  estado/exceções e concorrência/idempotência.
- Teste de caracterização antes de alterar comportamento funcional.
- Manifesto curto em `docs/verification/*`.
- Testes públicos focados durante desenvolvimento.
- `./scripts/verify-public.sh` uma vez antes do push de mudança comportamental.
- Uma revisão independente por PR; revisão extra somente para risco relevante
  de segurança ou integridade.
- Uma rodada consolidada de correção e scope freeze antes do gate final.

## Adiamentos explícitos

- Validar impacto concreto de PDF hostil contra PdfPig em harness descartável.
- Identificar a superfície que entrega o UUID interno do candidato aos callers.
- Escolher exporter/backend de OpenTelemetry antes de configurar sinais reais.
- Habilitar CodeQL somente após billing de Actions e GitHub Code Security
  permitirem smoke run nas duas linguagens.
- Tratar paginação, superfícies mortas e cleanups mecânicos após os bloqueadores
  de autoridade, documento e efeito externo.
