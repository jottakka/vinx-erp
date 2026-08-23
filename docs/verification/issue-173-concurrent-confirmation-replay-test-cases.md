# Casos BDD — replay determinístico da confirmação concorrente (#173)

Esta matriz preserva o contrato de confirmação documental: o estado, a
idempotência e a auditoria persistidos são a autoridade; storage e scanner só
executam uma vez para a confirmação vencedora.

| Classe BDD | Dado / quando / então | Testes públicos |
| --- | --- | --- |
| sucesso | Dado documento em quarentena e confirmação válida, quando a confirmação conclui, então retorna o arquivo confirmado. | `Valid_confirmation_rechecks_private_storage_and_confirms_the_file` |
| validação/erro | Dado o mesmo `RequestId` com outro arquivo, quando a segunda confirmação chega, então nega com `idempotency_conflict`. | `Confirmation_with_same_request_id_and_different_file_returns_idempotency_conflict` |
| acesso | Dado autorização removida durante a inspeção privada, quando a finalização ocorre, então reautoriza e nega sem mudar o estado. | `Confirmation_reauthorizes_after_private_inspection_finishes` (inalterado, coberto por nome) |
| estado/exceção | Dado confirmação já confirmada com o mesmo pedido, quando é repetida, então devolve o resultado durável sem nova auditoria ou exposição. | `Repeated_confirmation_replays_confirmed_state_without_a_second_audit_or_exposure` (inalterado, coberto por nome) |
| concorrência | Dado o primeiro read vazio de idempotência antes do commit vencedor e o mesmo pedido, quando o primeiro fluxo é liberado, então ambos retornam `200 confirmed`, com uma inspeção, scan, auditoria de sucesso e linha de idempotência. | `Concurrent_same_request_replays_when_idempotency_read_precedes_the_winner_commit` |
| concorrência | Dadas confirmações simultâneas do mesmo pedido, quando disputam o lock por empresa e arquivo, então só uma confirmação materializa efeitos duráveis. | `Concurrent_confirmation_with_the_same_request_creates_one_confirmed_result` (inalterado, coberto por nome) |
| concorrência | Dadas confirmações simultâneas com pedidos distintos, quando uma já confirma o arquivo, então a outra preserva o conflito de negócio e não reinspeciona. | `Concurrent_confirmations_with_distinct_request_ids_inspect_once_and_replay_their_results` (inalterado, coberto por nome) |

Não há migration, mudança de contrato HTTP, polling, retry ou mudança de
isolamento nesta fatia.
