# Casos públicos: Agent Harness V1 — issue #127

Esta matriz liga o comportamento aceito na issue #127 aos nomes exatos dos testes públicos. Os cenários usam formato BDD em inglês para permanecerem próximos do código.

| Categoria | Given / When / Then | Teste público | Quebra coberta |
| --- | --- | --- | --- |
| Final reply | Given a model final reply, when the harness runs, then it returns without invoking a tool. | `AgentHarnessTests.Run_returns_final_reply_without_invoking_tool` | Invocar tool sem pedido ou perder resposta final. |
| Read tool | Given an allowed read tool, when the model requests it, then its safe result returns to the model before the final reply. | `AgentHarnessTests.Run_passes_read_result_to_model_then_returns_final_reply` | Omitir resultado, repetir tool ou terminar cedo. |
| Missing company | Given a tool that requires company, when trusted context has none, then policy stops before validation and invocation. | `AgentHarnessTests.Run_returns_policy_outcome_without_validating` | Aceitar empresa ausente ou validar payload não autorizado. |
| Denied capability / allowlist | Given denied capability or a tool outside the profile, when requested, then policy denies before validation. | `AgentHarnessTests.Run_returns_policy_outcome_without_validating` | Ignorar capability, allowlist ou precedência segura. |
| Confirmation barrier | Given any write-risk tool, when requested, then confirmation is returned without validation or invocation. | `AgentHarnessTests.Run_returns_confirmation_without_validating_or_invoking_write_tool` | Executar escrita sem confirmação. |
| Model company override | Given model arguments containing company aliases, when a read tool runs, then only trusted company is used. | `AgentHarnessTests.Run_uses_trusted_company_after_model_proposes_company_argument` | Modelo substituir escopo da empresa. |
| Untrusted injection | Given injection text in user/context input, when policy denies the tool, then text cannot bypass policy. | `AgentHarnessTests.Run_ignores_injection_text_when_policy_denies_tool` | Conteúdo não confiável autorizar execução. |
| Timeout | Given internal timeout, when model execution exceeds it, then harness returns `validation_error` with `agent_timeout` and does not invoke a tool. | `AgentHarnessTests.Run_maps_internal_timeout_without_invoking_tool` | Timeout virar execução parcial, código incorreto ou exceção não mapeada. |
| Caller cancellation | Given caller cancellation, when a run is active, then cancellation propagates and no tool is invoked. | `AgentHarnessTests.Run_propagates_caller_cancellation_without_invoking_tool` | Ocultar cancelamento externo ou executar após cancelamento. |
| Turn limit | Given the tool-turn limit is exhausted, when the model proposes another tool, then harness returns `validation_error` with `tool_turn_limit_exceeded` before a second invocation. | `AgentHarnessTests.Run_stops_before_second_tool_invocation_at_turn_limit` | Invocar outra tool depois do limite ou retornar código incorreto. |
| Safe observation | Given a complete run, when events are observed, then only safe metadata is exposed. | `AgentHarnessTests.Run_observer_receives_only_safe_metadata` | Vazar prompt, argumentos, resultado ou identificador arbitrário. |
| Concurrency isolation | Given concurrent runs for distinct companies, when both execute, then trusted contexts remain isolated. | `AgentHarnessTests.Run_keeps_concurrent_execution_contexts_isolated` | Compartilhar estado, empresa, turnos ou cancelamento entre runs. |

## Evidência complementar

- `AgentContractsTests.Run_request_exposes_only_untrusted_message_and_external_context` limita a superfície da entrada não confiável.
- `AgentContractsTests.Model_request_exposes_only_untrusted_request_and_approved_model_inputs` impede envio de contexto confiável e perfil ao modelo.
- `AgentContractsTests.Tool_call_removes_nested_company_scope_aliases_and_preserves_business_fields` remove aliases de empresa em qualquer profundidade sem apagar campos de negócio.
- `AgentContractsTests.Run_event_exposes_only_safe_metadata` limita estruturalmente eventos de execução.
- `AiDependencyTests.Ai_module_does_not_depend_on_transport_hosts` impede dependência do módulo AI em hosts de API, MCP ou worker.

## Escopo adiado

- #125: identidade, contexto de empresa e tool `whoAmI` integrados ao backend real.
- #126: tools de escrita com preparação, confirmação, auditoria e idempotência.
- #28: integração do canal UI.
- #48: integração do canal WhatsApp na V1 do produto.
- #52: OCR de PDFs e imagens como entrada não confiável.
- #40: host MCP remoto e consumo por clientes externos, incluindo Claude e GPT.

## Comandos

```bash
dotnet test tests/public/Vinx.Erp.ArchitectureTests/Vinx.Erp.ArchitectureTests.csproj --no-restore
dotnet test tests/public/Vinx.Erp.AI.UnitTests/Vinx.Erp.AI.UnitTests.csproj --no-restore
```
