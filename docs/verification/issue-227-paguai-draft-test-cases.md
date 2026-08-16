# Casos BDD — issue 227, Paguai MVP de rascunho

| Classe BDD | Dado / quando / então | Evidência | Resultado esperado |
| --- | --- | --- | --- |
| criação | Dado membership e capability de criação, quando envia metadados válidos, então cria o rascunho | `PaguaiDraftEndpointTests.Draft_creation_returns_a_company_scoped_rascunho` | `201`, `Location`, `rascunho`, versão `1` |
| idempotência | Dado o mesmo `requestId`, quando repete o mesmo corpo ou muda o título, então reobtém ou conflita | `PaguaiDraftEndpointTests.Replaying_same_payload_returns_same_draft_and_different_payload_conflicts` | `200` mesmo ID, sem duplicata; `409` no hash diferente |
| visibilidade | Dado rascunho próprio, quando lista/detalha na empresa autorizada ou em empresa externa, então aplica escopo | `PaguaiDraftEndpointTests.Draft_reads_are_visible_to_member_company_only` | leitura própria `200`; detalhe externo `404`; lista externa `403` sem conteúdo |
| schema/RLS/grants | Dado banco migrado, quando runtime acessa `paguai.requests` sem contexto ou tenta mutação indevida, então o banco nega | verificação manual pública antes do PR | RLS forçado; somente `USAGE`, `SELECT`, `INSERT`; sem acesso PUBLIC |
| concorrência | Dadas duas criações simultâneas com o mesmo corpo, quando uma vence a chave idempotente, então a outra reconcilia após autorizar | verificação manual pública antes do PR | uma criação e um replay, uma só auditoria de sucesso |
| auditoria | Dada falha de auditoria, quando cria rascunho, então toda a transação reverte | verificação manual pública antes do PR | sem solicitação e sem recibo persistidos |

Verificação manual da UI: abrir `/paguai` com sessão real; o seletor oferece
somente empresas com capability Paguai, a lista vem da API same-origin, e a
criação mantém o `requestId` em caso de falha ambígua para retry explícito.
