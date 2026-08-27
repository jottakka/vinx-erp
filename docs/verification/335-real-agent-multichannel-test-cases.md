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

Há evidência focada por tarefa, sem Docker, para os contratos acima. No último
rerun congelado de migrations/adapters, 24 de 25 testes passaram; a única
asserção antiforgery desatualizada foi corrigida depois, com compilação limpa,
mas sem novo rerun. Permanecem pendentes: `verify-public.sh` final, revisão
completa da branch, CI e smokes reais sintéticos de texto/visão. Não há
evidência de envio por canal nem de execução de provider real.

## Escopo congelado

- 132 arquivos no total: 83 não teste e 49 de teste.
- Alterações manuais: +12.901/-4.037 linhas (16.938 de churn).
- Gerados: 5 arquivos e +8.261 linhas.

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
