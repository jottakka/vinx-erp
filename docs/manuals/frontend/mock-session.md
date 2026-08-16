# Sessão mockada do protótipo

## Objetivo e público

Esta é uma entrada interna de desenvolvimento para validar a rota protegida do
protótipo. Destina-se apenas à equipe VINX; não é login de produção nem deve
ser apresentada a clientes.

## Pré-requisitos

- Dependências do repositório instaladas.
- Aplicação web iniciada com `npm --workspace apps/web run dev`.

## Entrar e sair

1. Abra a aplicação e siga para `/login`.
2. Mantenha o perfil de desenvolvimento `maria` e escolha **Entrar**.
3. Antes de entrar, abra **Abrir preferências visuais** e escolha **Tema**
   (sistema, claro ou escuro) e **Densidade** (confortável ou compacta). Essas
   preferências são somente visuais e podem ser restauradas pelo navegador;
   não alteram sessão, autorização ou empresa.
4. Confirme a página **Início**, o escopo **Todas as empresas autorizadas** e as empresas Alfa e Beta.
5. Para encerrar, escolha **Sair** no cabeçalho.

Após sair, a aplicação retorna a `/login`. Uma recarga também remove a sessão;
a próxima navegação protegida pede entrada novamente.

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

## Erros frequentes

- **Perfil de desenvolvimento indisponível:** use o identificador `maria`.
- **Rota protegida volta ao login:** esperado quando não existe sessão em
  memória ou depois de recarregar a página.
- **A aplicação não inicia:** execute a partir da raiz do repositório e
  confirme as dependências com `npm ci`.

## Próxima ação

Use este fluxo somente para desenvolvimento da interface. Para orientações de navegação voltadas a quem usa o produto, consulte
`docs/manuals/frontend/app-shell-home.md`. O cliente e estado do bootstrap
autenticado estão documentados em `docs/manuals/frontend/foundation-bootstrap.md`;
a composição da sessão BFF real no caminho de release pertence a #117.

Esta sessão é uma mock de desenvolvimento não persistente: cada instância do
navegador cria seu próprio estado em memória e o descarta ao recarregar. As
preferências visuais podem permanecer no armazenamento local, sem persistir
sessão nem dados autenticados.
