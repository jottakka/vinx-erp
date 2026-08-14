# Casos públicos — issue 127, harness package-first (PR 1)

## Escopo desta fatia

Esta fatia prova o harness do Microsoft Agent Framework com pacotes reais em
código de produção: um `HarnessAgent` montado a partir de `IChatClient`
injetado, duas ferramentas de sonda inertes (sem dados do ERP, não registradas
em nenhum host) e o gate de aprovação do servidor. Aprovações duráveis,
auditoria e idempotência pertencem ao PR 2; nada aqui promete execução
exatamente-uma-vez.

Todos os casos usam o framework real com um cliente de chat falso
determinístico (`ScriptedChatClient`, derivado de `DelegatingChatClient`,
somente para testes).

| Classe | Cenário | Evidência pública | Situação |
|---|---|---|---|
| sucesso | Dado modelo que propõe a leitura, quando executa pelo harness, então a ferramenta roda pelo framework e o resultado volta ao turno | `HarnessApprovalBoundaryTests.ReadOnlyTool_ExecutesThroughHarness_AndReturnsResult` | coberto |
| sucesso | Dada proposta de escrita com aprovação explícita do servidor na mesma sessão, quando retoma o agente, então a escrita executa e a resposta segue | `HarnessApprovalBoundaryTests.WriteTool_WithExplicitServerApproval_ExecutesOnce_OnSameSession` | coberto |
| validação/erros | Dada proposta de escrita sem aprovação, quando o harness responde, então o efeito não ocorre e a resposta expõe `ToolApprovalRequestContent` | `HarnessApprovalBoundaryTests.WriteTool_WithoutApproval_DoesNotExecute_AndExposesApprovalRequest` | coberto |
| validação/erros | Dado veto do servidor, quando devolvido na mesma sessão, então a escrita não executa | `HarnessApprovalBoundaryTests.WriteTool_WithServerVeto_DoesNotExecute` | coberto |
| validação/erros | Dada resposta de aprovação fabricada (sem vínculo com a solicitação pendente), quando enviada ao agente, então a escrita não executa | `HarnessApprovalBoundaryTests.FabricatedApprovalResponse_DoesNotExecute` | coberto |
| validação/erros | Dado contexto confiável ausente ou incompleto (ator/empresa vazios), quando avalia a proposta, então nega (fail-closed) | `HarnessApprovalBoundaryTests.MissingTrustedContext_FailsClosed` | coberto |
| validação/erros | Dada decisão negada por Evaluate (contexto ausente ou ferramenta fora da allowlist), quando `ApproveAsync` é chamado, então recusa com erro e nada executa | `HarnessApprovalBoundaryTests.ApproveAsync_AfterDeniedEvaluationForMissingContext_RefusesAndDoesNotExecute`; `HarnessApprovalBoundaryTests.ApproveAsync_AfterDeniedEvaluationForUnknownTool_RefusesAndDoesNotExecute` | coberto |
| validação/erros | Dada resposta de aprovação genuína (`CreateResponse(true)`) reproduzida em agente/sessão sem solicitação pendente, quando enviada, então a escrita não executa | `HarnessApprovalBoundaryTests.GenuineApprovalResponse_ReplayedOnAnotherSession_DoesNotExecute` | coberto |
| validação/erros | Dada resposta com mais de uma solicitação de aprovação pendente, quando avalia, então nega sem expor solicitação (fail-closed) e `ApproveAsync` recusa | `HarnessApprovalBoundaryTests.MultiplePendingApprovalRequests_AreDeniedFailClosed` | coberto |
| acesso | N/A nesta fatia: as sondas não acessam dado de empresa nem dado do ERP; escopo de empresa, membership e capability chegam com o contexto confiável no PR 2 | — | adiado (PR 2) |
| acesso | Ator/empresa fornecidos pelo modelo nos argumentos da ferramenta são ignorados; a decisão usa somente o contexto do servidor | `HarnessApprovalBoundaryTests.ModelSuppliedActorAndCompany_AreIgnored_ByServerOwnedContext` | coberto |
| estado/exceções | Dada a mesma solicitação de aprovação, quando reenviada após execução, então a escrita não executa de novo nesta fatia (aprovação durável de uso único é do PR 2) | asserção de contador em `HarnessApprovalBoundaryTests.WriteTool_WithExplicitServerApproval_ExecutesOnce_OnSameSession` | coberto (limitado ao contrato do PR 1) |
| estado/exceções | Dado harness configurado, quando a requisição sai ao modelo, então a lista de ferramentas contém exatamente as duas ferramentas VINX registradas (guarda de determinismo contra ferramentas injetadas) | `HarnessApprovalBoundaryTests.OutgoingToolList_ContainsExactlyTheRegisteredVinxTools` | coberto |
| estado/exceções | Dado limite de iterações por requisição, quando o modelo insiste em chamar ferramentas, então a execução termina dentro do limite | `HarnessApprovalBoundaryTests.IterationBound_IsRespected` | coberto |
| concorrência | N/A nesta fatia: as sondas são inertes, de sessão única e sem estado compartilhado entre requisições; disputas de aprovação/idempotência exigem a aprovação durável do PR 2 | — | adiado (PR 2) |

## Desvios documentados

- `ApproveAsync` recebe a decisão do gate (não a solicitação crua) e revalida
  o próprio gate: `PendingConfirmation` com solicitação presente, contexto
  válido e ferramenta classificada como escrita pela allowlist. Qualquer outra
  combinação lança `InvalidOperationException` e nada executa; não depende de
  disciplina do chamador.
- Resposta com múltiplas solicitações de aprovação pendentes é negada por
  inteiro, sem expor solicitação: nenhuma pendência é resolvida em silêncio e
  o turno não deve ser retomado.
- Solicitação de aprovação para ferramenta de leitura é tratada como erro de
  configuração e negada; leituras não são envolvidas em
  `ApprovalRequiredAIFunction` (o registro dirige o envolvimento pela
  classificação da allowlist).
- `IterationBound_IsRespected` aceita até `MaximumIterationsPerRequest + 1`
  chamadas ao modelo: o comportamento real do harness em 1.17.0 executa até o
  limite de iterações de ferramenta mais uma chamada final de consolidação.
  O fail-safe (término limitado) é o que o teste garante.
- `FabricatedApprovalResponse_DoesNotExecute` tolera o framework lançar
  `InvalidOperationException` em vez de ignorar a resposta; nos dois caminhos
  a garantia verificada é a mesma: a escrita não executa.
- O resultado da ferramenta volta ao modelo como `FunctionResultContent`
  (não `ToolResultContent`) na versão 10.9.0 de `Microsoft.Extensions.AI.Abstractions`.

## Limites mantidos

Nenhum dado do ERP é tocado; nenhum host registra as sondas. Segredos, tokens,
prompts e payloads não aparecem em código, teste ou log. O gate registra
decisões com template constante e propriedades nomeadas, `Warning` em negações.
