# Sessão mockada do protótipo

## Objetivo e público

Esta é uma entrada interna de desenvolvimento para validar a rota protegida do
protótipo. Destina-se apenas à equipe VINX; não é login de produção nem deve
ser apresentada a clientes.

## Uso permitido

O mock não é a composição padrão da aplicação e não deve ser usado para
validar release. `AppProviders`, usado por `main.tsx`, usa BFF real. Teste ou
ambiente de desenvolvimento só o habilita por injeção explícita de
`sessionGateway` em `AppProviders`.

Com a injeção ativa, o perfil `maria` permite verificar a Home com Alfa e Beta
e a saída retorna a `/login`. Preferências visuais continuam independentes de
sessão, autorização e empresa.

## Resultado esperado

A Home mostra somente as empresas autorizadas da fixture. Gama e identificadores
 técnicos de capability não aparecem. Não há empresa ativa, contador de não
lidas, token, armazenamento local de sessão/dados autenticados, chamada de
domínio ou acesso a API.
Os controles de aparência não autenticam, não autorizam e não criam um
seletor global de empresa.

Para uma checagem de acessibilidade, repita o fluxo somente com teclado,
inclusive com a janela em 320 CSS px e com zoom do navegador em 200%. O menu
de preferências deve abrir pelo botão nomeado e manter foco e nomes acessíveis.

## Próxima ação

Use este fluxo somente para desenvolvimento da interface. Para orientações de
navegação voltadas a quem usa o produto, consulte
`docs/manuals/frontend/app-shell-home.md`. O cliente, bootstrap e composição
real estão documentados em `docs/manuals/frontend/foundation-bootstrap.md` e
`docs/manuals/frontend/browser-bff-transport.md`.

Esta sessão é uma mock de desenvolvimento não persistente: cada instância do
navegador cria seu próprio estado em memória e o descarta ao recarregar. As
preferências visuais podem permanecer no armazenamento local, sem persistir
sessão nem dados autenticados.
