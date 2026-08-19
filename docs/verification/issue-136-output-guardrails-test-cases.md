# Casos BDD — guardrails de saída do agente (#136)

## Escopo e comandos

Esta matriz cobre a fatia de guardrails de saída do módulo `Vinx.Erp.AI`
(namespace `Guardrails`): contrato estrito do payload estruturado por
jornada, gate determinístico com ordem fixa, autorização de fontes contra o
contexto e redação sensível por padrões fixos. Os 42 casos desta fatia usam
somente texto sintético (domínios `.example`, identificadores publicados como
exemplo); nenhum teste chama provider, canal, ferramenta ou banco, e nenhum
prompt real aparece em fixture ou asserção. A resposta normalizada
(`AgentModelResponse`) nunca carrega prompt, JWT ou segredo; negações do gate
não expõem texto, fonte intrusa nem marcador hostil em nenhum campo do
resultado.

| Código | Comando exato |
|---|---|
| AU | `dotnet test tests/public/Vinx.Erp.AI.UnitTests` |
| FU | `dotnet test tests/public/Vinx.Erp.Foundation.UnitTests` |
| ARQ | `dotnet test tests/public/Vinx.Erp.ArchitectureTests` |

AU fecha com 140 casos no total (42 desta fatia). FU roda somente como
regressão (Foundation intocada). ARQ inclui o contrato de documentação do
módulo (`ModuleContractDocumentationTests`, headings obrigatórios de
`docs/modules/ai.md`).

## Cinco classes BDD

| Classe BDD | Cmd | Dado / quando / então | Resultado exigido | Evidência |
|---|---|---|---|---|
| sucesso | AU | Dado contrato `resumo-diario` com `titulo` e `corpo`, quando valida payload com exatamente esses campos, então passa. | `contract_valid`. | `AgentOutputContractTests.Valid_payload_with_required_fields_passes` |
| sucesso | AU | Dado contrato sem campos obrigatórios, quando valida payload nulo, então passa. | `contract_valid` — jornada sem schema não exige JSON estruturado. | `AgentOutputContractTests.Null_structured_json_without_required_fields_is_valid` |
| sucesso | AU | Dado fonte citada da mesma empresa do contexto, quando o `SourceAuthorizationGate` autoriza, então a fonte passa inteira. | Fonte em `AuthorizedSources`; `RemovedSources` vazio; sem violação cross-tenant. | `SourceAuthorizationGateTests.Same_company_source_is_authorized` |
| sucesso | AU | Dado resposta com JSON válido, fonte da própria empresa e confiança 0,9, quando o gate avalia, então libera. | `Result/ok` com `PolicyVersion` vigente, fonte autorizada única e texto redigido; `ToString()` do resultado não menciona a outra empresa. | `AgentOutputGateSchemaTests.Valid_structured_response_returns_result_with_authorized_sources` |
| sucesso | AU | Dado resposta fundamentada com fonte da própria empresa e `SourcesRequired`, quando o gate avalia, então libera somente a fonte autorizada. | `Result/ok`; `RedactedText` não nulo; fonte da empresa do contexto única em `AuthorizedSources`. | `AgentOutputGateGroundingTests.Grounded_response_returns_result_with_own_company_sources_only` |
| sucesso | AU | Dado resposta fundamentada com confiança nula (não reportada), quando o gate avalia, então não se abstém. | `Result/ok` — sem confiança reportada não há base para abster. | `AgentOutputGateGroundingTests.Null_confidence_does_not_abstain` |
| sucesso | AU | Dado texto legítimo com frase parecida com marcador de injeção ("Consulte as instruções do produto"), quando o gate avalia, então passa sem falso positivo. | `Result/ok`; texto liberado igual ao enviado; lista fechada não captura frase parecida. | `AgentOutputGateAdversarialTests.Legitimate_text_with_similar_phrase_passes_without_false_positive` |
| sucesso | AU | Dado texto com e-mail, quando redige, então troca por marcador fixo sem ecoar endereço. | `Enviei o pedido para [email-redigido] ontem`; `RedactedSomething` true. | `SensitiveOutputRedactorTests.Email_is_replaced_by_fixed_token_without_echoing_address` |
| sucesso | AU | Dado CPF formatado `123.456.789-09`, quando redige, então vira um identificador. | `CPF [identificador-redigido] aprovado no crédito`. | `SensitiveOutputRedactorTests.Formatted_cpf_is_redacted` |
| sucesso | AU | Dado CPF cru de 11 dígitos, quando redige, então nenhum dígito sobra. | `Cliente [identificador-redigido] liberado`; prefixo `123456` ausente. | `SensitiveOutputRedactorTests.Raw_cpf_is_redacted_without_digit_echo` |
| sucesso | AU | Dado CNPJ formatado `12.345.678/0001-95`, quando redige, então vira um identificador. | `Emitente [identificador-redigido] no XML`. | `SensitiveOutputRedactorTests.Formatted_cnpj_is_redacted` |
| sucesso | AU | Dado CNPJ cru de 14 dígitos, quando redige, então é um único identificador — nunca dois de 11. | `CNPJ [identificador-redigido] consultado` com exatamente 1 token; CNPJ avaliado antes do CPF. | `SensitiveOutputRedactorTests.Raw_cnpj_becomes_one_identifier_token_not_two_cpf_tokens` |
| sucesso | AU | Dado agência bancária com rótulo contextual ("Agência 1234-5"), quando redige, então o número vira marcador e o rótulo fica. | `Pagamento na Agência [dado-bancario-redigido] hoje`. | `SensitiveOutputRedactorTests.Bank_agency_with_context_prefix_is_redacted` |
| sucesso | AU | Dado conta bancária com rótulo PT ("conta: 54321-6") e EN ("account 1234567-8"), quando redige, então ambas viram marcador. | `conta [dado-bancario-redigido]` e `account [dado-bancario-redigido]`. | `SensitiveOutputRedactorTests.Bank_account_with_pt_and_en_context_prefix_is_redacted` |
| sucesso | AU | Dado código IBAN-like `XX00TEST000000012345`, quando redige, então vira marcador bancário. | `IBAN [dado-bancario-redigido] no arquivo`. | `SensitiveOutputRedactorTests.Iban_like_code_is_redacted` |
| sucesso | AU | Dado texto com marcadores médicos da lista fechada (diagnóstico, prescrição, CID-10, dosagem em mg), quando redige, então cada um vira o mesmo marcador. | 4 ocorrências de `[conteudo-medico-redigido]`; nenhum termo original sobra. | `SensitiveOutputRedactorTests.Medical_markers_from_closed_list_are_redacted` |
| sucesso | AU | Dado URL com host allow-listado, quando redige, então permanece intacta — inclusive dígitos de CPF no path. | Texto igual ao original; `RedactedSomething` false; demais regras não tocam o span de URL. | `SensitiveOutputRedactorTests.Url_with_allowed_host_stays_intact_including_digits_in_path` |
| sucesso | AU | Dado URL com host em caixa diferente da allow-list, quando redige, então permanece intacta. | Comparação de host `OrdinalIgnoreCase`: `DOCS.VINX.EXAMPLE` permitido como `docs.vinx.example`. | `SensitiveOutputRedactorTests.Url_host_allow_list_is_case_insensitive` |
| sucesso | AU | Dado texto misto com todas as categorias sensíveis, quando redige, então cada categoria vira marcador e nada ecoa. | Um marcador por categoria (e-mail, identificador, bancário, médico, link); nenhum valor original presente. | `SensitiveOutputRedactorTests.Mixed_sensitive_text_redacts_every_category_without_any_echo` |
| sucesso | AU | Dado resposta `ok` cujo texto contém e-mail, quando o gate libera, então o texto exposto é sempre o redigido. | `RedactedText` com `[email-redigido]`; domínio do e-mail ausente. | `AgentOutputGateRedactionTests.Result_text_is_always_redacted_and_never_echoes_the_email` |
| validação | AU | Dado payload sem campo obrigatório, quando o contrato valida, então reprova nomeando o campo. | `required_field_missing:corpo`. | `AgentOutputContractTests.Missing_required_field_reports_field_name` |
| validação | AU | Dado payload com campo fora do contrato, quando o contrato valida, então reprova nomeando o campo. | `unknown_field:extra`. | `AgentOutputContractTests.Unknown_field_reports_field_name` |
| validação | AU | Dado JSON malformado, array ou escalar, quando o contrato valida, então falha fechado. | `structured_json_invalid` nos três casos — somente objeto raiz vale. | `AgentOutputContractTests.Malformed_or_non_object_json_fails_closed` |
| validação | AU | Dado payload nulo com contrato exigindo campos, quando o contrato valida, então falha fechado. | `structured_json_invalid`. | `AgentOutputContractTests.Null_structured_json_with_required_fields_fails_closed` |
| validação | AU | Dado resposta com schema inválido, quando o gate avalia (passo 1 da ordem fixa), então reprova sem expor nada. | `ValidationError/schema_invalid`; `RedactedText` nulo, fontes vazias; nem texto nem referência de fonte em nenhum campo do resultado. | `AgentOutputGateSchemaTests.Invalid_schema_blocks_text_from_channel` |
| validação | AU | Dado lista de fontes vazia, quando o `SourceAuthorizationGate` autoriza, então não há autorizadas nem violação. | `AuthorizedSources` e `RemovedSources` vazios; `CrossTenantViolation` false. | `SourceAuthorizationGateTests.Empty_source_list_has_no_authorized_and_no_violation` |
| validação | AU | Dado URL hostil no fim da frase com pontuação, quando redige, então o marcador preserva a pontuação final. | `Acesse [link-removido].` — pontuação não é engolida pelo link. | `SensitiveOutputRedactorTests.Url_at_sentence_end_keeps_trailing_punctuation` |
| validação | AU | Dado texto limpo de produção (pedido, número curto, tempo), quando redige, então permanece inalterado. | Texto idêntico; `RedactedSomething` false — sem falso positivo em numeração operacional comum. | `SensitiveOutputRedactorTests.Clean_text_returns_unchanged_without_redaction_flag` |
| validação | AU | Dado texto nulo ou vazio, quando redige, então atravessa inalterado. | `RedactedText` nulo/vazio com `RedactedSomething` false; a redação nunca inventa conteúdo. | `SensitiveOutputRedactorTests.Null_and_empty_text_pass_through_without_redaction` |
| acesso | AU | Dado fonte citada de outra empresa, quando o `SourceAuthorizationGate` autoriza, então remove e sinaliza violação. | Fonte em `RemovedSources`; `AuthorizedSources` vazio; `CrossTenantViolation` true. | `SourceAuthorizationGateTests.Other_company_source_is_removed_and_flags_cross_tenant_violation` |
| acesso | AU | Dado resposta que cita fonte de outra empresa, quando o gate avalia, então nega sem expor a intrusa. | `Denied/cross_tenant_source`; `RedactedText` nulo, fontes vazias; id, referência e texto da intrusa ausentes do resultado inteiro. | `AgentOutputGateGroundingTests.Cross_tenant_source_denies_without_exposing_intruder_anywhere` |
| acesso | AU | Dado `SourcesRequired` sem nenhuma fonte autorizada, quando o gate avalia, então se abstém em vez de completar lacunas. | `NeedsClarification/no_authorized_source`; texto nunca chega ao canal como fato. | `AgentOutputGateGroundingTests.Sources_required_without_authorized_source_abstains` |
| acesso | AU | Dado confiança 0,2 — abaixo do piso 0,5 —, quando o gate avalia, então se abstém. | `NeedsClarification/low_confidence`; `MinimumConfidence = 0.5` travada; sem texto nem fontes. | `AgentOutputGateGroundingTests.Low_confidence_abstains` |
| acesso | AU | Dado resposta sem texto e sem fontes, quando o gate avalia, então se abstém por ausência de evidência. | `NeedsClarification/no_evidence`; nada chega ao canal. | `AgentOutputGateGroundingTests.Empty_text_without_sources_abstains_with_no_evidence` |
| acesso | AU | Dado texto contendo marcador da lista fechada de injeção, quando o gate avalia (passo 2, antes das fontes), então nega sem expor o texto. | `Denied/prompt_injection_detected`; texto hostil, marcador e fontes ausentes do resultado inteiro. | `AgentOutputGateAdversarialTests.Injection_marker_denies_without_exposing_text` |
| acesso | AU | Dado injeção e fonte cross-tenant na mesma resposta, quando o gate avalia, então a injeção (passo 2) define o desfecho antes da fonte (passo 4). | `prompt_injection_detected` — não `cross_tenant_source`; ordem fixa é observável no reason code. | `AgentOutputGateAdversarialTests.Injection_wins_over_cross_tenant_source` |
| acesso | AU | Dado URL com host fora da allow-list, quando redige, então o link é removido. | `Detalhes em [link-removido] fim` — só a URL some, o texto da frase permanece. | `SensitiveOutputRedactorTests.Url_with_unknown_host_is_removed` |
| estado | AU | Dado resposta com proposta de tool em JSON válido e em JSON quebrado, quando o gate avalia ambas, então a flag `HasToolProposal` sobrevive a qualquer status. | `Result` e `ValidationError` com `HasToolProposal` true — o estado de confirmação é espelhado, nunca descartado. | `AgentOutputGateSchemaTests.Tool_proposal_flag_is_preserved_regardless_of_status` |
| estado | AU | Dado resposta com proposta de tool e e-mail no texto, quando o gate libera, então o estado é puro: `Result/ok` com a flag e somente texto redigido. | `HasToolProposal` true; `[email-redigido]` presente; nenhum caminho de execução no gate. | `AgentOutputGateAdversarialTests.Tool_proposal_state_is_pure_and_carries_redacted_text` |
| estado | AU | Dado texto lotado de CPF, CNPJ e link hostil, quando o gate libera, então a redação é cosmética e nunca muda o desfecho. | Permanece `Result/ok`; 2 tokens de identificador (14 dígitos = um) e link removido; nenhum dígito original ecoado. | `AgentOutputGateRedactionTests.Redaction_never_changes_gate_status` |
| estado | AU | Dado os seis desfechos do gate (ok, injeção, cross-tenant, sem fonte, baixa confiança, schema), quando cada um fecha, então todos carregam a versão da política. | `PolicyVersion` igual a `AgentPolicyVersion.Current` (`2026-08-19.1`) em todo resultado — auditoria por desfecho. | `AgentOutputGateAdversarialTests.Policy_version_is_present_on_every_outcome` |
| concorrência | AU | Dados dois gates avaliando em paralelo respostas de empresas distintas com fontes próprias, quando concluem, então cada resultado casa 1:1 com a sua empresa. | Cada `ok` com exatamente as fontes da própria empresa; id, referências e texto da outra empresa ausentes de ambos os resultados — o gate é stateless por chamada. | `AgentOutputGateAdversarialTests.Concurrent_gates_keep_tenant_results_isolated` |

## Ordem fixa do gate

`AgentOutputGate.Evaluate` aplica, nesta ordem: (1) contrato estrutural —
schema inválido reprova sem expor texto; (2) injeção de prompt — marcador da
lista fechada nega; (3) autorização de fontes — fonte de outra empresa nega;
(3a) fontes exigidas sem autorizada abstém; (4) confiança abaixo de 0,5
abstém e resposta sem texto e sem fontes abstém por `no_evidence`; (5)
redação — o texto liberado é sempre o redigido e a redação nunca altera o
status. Negações e abstenções não carregam texto nem fontes; somente a
proposta de tool (`HasToolProposal`) segue espelhada em qualquer desfecho.

## Lacunas conhecidas

- A heurística de injeção é defesa-em-profundidade com lista FECHADA de
  marcadores PT/EN (match ordinal ignore-case): não é moderação semântica,
  não detecta parafraseamento e nunca serve para autorizar — um texto sem
  marcador passa pelos demais passos do gate, não é aprovado por ela.
  Termos novos só entram por mudança de política versionada.
- A redação é determinística por padrões fixos e NÃO cobre: número bancário
  sem rótulo contextual imediato (agência/conta/account), sequências de
  12–13 dígitos (nem CPF de 11 nem CNPJ de 14), IBAN escrito em minúsculas
  (o padrão exige caixa alta) e dosagem com dois-pontos entre o rótulo e o
  valor ("dosagem: 500 mg"). Sem moderação semântica: categoria fora dos
  padrões atravessa.
- `requires_confirmation_state` é declarado e não emitido: a proposta de
  tool viaja como estado puro (`HasToolProposal`) em qualquer desfecho; não
  existe fluxo de confirmação nesta fatia.
- Nenhum host chama o gate: a integração do gate ao canal/API supervisionado
  fica para #138. `AgentModelResponse` é construída fora daqui a partir da
  resposta do modelo; essa costura também pertence a #138.

Provider real, execução confirmada de ferramentas, moderação semântica e
classificador de confiança não existem nesta fatia e pertencem a #135–#138.
