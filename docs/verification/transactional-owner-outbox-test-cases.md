# Casos BDD — outbox transacional do convite inicial

## Escopo

Esta matriz cobre a persistência do único evento
`OwnerInvitationDeliveryRequested` durante o provisionamento inicial de
organização. O evento contém exclusivamente o UUID opaco `invitationId`;
nome, e-mail, token, payload, hash e segredo não podem aparecer no evento,
metadados do envelope, logs ou manifesto.

| Classe BDD | Dado / quando / então | Resultado exigido |
|---|---|---|
| sucesso e atomicidade | Dado operador global com `aal2`, quando cria organização com request novo, então negócio, recibo, auditoria e envelope são confirmados na mesma transação. | Retorna `201`; existe uma única mensagem para o convite criado e o corpo JSON contém somente `invitationId`. |
| replay | Dado o mesmo ator, request e hash canônico já confirmados, quando repete a criação, então não publica novamente. | Retorna replay com os mesmos IDs e permanece exatamente um envelope. |
| autorização | Dado ator sem operador global ou operador com `aal1`, quando chama a rota, então a autorização falha antes da mutação. | Retorna o `403` específico, não cria organização, recibo, auditoria de sucesso nem mensagem. |
| falha pré-commit | Dada falha ao persistir o envelope antes do commit, quando a criação é executada, então a transação inteira é desfeita. | Retorna indisponibilidade sem grupo, empresa, convite, recibo, auditoria de sucesso ou envelope residual. |
| commit ambíguo | Dada confirmação de commit perdida após a gravação física, quando a chamada falha, então o serviço não compensa, reenvia ou faz flush. | Retorna indisponibilidade; a repetição segura identifica replay e conserva uma única mensagem. |
| flush pós-commit | Dada falha ao encaminhar o envelope depois de commit confirmado, quando a criação termina, então não desfaz o negócio. | Retorna criação com convite `pending_delivery` e envelope durável para o relay posterior. |
| startup sem storage | Dado storage técnico ainda não provisionado, quando ApiHost produtor ou WorkerHost iniciam, então não executam DDL implícito. | O startup falha fechado e não cria fila, schemas ou tabelas técnicas. O deploy provisiona os dois hosts antes do runtime. |
| privilégio de runtime | Dado runtime com grants mínimos, quando tenta criar ou alterar storage Wolverine, então o PostgreSQL nega a operação. | Runtime não recebe ownership nem DDL; deploy executa `db-apply`, grants e `db-assert` com identidade migradora. |
| privacidade e opacidade | Dado envelope persistido, quando é lido para verificação, então payload e metadados não expõem dados pessoais ou segredos. | Evento e JSON contém somente `invitationId` opaco; metadados identificam o tipo sem request, payload ou dados do owner. |
| concorrência | Dadas requisições concorrentes com o mesmo ator e request, quando a função serializa a idempotência, então não há duplicação. | Há no máximo um resultado `created`; as concorrentes perdedoras recebem `replay` 200 ou falha fechada 409/503 sem estado parcial, e a repetição posterior do mesmo request converge em `replay` com uma única mensagem. |

## Limites adiados

- O ingresso local, listener, retry e DLQ pertencem à primeira fatia da #48 e
  são verificados em `owner-invitation-worker-ingress-test-cases.md`.
- Entrega e reconciliação pelo provider Supabase pertencem à #211B.
- WhatsApp, Meta, opt-in, template e webhook pertencem à #28.

O outbox e a deduplicação interna não prometem exactly-once externo.
