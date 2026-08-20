# Casos públicos — issue 95, UI da inbox de notificações e contador não lido

## Escopo desta fatia

UI consumidora do contrato #94: rota `/notificacoes` no catálogo e no
manifesto, ícone de notificações com contador de não lidas no header, lista
paginada com empresa de origem resolvida pelo bootstrap, marcação de leitura
confirmada pelo servidor, estados loading/empty/error/retry e manual em
português. Sem polling, realtime, push, WhatsApp ou dados simulados; erro de
rede/API nunca reduz o contador nem marca item visualmente sem confirmação.

| Classe BDD | Cenário | Evidência pública | Situação |
|---|---|---|---|
| sucesso | Dado notificações não lidas autorizadas, quando abre `/notificacoes`, então a lista mostra cada item com a empresa de origem e o estado de leitura | `notifications-page.test.tsx > lists authorized notifications with the company of origin` | coberto |
| sucesso | Dado item não lido, quando clica em "Marcar como lida", então a UI confirma o resultado da API antes de renderizar como lida | `notifications-page.test.tsx > confirms the server result before rendering the item as read` | coberto |
| sucesso | Dado sessão válida, quando o provider monta, então o contador busca `unread-count` e o header exibe o badge ("N não lidas") | `notifications-provider.test.tsx > loads the unread count after a valid bootstrap` | coberto |
| sucesso | Dado paginação com `nextCursor`, quando clica "Carregar mais", então itens novos são acrescidos sem duplicar ids | `notifications-client.test.tsx > fetches an inbox page with cursor and limit` + dedupe por id na página | coberto |
| validação | Dado resposta fora do envelope estrito, quando o cliente valida, então erro `protocol` sem renderizar payload | `notifications-client.test.tsx > rejects payloads outside the strict envelope` | coberto |
| validação | Dado erros HTTP tipados (400/401/404/429/503/rede), quando o cliente mapeia, então mensagem segura por código, sem detalhe interno | `notifications-client.test.tsx > maps http errors and network failures to typed errors` | coberto |
| validação | Dado marcação com CSRF de sessão cookie, quando o POST sai, então header `X-CSRF-TOKEN` com token fresco da rota antiforgery | `notifications-client.test.tsx > marks a notification as read with an antiforgery token` | coberto |
| erro | Dado falha na marcação, quando o POST falha, então o item permanece visivelmente não lido com retry manual ("Repetir") e sem mensagem de falso sucesso (BDD #95) | `notifications-page.test.tsx > keeps the item visibly unread and offers retry when the read fails` | coberto |
| erro | Dado falha do contador, quando a busca falha, então o valor anterior é preservado — nunca zerado/apagado por erro (BDD #95) | `notifications-provider.test.tsx > keeps the previous count when the request fails` | coberto |
| erro | Dado indisponibilidade da API, quando a página carrega, então estado de erro com "Tentar novamente" e empty state seguro após sucesso | `notifications-page.test.tsx > shows loading, empty and error states safely` | coberto |
| estado | Dado sessão encerrada/bootstrap nulo, quando o provider reage, então o contador reseta (sem contador da sessão anterior pós-logout) | `notifications-provider.test.tsx > resets the counter when the session ends` | coberto |
| acesso | Dado ação `action.href`, quando a UI navega, então somente href da allowlist local (`/paguai`, `/atendimento`) vira RouterLink interno; JSON bruto nunca vira URL arbitrária | `notifications-page.tsx` (filtro `allowlistedHrefs`) + `lists authorized notifications...` | coberto |
| acesso | Dado item de empresa, quando renderiza, então o nome vem do bootstrap autorizado; a resposta da API não carrega nome de empresa | `notifications-page.tsx > companyNameFor` + contrato #94 sem `companyName` | coberto |
| apresentação | Dado teclado/foco/aria, quando navega por Tab, então botões com aria-label descritivos ("Marcar notificação da empresa X como lida", "Notificações: N não lidas") e foco visível do tema | `notifications-page.test.tsx` (queries por role/name) + `app-header.tsx` badge | coberto |
| apresentação | Dado manual do cliente, quando abre Ajuda, então seção Notificações explica leitura, empresa de origem e próximo passo | `apps/web/src/app/help-page.tsx` seção "Notificações" | coberto |

## Limites mantidos

Sem polling/realtime/push (contador em mount, focus, pós-marcação e rota);
marcação sem otimismo — estado visual muda somente após confirmação da API;
erro preserva contador e estado não lido; navegação somente por href
allowlistado; sem dados simulados; sem alteração no contrato #94, worker ou
provider. Playwright E2E fica para o corte de orquestração Luna da #95,
contra o contrato congelado.
