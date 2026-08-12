# Casos testados — issue 127, harness supervisionado

| Classe BDD | Dado / Quando / Então | Evidência pública | Situação |
|---|---|---|---|
| Sucesso | Dado descriptor externo coerente, quando declara escrita destrutiva não idempotente sem se anunciar como somente leitura, então o contrato é aceito | `AgentToolRegistryTests.Non_idempotent_destructive_external_write_descriptor_can_be_consistent` | coberto em #175 |
| Validação/erros | Dado descriptor com risco, hint MCP ou idempotência requerida contraditórios, quando é criado, então falha antes de registrar ou invocar a ferramenta | `AgentToolRegistryTests.Read_only_descriptor_rejects_mcp_write_or_destructive_hints`; `AgentToolRegistryTests.Write_descriptor_rejects_read_only_hint_or_required_idempotency_mismatch` | coberto em #175 |
| Acesso | Dado perfil, empresa ou capability insuficientes, quando a policy avalia uma ferramenta, então nega antes de confirmação, validação ou invocação | `AgentRunPolicyTests.Policy_uses_fixed_precedence` | coberto em #175 |
| Estado/exceções | Dado modelo ou ferramenta que ignora cancelamento, quando o timeout do perfil vence, então o runner retorna `agent_timeout`, encerra seu ciclo e não pede novo turno; resultado nulo ou rotulado para outra ferramenta também falha fechado | `AgentHarnessRunnerTests.Run_maps_profile_timeout_when_model_ignores_cancellation`; `AgentHarnessRunnerTests.Run_maps_profile_timeout_when_tool_ignores_cancellation_without_next_model_request`; `AgentHarnessRunnerTests.Run_maps_profile_timeout_when_model_later_throws_unrelated_cancellation`; `AgentHarnessRunnerTests.Run_rejects_null_tool_result_without_requesting_next_model_turn`; `AgentHarnessRunnerTests.Run_rejects_tool_result_for_another_tool_without_requesting_next_model_turn` | coberto em #176 |
| Concorrência | Dadas duas execuções paralelas com atores, empresas e correlações distintos, quando concluem, então eventos não misturam seus contextos confiáveis | `AgentHarnessRunnerTests.Run_keeps_parallel_actor_company_and_correlation_isolated` | coberto em #176 |

## Limites mantidos

Este contrato ainda não entrega adaptação MCP, confirmação durável, provider,
OCR, WhatsApp, persistência ou ferramenta de domínio. A policy do runner
permanece a decisão determinística anterior a qualquer transporte.
