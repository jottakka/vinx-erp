# Casos BDD — guardrails de saída do agente (#136)

## Escopo e comandos

Esta matriz cobre a fatia de guardrails de saída do módulo `Vinx.Erp.AI`
(namespace `Guardrails`): contrato estrito do payload estruturado por
jornada, gate determinístico com ordem fixa e autorização de fontes contra o
contexto. Os 21 casos desta fatia usam somente texto sintético (domínios
`.example`, identificadores publicados como exemplo); nenhum teste chama
provider, canal, ferramenta ou banco, e nenhum prompt real aparece em
fixture ou asserção. A resposta normalizada (`AgentModelResponse`) nunca
carrega prompt, JWT ou segredo; negações do gate não expõem texto nem fonte
intrusa em nenhum campo do resultado.

| Código | Comando exato |
|---|---|
| AU | `dotnet test tests/public/Vinx.Erp.AI.UnitTests` |
| FU | `dotnet test tests/public/Vinx.Erp.Foundation.UnitTests` |
| ARQ | `dotnet test tests/public/Vinx.Erp.ArchitectureTests` |

AU fecha com 290 casos no total (21 desta fatia). FU roda somente como
regressão (Foundation intocada). ARQ inclui o contrato de documentação do
módulo (`ModuleContractDocumentationTests`, headings obrigatórios de
`docs/modules/ai.md`).

## Cinco classes BDD

| Classe BDD | Cmd | Dado / quando / então | Resultado exigido | Evidência |
|---|---|---|---|---|
| sucesso | AU | Dado contrato `resumo-diario` com `titulo` e `corpo`, quando valida payload com exatamente esses campos, então passa. | `contract_valid`. | `AgentOutputContractTests.Valid_payload_with_required_fields_passes` |
| sucesso | AU | Dado contrato sem campos obrigatórios, quando valida payload nulo, então passa. | `contract_valid` — jornada sem schema não exige JSON estruturado. | `AgentOutputContractTests.Null_structured_json_without_required_fields_is_valid` |
| sucesso | AU | Dado fonte citada da mesma empresa do contexto, quando o `SourceAuthorizationGate` autoriza, então a fonte passa inteira. | Fonte em `AuthorizedSources`; `RemovedSources` vazio; sem violação cross-tenant. | `SourceAuthorizationGateTests.Same_company_source_is_authorized` |
| sucesso | AU | Dado resposta com JSON válido, fonte da própria empresa e confiança 0,9, quando o gate avalia, então libera. | `Result/ok` com `PolicyVersion` vigente, fonte autorizada única e texto do modelo sem transformação; `ToString()` do resultado não menciona a outra empresa. | `AgentOutputGateSchemaTests.Valid_structured_response_returns_result_with_authorized_sources` |
| sucesso | AU | Dado resposta fundamentada com fonte da própria empresa e `SourcesRequired`, quando o gate avalia, então libera somente a fonte autorizada. | `Result/ok`; `Text` não nulo; fonte da empresa do contexto única em `AuthorizedSources`. | `AgentOutputGateGroundingTests.Grounded_response_returns_result_with_own_company_sources_only` |
| sucesso | AU | Dado resposta fundamentada com confiança nula (não reportada), quando o gate avalia, então não se abstém. | `Result/ok` — sem confiança reportada não há base para abster. | `AgentOutputGateGroundingTests.Null_confidence_does_not_abstain` |
| sucesso | AU | Dado resposta com proposta de tool e e-mail no texto, quando o gate libera, então o estado é puro e o texto atravessa sem transformação. | `HasToolProposal` true; `Text` igual ao enviado — o gate não redige; nenhum caminho de execução. | `AgentOutputGateAdversarialTests.Tool_proposal_state_is_pure_and_carries_text_unmodified` |
| validação | AU | Dado payload sem campo obrigatório, quando o contrato valida, então reprova nomeando o campo. | `required_field_missing:corpo`. | `AgentOutputContractTests.Missing_required_field_reports_field_name` |
| validação | AU | Dado payload com campo fora do contrato, quando o contrato valida, então reprova nomeando o campo. | `unknown_field:extra`. | `AgentOutputContractTests.Unknown_field_reports_field_name` |
| validação | AU | Dado JSON malformado, array ou escalar, quando o contrato valida, então falha fechado. | `structured_json_invalid` nos três casos — somente objeto raiz vale. | `AgentOutputContractTests.Malformed_or_non_object_json_fails_closed` |
| validação | AU | Dado payload nulo com contrato exigindo campos, quando o contrato valida, então falha fechado. | `structured_json_invalid`. | `AgentOutputContractTests.Null_structured_json_with_required_fields_fails_closed` |
| validação | AU | Dado resposta com schema inválido, quando o gate avalia (passo 1 da ordem fixa), então reprova sem expor nada. | `ValidationError/schema_invalid`; `Text` nulo, fontes vazias; nem texto nem referência de fonte em nenhum campo do resultado. | `AgentOutputGateSchemaTests.Invalid_schema_blocks_text_from_channel` |
| validação | AU | Dado lista de fontes vazia, quando o `SourceAuthorizationGate` autoriza, então não há autorizadas nem violação. | `AuthorizedSources` e `RemovedSources` vazios; `CrossTenantViolation` false. | `SourceAuthorizationGateTests.Empty_source_list_has_no_authorized_and_no_violation` |
| acesso | AU | Dado fonte citada de outra empresa, quando o `SourceAuthorizationGate` autoriza, então remove e sinaliza violação. | Fonte em `RemovedSources`; `AuthorizedSources` vazio; `CrossTenantViolation` true. | `SourceAuthorizationGateTests.Other_company_source_is_removed_and_flags_cross_tenant_violation` |
| acesso | AU | Dado resposta que cita fonte de outra empresa, quando o gate avalia, então nega sem expor a intrusa. | `Denied/cross_tenant_source`; `Text` nulo, fontes vazias; id, referência e texto da intrusa ausentes do resultado inteiro. | `AgentOutputGateGroundingTests.Cross_tenant_source_denies_without_exposing_intruder_anywhere` |
| acesso | AU | Dado `SourcesRequired` sem nenhuma fonte autorizada, quando o gate avalia, então se abstém em vez de completar lacunas. | `NeedsClarification/no_authorized_source`; texto nunca chega ao canal como fato. | `AgentOutputGateGroundingTests.Sources_required_without_authorized_source_abstains` |
| acesso | AU | Dado confiança 0,2 — abaixo do piso 0,5 —, quando o gate avalia, então se abstém. | `NeedsClarification/low_confidence`; `MinimumConfidence = 0.5` travada; sem texto nem fontes. | `AgentOutputGateGroundingTests.Low_confidence_abstains` |
| acesso | AU | Dado resposta sem texto e sem fontes, quando o gate avalia, então se abstém por ausência de evidência. | `NeedsClarification/no_evidence`; nada chega ao canal. | `AgentOutputGateGroundingTests.Empty_text_without_sources_abstains_with_no_evidence` |
| estado | AU | Dado resposta com proposta de tool em JSON válido e em JSON quebrado, quando o gate avalia ambas, então a flag `HasToolProposal` sobrevive a qualquer status. | `Result` e `ValidationError` com `HasToolProposal` true — o estado de confirmação é espelhado, nunca descartado. | `AgentOutputGateSchemaTests.Tool_proposal_flag_is_preserved_regardless_of_status` |
| estado | AU | Dado os desfechos do gate (ok, cross-tenant, sem fonte, baixa confiança, schema, sem evidência), quando cada um fecha, então todos carregam a versão da política. | `PolicyVersion` igual a `AgentPolicyVersion.Current` (`2026-08-19.2`) em todo resultado — auditoria por desfecho. | `AgentOutputGateAdversarialTests.Policy_version_is_present_on_every_outcome` |
| concorrência | AU | Dados dois gates avaliando em paralelo respostas de empresas distintas com fontes próprias, quando concluem, então cada resultado casa 1:1 com a sua empresa. | Cada `ok` com exatamente as fontes da própria empresa; id, referências e texto da outra empresa ausentes de ambos os resultados — o gate é stateless por chamada. | `AgentOutputGateAdversarialTests.Concurrent_gates_keep_tenant_results_isolated` |

## Ordem fixa do gate

`AgentOutputGate.Evaluate` aplica, nesta ordem: (1) contrato estrutural —
schema inválido reprova sem expor texto; (2) autorização de fontes — fonte
de outra empresa nega; (2a) fontes exigidas sem autorizada abstém;
(3) confiança abaixo de 0,5 abstém e resposta sem texto e sem fontes abstém
por `no_evidence`; (4) caminho ok — o texto do modelo é liberado sem
transformação. Negações e abstenções não carregam texto nem fontes; somente
a proposta de tool (`HasToolProposal`) segue espelhada em qualquer desfecho.

## Decisões e lacunas conhecidas

- **Sem redação determinística, por decisão**: redação de PII
  (CPF/CNPJ, banco, médico, links) por regex fixo foi removida — padrão
  numa única linguagem cobre fração mínima do problema e gera falso
  positivo em numeração operacional (qualquer sequência de 11 dígitos,
  telefone, número de pedido). Quando um canal exigir redação, será por
  detector não determinístico (serviço/modelo) decidido por canal em #138+.
- **Sem detecção de injeção por lista fechada, por decisão**: lista fixa de
  marcadores PT/EN não detecta parafraseamento e nega texto legítimo
  parecido. Triagem de injeção, quando exigida, também será detector não
  determinístico por canal em #138+.
- O payload estruturado (`StructuredJson`) não viaja no resultado do gate;
  ao integrar com o canal supervisionado (#138), qualquer campo do payload
  exposto ao usuário final deve passar pelas mesmas regras do gate.
- `requires_confirmation_state` é declarado e não emitido: a proposta de
  tool viaja como estado puro (`HasToolProposal`) em qualquer desfecho; não
  existe fluxo de confirmação nesta fatia.
- Nenhum host chama o gate: a integração do gate ao canal/API supervisionado
  fica para #138. `AgentModelResponse` é construída fora daqui a partir da
  resposta do modelo; essa costura também pertence a #138.

Provider real, execução confirmada de ferramentas, moderação semântica e
classificador de confiança não existem nesta fatia e pertencem a #135–#138.
