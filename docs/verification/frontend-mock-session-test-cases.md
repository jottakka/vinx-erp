# Casos de verificação: sessão mockada do frontend

## Limite da evidência

Esta fatia prova somente a sessão mockada local, a entrada e a proteção de
rotas do protótipo. Ela não prova autenticação por API, banco, RLS, provider,
token ou autorização de produção; essas evidências pertencem às issues #114 e
#115.

| Classe BDD | Cenário em inglês | Unitário/componente | Playwright | Manual |
|---|---|---|---|---|
| happy | User enters the prototype with an authorized mock profile | `mock-session-gateway.test.ts`: fixture fonte Alfa/Beta/Gama e projeção somente autorizada; `login-page.test.tsx`: entrada bem-sucedida | `mock-session.spec.ts`: enters the prototype with only authorized mock companies | Entrar com `maria` e confirmar Alfa/Beta |
| validation | Invalid mock profile is actionable | `mock-session-gateway.test.ts`: perfil ausente; `login-page.test.tsx`: erro acionável e rota pública | Não aplicável: erro é coberto no componente sem backend | Informar perfil diferente de `maria` e confirmar mensagem |
| access | Anonymous user cannot open a protected route | `protected-route.test.tsx`: deep link anônimo redireciona | `mock-session.spec.ts`: redirects an anonymous protected deep link to login | Abrir `/protected` sem sessão |
| state | Reload and logout remove the mock session | `mock-session-gateway.test.ts`: logout; `session-provider.test.tsx`: provider novo anônimo; `protected-route.test.tsx`: logout | `mock-session.spec.ts`: logout removes access; reload returns to login | Sair e recarregar depois da entrada |
| concurrency | Repeated sign-in and sign-out keep one deterministic session | `mock-session-gateway.test.ts`: login/logout sobrepostos por promessas adiadas; logout conclui primeiro e o login que conclui por último deixa Maria autenticada. `login-page.test.tsx`: login adiado com dois cliques chama `router.navigate("/protected")` uma vez e o subscriber do router observa uma transição protegida. | Não aplicável: ordem de conclusão e transição única são controladas no componente sem backend | Clique repetido permanece bloqueado enquanto a ação está pendente |

Os testes usam fixture em memória. Nenhum cenário abre requisição de domínio ou
persiste dados autenticados.
