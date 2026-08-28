# Mapa de verificação: limpeza de simplificação da main (2026-08-28)

## Limite da evidência

Manifesto curto exigido pelo aceite da campanha: toda mudança comportamental
da onda mapeada ao seu teste regressivo RED/GREEN. Correções sem mudança de
comportamento (mock de teste WEB-03, alinhamento de expectativa/documento
FND-01 e blocos documentais DOC-01) não têm linha aqui. O log RED/GREEN por
bloco é evidência temporária da campanha; este arquivo persiste somente o
contrato durável entre mudança e teste.

| Bloco | Comportamento entregue | Teste regressivo | Contrato preservado |
|---|---|---|---|
| WEB-01 | Alias de rota `/kanban` removido do catálogo e do manifest; jornada autenticada visita Paguai, Atendimento e Pessoas. | `apps/web/src/shared/routing/app-paths.test.ts`; `apps/web/src/app/app-route-manifest.test.tsx`; `apps/web/e2e/app-shell-home.spec.ts` | `/demandas` segue consumindo `ViewKanbanOutlinedIcon`; protótipos DEV (`/prototipos/*`) intocados; heading real `Áreas disponíveis`. |
| AI-01 | Composição do ApiHost falha na partida (`InvalidOperationException`) para endpoint HTTPS loopback do perfil principal, mesmo com host na allowlist. | `tests/public/Vinx.Erp.Foundation.UnitTests/AgentRunCompositionTests.cs` — `Https_loopback_endpoint_fails_the_composition` | Reutiliza o validador canônico `ValidateForRuntimeCredential` do Worker, sem novo tipo compartilhado; sem provider permanece fail-closed `provider_not_configured`. |
| AI-02 | Chave JSON duplicada na raiz do payload estruturado reprova fechado. | `tests/public/Vinx.Erp.AI.UnitTests/Guardrails/AgentOutputContractTests.cs` — `Duplicate_required_field_fails_closed` | Motivo estável `structured_json_invalid`; campos desconhecidos/ausentes, JSON malformado, não-objeto e nulo seguem inalterados; conteúdo aninhado não é inspecionado. |
| WEB-02 | Proxy de desenvolvimento Vite cobre somente os subcaminhos BFF ativos de Agent, Atendimento, Demandas, Foundation, Notifications e Paguai (contrato operacional em `docs/runbooks/local-development.md`). | `apps/web/src/app/vite-api-proxy.test.ts` | Destino aceita apenas origem exata `http://127.0.0.1:<porta>`; rotas SPA (`/atendimento`, `/demandas`, `/paguai`) ficam fora de todo contexto; preview/produção sem proxy. |
| WEB-04 | Empresa com capability somente `read` não recebe ação de avanço; capability `create` não publica botão de criação sem fluxo. | `apps/web/src/features/demandas/demandas-page.test.tsx` — `hides advance actions from a company with read capability only`; `does not publish an inert create action` | Servidor permanece autoritatório para autenticação, empresa, capability, versão, idempotência e auditoria; a UI apenas projeta a permissão. |

## Linhas anteriores alinhadas

A mesma correção alinhou as linhas da jornada autenticada em
`frontend-workspace-shell-test-cases.md` (destinos sem Kanban) e do proxy
local em `browser-bff-transport-test-cases.md` (cobertura atual dos
subcaminhos BFF) ao comportamento entregue.
