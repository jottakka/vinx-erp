# Transporte Browser BFF

O web chama somente as três operações BFF de mesma origem em `/foundation`:
antiforgery, login e logout. O módulo
`apps/web/src/features/auth/browser-session-client.ts` não aceita URL ou header
do consumidor; usa `fetch` nativo com cookies `same-origin`, sem cache, redirect
ou repetição automática.

`signIn` e `signOut` recebem um intento explícito da interface. Cada chamada
busca um `requestToken` antiforgery transitório e, se ele for válido, faz uma
única mutação com `X-CSRF-TOKEN`. O token não entra em storage, estado global,
log ou retry. Falha, cancelamento ou reconexão não reenvia a mutação; uma nova
tentativa precisa partir de nova ação visível da pessoa.

O resultado é discriminado por `ok`. Em falha, contém somente categoria,
status, código documentado e `X-Correlation-ID` UUID canônico quando presente.
Texto bruto, `statusText`, URL, stack e campos desconhecidos do servidor são
descartados. O cliente não chama Supabase, Storage, PostgreSQL ou provider.

O proxy Vite é exclusivo de desenvolvimento: `VINX_API_ORIGIN` aceita apenas
`http://127.0.0.1:<porta>` e encaminha `/foundation`. Não use `VITE_*` para
esta origem. Em preview e produção, o reverse proxy/ingress da mesma origem é
responsabilidade do deployment.

Bootstrap, TanStack Query e a integração da tela/sessão real não pertencem a
este módulo: #90 e #117 os consomem depois.
