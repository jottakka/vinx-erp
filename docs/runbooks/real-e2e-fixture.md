# Fixture E2E real descartável

## Objetivo

Este fluxo sobe, por um comando, um PostgreSQL descartável, o ApiHost real em
Kestrel e o web em Vite. O dataset contém pessoas, empresas, cargos e registros
dos módulos necessários para inspeção manual. Ele existe somente em projetos de
teste e não altera migrations, seed de produção ou Supabase local.

## Pré-requisitos

- Daemon Docker compatível com Testcontainers, disponível no contexto já
  selecionado.
- Node.js e .NET nas versões travadas pelo repositório.
- Dependências instaladas com `npm ci` e `dotnet restore --locked-mode`.
- Porta `4174` livre.

Antes de iniciar, atribua containers e processos VINX já ativos. Não interrompa
recursos de outra worktree nem a stack Supabase. O launcher cria e remove apenas
os grupos de processo e o Testcontainer desta execução.

## Iniciar

Na raiz do repositório:

```bash
npm run dev:real-e2e -- --profile maria
```

Profiles aceitos: `maria`, `beatriz`, `joao` e `clara`. O profile é resolvido
antes de criar o banco e não pode ser trocado por header, cookie, query string
ou interface web.

Depois do readback completo, o comando publica somente:

```text
E2E_BROWSER_URL=http://127.0.0.1:4174
E2E_PROFILE=maria
```

Abra a URL publicada. A porta da API é dinâmica e permanece interna ao
launcher. `VINX_API_ORIGIN` é entregue somente ao processo Vite filho.

## Dataset

- Empresas Alfa, Beta e Gama no mesmo grupo.
- Maria participa de Alfa e Beta; Beatriz, somente de Alfa; João, somente de
  Beta; Clara, somente de Gama.
- Unidades, equipes, cargos, assignments e capabilities distintos por empresa.
- Employees em cada empresa, quatro rascunhos Paguai, cinco cards Atendimento e
  notificações entregues, incluindo uma já lida.

O mesmo seed determinístico é usado em todos os profiles. A identidade ativa
muda, mas o recibo do dataset não contém identidade selecionada. Reexecutar o
seed no mesmo banco falha fechado; não faz merge com estado anterior.

## Roteiro manual mínimo

1. Em `/`, confirme as empresas visíveis para o profile selecionado.
2. Em `/pessoas`, alterne somente entre empresas autorizadas e confira as
   pessoas de cada empresa.
3. Em `/paguai`, confira os rascunhos da empresa selecionada.
4. Em `/atendimento` e `/kanban`, confira cards nos estados esperados.
5. Em `/notificacoes`, confira itens lidos e não lidos.
6. Tente acessar Gama com um profile que não participa dela; a API deve negar ou
   ocultar o recurso conforme o contrato, nunca mostrar os dados.

O manifesto em
`docs/verification/e2e-real-fixtures-test-cases.md` separa evidência automática
e inspeção manual.

## Encerrar e conferir cleanup

Use `Ctrl+C` no terminal do launcher e aguarde o processo sair. O shutdown
encerra e aguarda Vite, depois ApiHost e PostgreSQL. As portas `4174` e dinâmica
da API devem fechar, e o Testcontainer próprio deve desaparecer. Não use
`pkill`, remoção por porta nem prune amplo.

Se o terminal foi fechado de forma abrupta, atribua primeiro o PID, a worktree
e os labels do container antes de encerrar qualquer recurso. Preserve tudo que
tiver dono ambíguo.

## Diagnóstico seguro

Falhas do host expõem somente uma destas categorias:

- `fixture_start_failed`
- `seed_failed`
- `host_start_failed`
- `readback_failed`

O launcher rejeita readiness inválida, timeout, profile desconhecido e porta
ocupada com saída não zero. Ele não imprime connection string, senha, claims,
e-mail, IDs do dataset, payloads ou mensagens internas de provider.

## Limites de evidência

Este fluxo prova migrations, seed, autorização/RLS, session gate de teste,
ApiHost real, proxy Vite e leitura dos módulos contra PostgreSQL real. Não prova
login Supabase, MFA, provider externo, WorkerHost, Meta, ClamAV, AI, Storage,
TLS/ingress, produção, reconnect ou revogação de sessão. A automação browser
real completa permanece no escopo de #115.
