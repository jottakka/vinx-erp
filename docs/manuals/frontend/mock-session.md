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
3. Confirme a entrada **Protótipo autenticado** e as empresas Alfa e Beta.
4. Para encerrar, escolha **Sair do protótipo**.

Após sair, a aplicação retorna a `/login`. Uma recarga também remove a sessão;
a próxima navegação protegida pede entrada novamente.

## Resultado esperado

O protótipo mostra somente as empresas autorizadas da fixture e suas
capabilities efetivas. Gama não aparece. Não há empresa ativa, contador de
não lidas, token, armazenamento local, chamada de domínio ou acesso a API.

## Erros frequentes

- **Perfil de desenvolvimento indisponível:** use o identificador `maria`.
- **Rota protegida volta ao login:** esperado quando não existe sessão em
  memória ou depois de recarregar a página.
- **A aplicação não inicia:** execute a partir da raiz do repositório e
  confirme as dependências com `npm ci`.

## Próxima ação

Use este fluxo somente para desenvolvimento da interface. A prova de
autenticação real com API e banco pertence às issues #114 e #115.

Esta sessão é uma mock de desenvolvimento não persistente: cada instância do
navegador cria seu próprio estado em memória e o descarta ao recarregar.
