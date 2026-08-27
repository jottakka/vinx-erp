# #335 — Agente real multicanal: casos e limites de verificação

## Mapa BDD → testes públicos

| Classe BDD | Testes públicos exatos |
| --- | --- |
| Sucesso | `AgentEndpointTests` (`tests/public/Vinx.Erp.Foundation.IntegrationTests/AgentEndpointTests.cs`); `InboundWhatsAppAgentAdapterTests` (`tests/public/Vinx.Erp.Foundation.IntegrationTests/InboundWhatsAppAgentAdapterTests.cs`); `TelegramAgentAdapterTests` (`tests/public/Vinx.Erp.Foundation.IntegrationTests/TelegramAgentAdapterTests.cs`) |
| Validação e erros | `AgentProviderCredentialTests` (`tests/public/Vinx.Erp.AI.UnitTests/Providers/OpenAiCompatible/AgentProviderCredentialTests.cs`); `PaguaiDocumentProposalParserTests` (`tests/public/Vinx.Erp.AI.UnitTests/Guardrails/PaguaiDocumentProposalParserTests.cs`) |
| Acesso | `AgentEndpointTests` (`tests/public/Vinx.Erp.Foundation.IntegrationTests/AgentEndpointTests.cs`); `InboundWhatsAppAgentAdapterTests` (`tests/public/Vinx.Erp.Foundation.IntegrationTests/InboundWhatsAppAgentAdapterTests.cs`); `TelegramAgentAdapterTests` (`tests/public/Vinx.Erp.Foundation.IntegrationTests/TelegramAgentAdapterTests.cs`) |
| Estado e exceções | `AgentExecutionLedgerTests` (`tests/public/Vinx.Erp.Foundation.UnitTests/AgentExecutionLedgerTests.cs`); `AgentExecutionLedgerMigrationTests` (`tests/public/Vinx.Erp.Foundation.IntegrationTests/AgentExecutionLedgerMigrationTests.cs`); `PaguaiProposalEndpointTests` (`tests/public/Vinx.Erp.Foundation.IntegrationTests/PaguaiProposalEndpointTests.cs`) |
| Concorrência e ambiguidade externa | `AgentEndpointTests` (`tests/public/Vinx.Erp.Foundation.IntegrationTests/AgentEndpointTests.cs`); `PaguaiProposalEndpointTests` (`tests/public/Vinx.Erp.Foundation.IntegrationTests/PaguaiProposalEndpointTests.cs`); `TelegramAgentAdapterTests` (`tests/public/Vinx.Erp.Foundation.IntegrationTests/TelegramAgentAdapterTests.cs`) |

## Evidência e pendências

Há evidência focada por tarefa para os contratos acima. O rerun consolidado de
migrations/adapters chegou a 24/25; a única asserção antiforgery desatualizada
foi corrigida depois, com compilação limpa. A rodada final de cinco correções
teve 16/16 testes de integração, 74/74 Foundation Unit, 56/56 AI Unit, 30/30
Web Pagaí e builds .NET/Web verdes. A revisão Sol completa encontrou cinco
findings importantes; a re-revisão confirmou todos corrigidos, sem novo finding
crítico/importante.

O único `verify-public.sh` local saiu `1` no lint Web, antes das suítes amplas:
dois bloqueios herdados da `main` e uma linha de formato desta branch. O rebase
absorveu a correção Atendimento; as duas linhas restantes foram corrigidas e o
Biome focado ficou verde, sem repetir o gate. CI permanece pendente.

O smoke real autorizado fez exatamente uma chamada de texto e uma de visão com
conteúdo sintético. Ambas falharam de forma segura, sem retry: texto em cerca de
695 ms e visão em cerca de 655 ms. Prompt, resposta, chave, headers e bytes não
foram registrados; portanto a evidência não distingue credencial/conta/modelo/
compatibilidade de API. Não houve envio por WhatsApp ou Telegram.

## Escopo congelado

- 153 arquivos no total: 94 não teste e 59 de teste.
- Alterações manuais: +13.414/-4.072 linhas (17.486 de churn).
- Gerados: 16 arquivos, +8.345/-10 linhas (8.355 de churn).

A entrega única é a exceção aprovada para a issue #335: o mesmo recorte reúne
núcleo, adapters, persistência, proposta e prova, sem expandir capacidade fora
desse contrato.

## Limites duráveis

O núcleo é agnóstico de provider e fala somente OpenAI-compatible. A chave
existe somente no ambiente do processo servidor como
`VINX_AGENT_PROVIDER_API_KEY`; OpenCode Go é apenas o primeiro perfil real de
fixture/smoke. O modo determinístico pertence aos testes; o modo real é manual,
sintético e separado.

WhatsApp e Telegram encaminham envelopes opacos; Telegram exige seleção de
empresa autenticada quando necessário e só uma nova mensagem inicia execução.
Pós-ledger, uma proposta Pagaí é materializada de forma idempotente e só a
confirmação Web autenticada e antiforgery, imutável e transacional, cria o
rascunho. Nenhum canal confirma Pagaí ou entrega externa.
