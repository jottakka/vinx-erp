# Plano: sessão real no frontend (#117)

## Objetivo

Compor o login, logout e bootstrap publicados pelo BFF C# no caminho de
release, mantendo a autorização e a revogação no servidor e a projeção de
sessão somente em memória no navegador.

## Limites e decisões

- Usar exclusivamente `BrowserSessionClient` e `FoundationBootstrapClient` da
  mesma origem; não adicionar SDK, URL configurável, provider ou token no
  navegador.
- O mock fica disponível somente por injeção explícita para teste ou ambiente
  de desenvolvimento. `main.tsx` usa a composição real.
- Login só publica conteúdo protegido após `204` do BFF e bootstrap válido.
  `401` limpa a projeção; indisponibilidade mantém a rota bloqueada com nova
  tentativa manual.
- O resultado de `signOut` distingue a falha da obtenção antiforgery daquela
  ocorrida após iniciar `DELETE`. Só `204`, `DELETE 401` ou `DELETE 503`
  `identity_gate_unavailable` concluem estado anônimo. Toda outra falha limpa
  a projeção e o cache, bloqueia a rota em recuperação e exige nova ação
  explícita de saída; reconexão nunca a repete.
- A geração monotônica e o abortamento de #90 continuam sendo a autoridade
  para corridas entre login, logout, atualização e navegação.

**Estimativa:** 11–15 arquivos não teste e 450–650 linhas manuais, incluindo
formulário, composição, testes focados, Playwright com transporte BFF simulado,
manual e matriz BDD. Não há API C#, migração, lockfile ou novo provider; a
fatia é coesa porque a UI não pode usar a sessão real sem a composição e seus
limites de estado.

## BDD e verificação

| Classe | Dado / Quando / Então | Evidência planejada |
|---|---|---|
| sucesso | Dada sessão BFF válida, quando o app inicia ou conclui login, então bootstrap válido libera somente contextos autorizados | Vitest + Playwright com BFF simulado |
| validação/erro | Dado login inválido, sessão expirada ou resposta indisponível, quando o fluxo termina, então a interface oferece orientação segura sem diagnóstico do provider | Vitest |
| acesso | Dada revogação ou `401`, quando a rota protegida revalida, então snapshot e cache são removidos antes de conteúdo protegido | Vitest |
| estado/exceção | Dado logout confirmado ou `503` posterior à tentativa, quando o BFF responde, então a projeção local é removida; outra falha não inventa logout | Vitest |
| concorrência | Dados login, logout, atualização e navegação sobrepostos, quando respostas antigas chegam por último, então não restauram acesso obsoleto | Vitest |

## Task 1 — composição e comportamento de sessão

1. Escrever REDs do fluxo real, logout e corrida; preservar os testes do
   transporte como limite de rede.
2. Compor clientes reais em `AppProviders`, deixando mock apenas como injeção
   explícita, e adaptar `SessionProvider` para os dois modos.
3. Substituir a tela de perfil local por formulário de email e senha e ajustar
   a rota protegida para estados seguros.
## Task 2 — jornada, documentação e publicação

1. Cobrir a jornada de navegador sem provider direto, atualizar manual e
   manifesto BDD; revisar, congelar, executar o gate público uma vez e
   publicar a filha draft da #225.

## Adiamentos

MFA/TOTP e recência, refresh, recuperação de conta, logout por dispositivo,
cache offline, service worker, fila/replay, armazenamento persistente e ajuda
in-app ficam nas respectivas fatias #44, #79 e #91.
