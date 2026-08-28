# Casos BDD — transporte Browser BFF

## Escopo

O transporte chama somente antiforgery, login e logout pela mesma origem. Não
monta bootstrap, não troca a tela e não persiste token ou sessão.

Jornada Playwright: a composição de #117 é mapeada em
`frontend-real-session-test-cases.md`; este documento preserva somente o
limite unitário do transporte.

| Classe BDD | Dado / quando / então | Evidência pública | Resultado esperado |
|---|---|---|---|
| sucesso | Dado intento explícito de login ou logout, quando há CSRF válido, então o token antecede uma única mutação | `browser-session-client.test.ts` — login/logout | caminho relativo, cookie same-origin, `204` e UUID de correlação quando válido |
| validação/erro | Dado token, corpo, correlação ou resposta inválidos, quando chegam, então a falha é segura | `browser-session-client.test.ts` — token/correlação/erros | sem texto bruto; somente categoria, status, código permitido e UUID válido |
| acesso | Dado módulo web, quando chama BFF, então não transporta autorização, correlação ou provider | `browser-session-client.test.ts` — headers; `npm run test:no-direct-supabase` | somente headers BFF permitidos e nenhuma importação/provider direto |
| estado/exceção | Dado CSRF falho, `204`, cancelamento ou rede indisponível, quando a operação termina, então não há parse/replay indevido | `browser-session-client.test.ts` — falha/abort/retry | nenhuma mutação após CSRF falho ou abort já solicitado; uma tentativa por chamada |
| concorrência | Dados logins paralelos, quando tokens são emitidos, então cada mutação usa seu próprio token | `browser-session-client.test.ts` — concorrência | nenhum token ou resultado compartilhado |
| transporte Atendimento | Dada mutação de cartão, quando há sessão BFF, então antiforgery antecede cada `POST` | `atendimento-client.test.ts` | token transitório em `X-CSRF-TOKEN`; falha do token não envia mutação |
| proxy local | Dada configuração Vite, quando `VINX_API_ORIGIN` é loopback permitido, então os subcaminhos BFF ativos de Agent, Atendimento, Demandas, Foundation, Notifications e Paguai recebem proxy de desenvolvimento | `vite-api-proxy.test.ts` | destino `127.0.0.1` exato; rotas SPA fora do proxy; preview/produção sem proxy |

## Limites mantidos

O ingress same-origin de produção e refresh/MFA/recuperação pertencem às
fatias posteriores. Nova tentativa é decisão explícita da interface; o
transporte nunca repete mutação automaticamente.
