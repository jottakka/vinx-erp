# Casos de verificação: sessão mockada do frontend

## Limite da evidência

Esta matriz prova somente `MockSessionGateway` injetado em unitário/componente.
Ela não prova autenticação por API, BFF, banco, RLS, provider, token ou
autorização de produção. Playwright de release usa BFF simulado, inclusive onde
o arquivo mantém o nome legado `mock-session.spec.ts`.

| Classe BDD | Cenário em inglês | Unitário/componente | Playwright | Manual |
|---|---|---|---|---|
| happy | User enters the prototype with an authorized mock profile | `mock-session-gateway.test.ts`: fixture fonte Alfa/Beta/Gama e projeção somente autorizada; `login-page.test.tsx`: entrada bem-sucedida | Não aplicável: release usa BFF simulado | Com injeção explícita, entrar com `maria` e confirmar Alfa/Beta |
| validation | Invalid mock profile is actionable | `mock-session-gateway.test.ts`: perfil ausente; `login-page.test.tsx`: erro acionável e rota pública | Não aplicável: erro é coberto no componente sem backend | Com injeção explícita, informar perfil diferente de `maria` |
| access | Anonymous user cannot open a protected route | `protected-route.test.tsx`: deep link anônimo redireciona | Não aplicável: limite coberto por componente | Com injeção explícita, abrir `/protected` sem sessão |
| state | Provider mock novo começa anônimo; logout remove sessão mock | `mock-session-gateway.test.ts`: logout; `session-provider.test.tsx`: provider novo anônimo; `protected-route.test.tsx`: logout | Não aplicável: reload release segue contrato BFF | Com injeção explícita, sair e criar provider novo |
| concurrency | Repeated sign-in and sign-out keep one deterministic session | `mock-session-gateway.test.ts`: login/logout sobrepostos por promessas adiadas; logout conclui primeiro e o login que conclui por último deixa Maria autenticada. `login-page.test.tsx`: login adiado com dois cliques chama `router.navigate("/protected")` uma vez e o subscriber do router observa uma transição protegida. | Não aplicável: ordem de conclusão e transição única são controladas no componente sem backend | Clique repetido permanece bloqueado enquanto a ação está pendente |

Os testes usam fixture em memória injetada. Nenhum cenário abre requisição de
domínio ou persiste dados autenticados.
