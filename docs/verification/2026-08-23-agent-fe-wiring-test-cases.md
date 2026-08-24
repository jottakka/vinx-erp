# Casos de teste — Ligação do chat do agente ao POST /agent/runs (#53, fatia FE)

Data: 2026-08-23. Branch: `codex/fe-chat-media-context`. Plano:
`docs/superpowers/plans/2026-08-23-agent-chat-fe-wiring-and-media-context.md`
(rev. 2, Sol APPROVED). Escopo: FE somente (apps/web); nenhum contrato do
servidor é alterado.

O workspace permanença DEV-gated (`import.meta.env.DEV`); a liberação de
produção depende de seed `ai.read` + provider configurado (decisão do dono).

## Cinco classes BDD

| Classe BDD | Cenário | Resultado exigido | Evidência |
|---|---|---|---|
| sucesso | Envio vira 1 POST `/agent/runs` com journey fixa `read_only_summarize_v1`, `requestId` UUID novo, `companyId` da primeira empresa do bootstrap e antiforgery `X-CSRF-TOKEN`; `result` renderiza texto + fontes (`sources[].reference` como Chip). | Chamada única; corpo exato; resposta tipada na UI. | `agent.test.tsx` — "sends the message to POST /agent/runs and renders the typed result with sources" |
| sucesso | Sem empresa no bootstrap → corpo SEM `companyId` (o servidor responde `needs_clarification/company_required`; o FE não fabrica). | Body sem o campo; POST realizado. | `agent-client.test.ts` — "omits companyId from the body when no company is available" |
| validação | 400 tipado `validation_error` (campos anuláveis presentes) e 400 antiforgery `{"code":"invalid_request"}` mapeados separadamente. | Tipado vira run; envelope vira falha `antiforgery`. | `agent-client.test.ts` — "keeps nullable contract fields…" e "maps the antiforgery 400 envelope…" |
| validação | 409 tipado `conflict`; 401/429 sem parse de corpo; 503/504 envelope com código allow-list EXATA por status (503 `provider_not_configured\|agent_unavailable\|audit_unavailable`, 504 somente `agent_timeout`); corpo/estado fora do schema → `protocol` sem ecoar conteúdo. | Falhas tipadas por classe; nada de body cru em resultado. | `agent-client.test.ts` — 409 / 401-429 / 503-504 / "turns unknown bodies…" / "rejects envelope codes…" |
| validação | Combinações cruzadas status×HTTP (`200/conflict`, `409/result`), `correlationId` não-UUID e envelope com campo extra → `protocol`. reasonCode desconhecido → normalizado para `null` (nunca ecoado). | Rejeição determinística antes de qualquer UI. | `agent-client.test.ts` — "rejects crossed status/HTTP…" / "rejects typed bodies whose correlationId…" / "normalizes unknown reasonCodes…" |
| acesso | Antiforgery indisponível (401 no GET) → falha tipada e MUTAÇÃO NÃO OCORRE. | 1 chamada fetch (somente o GET); zero POST. | `agent-client.test.ts` — "stops before the mutation…" |
| estado | `requires_confirmation` → mensagem fixa "exige confirmação explícita", SEM card, botão ou proposta (estado opaco v1). | Nenhum botão de execução na árvore. | `agent.test.tsx` — "renders a fixed confirmation notice…" |
| estado | Reset durante voo aborta e descarta a resposta tardia (epoch); conversa volta ao estado vazio. | Resposta tardia não aparece; mensagem do usuário removida. | `agent.test.tsx` — "discards a late reply after resetting…" |
| estado | Unmount durante voo aborta o POST (signal) e descarta a resposta tardia sem atualização de estado. | Signal abortado após unmount; nenhum erro de act/setState. | `agent.test.tsx` — "aborts and discards a pending reply when the workspace unmounts" |
| estado | Troca de empresa ativa (bootstrap atualizado) com execução pendente → abort, conversa limpa, caption muda para a nova empresa; resposta tardia da empresa anterior nunca entra no contexto. | POST da empresa anterior abortado; caption `Beta`; mensagem antiga e resposta tardia ausentes. | `agent.test.tsx` — "aborts, clears the conversation and switches the caption…" |
| concorrência | Dupla submissão rápida com uma execução pendente → trava síncrona mantém 1 POST/1 `requestId`; envio seguinte aceito gera `requestId` distinto. | Exatamente 1 POST pendente; 2 POSTs no total; ids distintos. | `agent.test.tsx` — "blocks a second submission…" |
| concorrência | Rede falha / abort / 429 / 503 / 504 → resultado final do turno, NUNCA retry (abort de POST é potencialmente cometido). | Falhas tipadas sem novas chamadas. | `agent-client.test.ts` — "maps network errors and aborts…" |

## Regras cruzadas exercitadas

- UI honesta: dock/página mostram o nome da empresa efetiva (`Alfa · Consultas
  independentes`) e o aviso de consultas independentes sem memória de turnos.
- Nenhum body bruto, `Error.message`, prompt, mensagem enviada ou erro de Zod
  aparece em UI, log ou resultado do client — somente mensagens fixas e
  `reasonCode`/`code` allow-listed.
- E2E (`e2e/module-prototypes.spec.ts`, `test:e2e:prototypes`): cenário do
  agente intercepta `/agent/runs`, valida corpo (journey/requestId UUID/
  companyId/CSRF header), resposta tipada com fonte e AUSENCIA do botão de
  execução; varredura axe do dock sem violações.

## Suítes

- `npx vitest --run src/features/agent/` — 21/21 verdes (15 client + 6 workspace).
- `npx playwright test -c playwright.prototypes.config.ts -g "agent dock"` —
  1/1 verde.
- Gate completo: `./scripts/verify-public.sh` no HEAD exato (serializado;
  suíte Docker de integração excluída por instrução do responsável nesta
  janela de máquina).
