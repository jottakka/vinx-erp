# Casos BDD — ingresso local do convite inicial

## Escopo

Esta matriz cobre somente o consumo interno de
`OwnerInvitationDeliveryRequested`. O worker não chama provider, não envia
e-mail, não cria token e não aceita convite. O evento e a DLQ nunca retêm nome,
e-mail, segredo ou corpo HTTP.

| Classe BDD | Dado / quando / então | Resultado exigido |
|---|---|---|
| transição | Dado convite `pending_delivery` e envelope válido, quando o worker consome a mensagem, então chama a função SQL de ingresso. | Convite muda para `pending_provider`, uma auditoria `owner_invitation.delivery.ingress` é gravada e a correlação do envelope é preservada. |
| replay e concorrência | Dadas duas instâncias do WorkerHost e a mesma mensagem, quando disputam o consumo, então a função serializa a transição. | Há no máximo uma transição e uma auditoria; replay não duplica evidência. |
| isolamento | Dado runtime do worker, quando tenta ler envelopes do produtor ou alterar tabelas Foundation diretamente, então o PostgreSQL nega. | O worker usa somente seu schema de envelopes, a fila compartilhada necessária e a função `SECURITY DEFINER` estreita. |
| preflight e DDL | Dada conexão sem `session_user` e `current_user` iguais a `vinx_worker`, quando o WorkerHost inicia, então falha antes do listener. | Runtime não cria DDL, não recebe ownership e mantém apenas `TEMPORARY` necessário ao listener PostgreSQL. |
| retry transitório | Dada falha PostgreSQL transitória, quando o worker a recebe, então agenda tentativas finitas. | Depois do esgotamento, o convite permanece `pending_delivery`, não há auditoria de ingresso e a mensagem vai para a DLQ sanitizada. |
| falha permanente ou inesperada | Dada entrada inválida, inconsistência ou falha inesperada de infraestrutura, quando a tentativa termina, então não há reprocessamento cego. | Convite e auditoria permanecem inalterados; a DLQ usa tipo e mensagem seguros. |
| privacidade da DLQ | Dada mensagem encaminhada para a DLQ, quando seu envelope é persistido, então o worker redige o conteúdo antes do armazenamento. | Corpo contém somente `invitationId` zerado, headers estão vazios e não há payload original, UUID original, dados pessoais, segredo ou exceção interna. |

## Limites adiados

- Adapter e reconciliação do Supabase Admin pertencem à #211B.
- Entrega de WhatsApp, Meta, opt-in, template e webhook pertencem à #28.
- O estado `pending_provider` significa apenas ingresso interno confirmado; não
  declara envio ou aceitação externa.
