# Mapa de verificação: entrega pelo provider do convite inicial

| Classe | Caso | Evidência |
| --- | --- | --- |
| Cliente Supabase | O worker envia somente `email` no corpo e `redirect_to` na query de `POST /invite`, com credenciais no servidor e redirects bloqueados. | `SupabaseAdminOwnerInvitationClientTests` |
| Cliente Supabase | 4xx é rejeição definitiva; timeout, 5xx, redirect, JSON inválido ou resposta acima do limite ficam ambíguos, sem corpo do provider no resultado. | `SupabaseAdminOwnerInvitationClientTests` |
| Função SQL | Apenas o vencedor da reivindicação lê o e-mail; replay recebe o mesmo `attemptId` sem e-mail. | `OwnerInvitationProviderDeliveryFunctionTests` |
| Função SQL | Sucesso, falha e ambiguidade gravam somente o estado permitido, uma auditoria e nenhuma repetição de finalização. | `OwnerInvitationProviderDeliveryFunctionTests` |
| Worker | O convite é enviado uma vez, preserva somente `issuer` + `subject` no sucesso e conserva auditorias one-shot. | `WorkerHostIngressTests` |
| Worker | A configuração inválida falha antes do listener; erros e DLQ continuam sem payload, segredo ou dados pessoais. | `WorkerHostIngressTests`, `WorkerHostSecurityContractTests` |

O estado `provider_ambiguous` não dispara retry externo. A próxima fatia decide
a reconciliação e o aceite autenticado; ela não pode reutilizar e-mail como
identidade nem criar uma segunda membership, role ou convite.
