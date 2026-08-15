# Casos públicos — issue 127, harness package-first

## Escopo

O projeto usa o Microsoft Agent Framework real com `IChatClient` injetado. As
duas ferramentas são sondas inertes, sem dados do ERP e sem registro em host.
A superfície pública aceita somente texto; não existe API para entregar uma
aprovação ou executar a escrita protegida.

Comando:

```bash
dotnet test tests/public/Vinx.Erp.AI.UnitTests --no-restore --nologo -v q
```

| Classe | Cenário | Evidência pública |
|---|---|---|
| sucesso | Leitura executa pelo loop real e devolve a resposta | `HarnessApprovalBoundaryTests.Read_only_user_turn_executes_through_real_harness` |
| validação/erros | Escrita sem entrega do servidor fica pendente e não executa | `HarnessApprovalBoundaryTests.Write_without_server_delivery_remains_pending_and_unexecuted` |
| validação/erros | A requisição ao modelo contém somente as ferramentas VINX registradas | `HarnessApprovalBoundaryTests.Outgoing_tool_list_contains_only_registered_vinx_tools` |
| acesso | Contexto ausente ou com identificador vazio é negado; contexto válido classifica a escrita | `HarnessApprovalBoundaryTests.Evaluate_uses_only_trusted_server_context` |
| estado/exceções | O feixe expõe dois métodos textuais e o gate não expõe entrega de aprovação | `HarnessApprovalBoundaryTests.Public_surface_is_text_only_and_has_no_approval_delivery` |
| estado/exceções | O loop encerra dentro do limite configurado | `HarnessApprovalBoundaryTests.Iteration_bound_is_respected` |
| concorrência/lote | `CallId` vazio, ferramenta desconhecida, leitura protegida e múltiplas pendências são negados | `HarnessApprovalBoundaryTests.Evaluate_rejects_unknown_read_and_multiple_requests` |

## Limites

O teste não afirma execução de escrita, retomada de sessão, replay local ou
integração HTTP. Essas capacidades não existem nesta fatia. Aprovação durável
permanece na Foundation e a integração hospedada depende da issue #138.
