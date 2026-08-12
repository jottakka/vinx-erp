# Casos testados — issue 127, harness supervisionado

| Classe BDD | Dado / Quando / Então | Evidência pública | Situação |
|---|---|---|---|
| Sucesso | Dado descriptor externo coerente, quando declara escrita destrutiva com confirmação e idempotência, então o contrato é aceito sem se anunciar como somente leitura | `AgentToolRegistryTests.Destructive_external_write_descriptor_can_be_consistent` | coberto em #175 |
| Validação/erros | Dado descriptor com risco, idempotência ou hints MCP contraditórios, quando é criado, então falha antes de registrar ou invocar a ferramenta | `AgentToolRegistryTests.Read_only_descriptor_rejects_mcp_write_or_destructive_hints`; `AgentToolRegistryTests.Write_descriptor_requires_idempotent_non_read_only_contract` | coberto em #175 |
| Acesso | Dado perfil, empresa ou capability insuficientes, quando a policy avalia uma ferramenta, então nega antes de confirmação, validação ou invocação | `AgentRunPolicyTests.Policy_uses_fixed_precedence` | coberto em #175 |
| Estado/exceções | Dada ferramenta de escrita coerente, quando a policy a avalia, então retorna `confirmation_required` e não permite execução automática | `AgentRunPolicyTests.Policy_uses_fixed_precedence` | coberto em #175 |
| Concorrência | N/A — registry e policy desta fatia são imutáveis e não executam ferramentas; isolamento de execuções paralelas pertence ao runner em #176 | `AgentHarnessRunnerTests.Run_keeps_parallel_actor_company_and_correlation_isolated` | adiado para #176 |

## Limites mantidos

Este contrato ainda não entrega adaptação MCP, confirmação durável, provider,
OCR, WhatsApp, persistência ou ferramenta de domínio. A policy do runner
permanece a decisão determinística anterior a qualquer transporte.
