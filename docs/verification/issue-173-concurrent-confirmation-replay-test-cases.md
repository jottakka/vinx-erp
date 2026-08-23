# Casos BDD — replay determinístico da confirmação concorrente (#173)

Esta matriz preserva o contrato de confirmação documental: o estado, a
idempotência e a auditoria persistidos são a autoridade; storage e scanner só
executam uma vez para a confirmação vencedora.

| Classe BDD | Dado / quando / então | Testes públicos exatos |
| --- | --- | --- |
| sucesso | Dado documento em quarentena e confirmação válida, quando a confirmação conclui, então retorna o arquivo confirmado. | `Valid_confirmation_rechecks_private_storage_and_confirms_the_file` |
| validação/erro | Dado o mesmo `RequestId` com outro arquivo, quando a segunda confirmação chega, então nega com `idempotency_conflict`. | `Confirmation_with_same_request_id_and_different_file_returns_idempotency_conflict` |
| autenticação | Dada chamada anônima à superfície protegida, quando passa pelo gate comum, então retorna `401`. | `Membership_map_rejects_an_anonymous_call` (inalterado; gate comum) |
| acesso/membership | Dada empresa sem membership do actor, quando cria intenção, então retorna `membership_required` com auditoria negada. | `Intent_rejects_missing_membership_and_missing_upload_capability` (inalterado) |
| acesso/membership | Dado read vazio de idempotência e confirmação vencedora, quando a membership de A é revogada antes do replay, então reavalia e retorna `403 membership_required`, com uma auditoria negada e sem novo efeito. | `Concurrent_same_request_rechecks_membership_before_returning_a_committed_replay` |
| acesso/capability | Dada autorização removida durante a inspeção privada, quando a finalização ocorre, então reautoriza e nega sem mudar o estado. | `Confirmation_reauthorizes_after_private_inspection_finishes` (inalterado) |
| acesso/capability | Dado read vazio de idempotência e confirmação vencedora, quando a capability de A é revogada antes do replay, então reavalia e nega antes de devolver o resultado durável. | `Concurrent_same_request_rechecks_capability_before_returning_a_committed_replay` |
| isolamento entre tenants | Dado arquivo de outra empresa à qual o actor também tem acesso, quando confirma pela empresa errada, então oculta como `resource_not_found`. | `Confirmation_hides_a_file_from_another_authorized_tenant` (inalterado) |
| falha de storage/scanner | Dada falha de leitura privada ou scanner indisponível, quando confirma, então mantém quarentena, retorna falha segura e não expõe download. | `Storage_read_failure_keeps_the_file_quarantined_without_a_download_endpoint`; `Unavailable_malware_scan_keeps_the_file_quarantined_and_records_a_failure_without_a_download_url` (inalterados) |
| estado/exceção | Dada confirmação já confirmada com o mesmo pedido, quando é repetida, então devolve o resultado durável sem nova auditoria ou exposição. | `Repeated_confirmation_replays_confirmed_state_without_a_second_audit_or_exposure` (inalterado) |
| concorrência | Dado o primeiro read vazio de idempotência antes do commit vencedor e o mesmo pedido, quando o primeiro fluxo é liberado, então ambos retornam `200 confirmed`, com uma inspeção, scan, auditoria de sucesso e linha de idempotência. | `Concurrent_same_request_replays_when_idempotency_read_precedes_the_winner_commit` |
| concorrência | Dadas confirmações simultâneas do mesmo pedido, quando disputam o lock por empresa e arquivo, então só uma confirmação materializa efeitos duráveis. | `Concurrent_confirmation_with_the_same_request_creates_one_confirmed_result` (inalterado) |
| concorrência | Dadas confirmações simultâneas com pedidos distintos, quando uma já confirma o arquivo, então a outra preserva o conflito de negócio e não reinspeciona. | `Concurrent_confirmations_with_distinct_request_ids_inspect_once_and_replay_their_results` (inalterado) |

Não há migration, mudança de contrato HTTP, polling, retry ou mudança de
isolamento nesta fatia.
