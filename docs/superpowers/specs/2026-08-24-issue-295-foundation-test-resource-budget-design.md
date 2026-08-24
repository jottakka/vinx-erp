# Orçamento de Testcontainers da integração Foundation — issue 295

## Status

Desenho aprovado para limitar o paralelismo por processo e selecionar testes
locais conforme o risco da mudança. A issue #295 mantém a evidência causal da
reprodução; este documento registra somente decisões duráveis.

## Objetivo

Impedir que o projeto `Vinx.Erp.Foundation.IntegrationTests` transforme todos
os processadores visíveis em PostgreSQL descartáveis simultâneos. O teste
continua usando PostgreSQL real e isolamento por fixture, mas cada processo
executa no máximo duas collections xUnit em paralelo.

Evitar gates pesados redundantes: desenvolvimento usa o menor teste que prova o
comportamento alterado; a verificação local completa fica reservada a fronteiras
compartilhadas e momentos finais, enquanto o CI continua como gate completo da
PR.

## Causa confirmada

xUnit v2 cria, por padrão, uma collection por classe e usa
`Environment.ProcessorCount` como teto de paralelismo. O projeto possui muitas
classes que criam seu próprio `FoundationPostgresFixture` ou
`FoundationApiFactory`; portanto, o número de containers, bootstraps e
migrations concorrentes cresce com a quantidade de CPUs exposta pelo runtime.

O ciclo de vida existente não é a causa. `DisposeAsync` remove o container no
caminho normal e Ryuk remove recursos da sessão quando o test host encerra.
Adicionar descarte duplicado, retentativa ou reutilização não reduz o pico e
ampliaria o risco de estado compartilhado ou recurso órfão.

## Decisões

1. O assembly de integração declara
   `[assembly: CollectionBehavior(MaxParallelThreads = 2)]` em um arquivo de
   configuração C# próprio.
2. O teto é por processo: o xUnit agenda no máximo duas collections de teste
   simultâneas. Um teste pode criar mais de um fixture internamente, portanto o
   atributo não promete quantidade máxima universal de containers; não haverá
   lock, fila ou lease global entre processos.
3. O modo continua `CollectionPerClass`, com paralelismo habilitado. O algoritmo
   conservador padrão do xUnit permanece inalterado.
4. Cada fixture continua criando banco, usuário, senha e porta efêmeros próprios.
   Não há container compartilhado, `WithReuse`, nome ou porta fixa.
5. `FoundationPostgresFixture`, `FoundationApiFactory`, Ryuk, imagem PostgreSQL,
   timeouts e recursos do Docker não são alterados.

## Forma da implementação

`TestAssemblyConfiguration.cs` contém apenas o atributo de assembly. Um teste
público sem Docker lê o atributo por reflexão e fixa três garantias:

- existe uma única configuração `CollectionBehaviorAttribute`;
- `MaxParallelThreads` é exatamente `2`;
- `DisableTestParallelization` permanece `false`.

O atributo compilado foi escolhido em vez de `xunit.runner.json` porque não
exige regra de cópia para a saída, acompanha o assembly em qualquer runner e
permite um contrato automatizado direto. Não será criada abstração para um
único valor de configuração.

## Fluxo de execução

```text
dotnet test
  -> xUnit descobre collections por classe
  -> atributo do assembly libera até 2 collections
  -> testes da collection criam e descartam seus PostgreSQL isolados
  -> próxima collection ocupa uma vaga liberada
  -> Ryuk permanece como proteção de encerramento da sessão
```

O limite reduz o fan-out do runner de `N CPUs x M processos` para no máximo
`2 collections x M processos`. A quantidade de containers observada depende
dos fixtures criados internamente pelos testes. O limite não promete
exclusividade do daemon Docker e não coordena outros repositórios, Supabase
local ou comandos Docker manuais.

## Falhas e descarte

Falha de bootstrap, migração ou caso de teste preserva o ciclo de vida atual. O
fixture tenta descartar seu container e o encerramento do test host fecha a
sessão acompanhada por Ryuk. O PR não aumenta timeout nem converte falha de
infraestrutura em sucesso.

Ausência ou mudança do atributo falha o teste de contrato antes de precisar de
Docker. Docker indisponível continua falhando nos testes que exigem PostgreSQL
real, com o erro original.

## Operação local

O runbook registra que Docker Desktop e Colima podem manter VMs distintas. Se o
contexto efetivo for `desktop-linux`, o operador deve confirmar que nenhuma
outra atividade depende de Colima antes de executar `colima stop`. O repositório
não para nem reconfigura runtimes globais automaticamente.

## Política de seleção de testes

A política rege agentes e execuções locais. O workflow remoto continua rodando
a verificação pública completa em cada PR; esta fatia não adiciona seleção por
caminho no CI.

### Fases obrigatórias

1. Investigação e RED executam um método, classe, cenário ou comando mínimo.
   Não iniciam o gate completo, o projeto Foundation inteiro ou Supabase apenas
   para diagnosticar.
2. Implementação e GREEN repetem o mesmo foco e, depois, o projeto público
   diretamente afetado. Projeto explicitamente misto e pesado usa a fatia
   estável definida na matriz; o projeto completo fica para CI ou gatilho
   estratégico. Commits e comentários de revisão não disparam projetos sem
   relação com a mudança.
3. Correções executam os contratos afetados. Alteração somente documental usa
   `git diff --check` e contratos documentais pertinentes; mudança executável em
   dependência compartilhada amplia a verificação um nível.
4. Fechamento usa o CI como gate completo normal. O gate local completo roda no
   máximo uma vez, depois do congelamento de escopo, somente quando um gatilho
   estratégico se aplica.

### Matriz de impacto

| Mudança | Verificação local esperada |
|---|---|
| Markdown, `AGENTS.md`, runbook ou plano | `git diff --check`; contrato público apenas quando consumir o documento |
| React ou cliente web | Vitest afetado; no fechamento, lint, formato, testes, build e guarda no-direct-Supabase |
| Jornada ou rota de browser | Teste de componente primeiro; Playwright somente para a jornada afetada |
| Módulo AI | Classe unitária afetada; AI Integration apenas para composição, contexto ou boundary real |
| Foundation sem banco/HTTP | Classe afetada e projeto Foundation Unit |
| Foundation HTTP, persistência, módulo ou worker | Filtros afetados; projeto Foundation Integration completo no CI ou com gatilho estratégico |
| McpHost ou WorkerHost | Teste afetado e Hosts Integration; incluir consumidor direto quando necessário |
| BDD | Cenário/tag afetado e, no fechamento, projeto Foundation BDD |
| EF, migration, bootstrap SQL, RLS, grant ou snapshot | Testes afetados, verificadores de banco/EF e gate final estratégico |
| Fixture, runner, pacote central, analyzer, build ou script de verificação | Contrato focado e um gate público completo após congelar o escopo |
| Storage/Supabase local | Testes sem Supabase primeiro; verificador Storage somente para adapter/configuração Storage |

O caminho alterado é indício, não decisão suficiente. O agente inclui
consumidores transitivos do contrato e, em dúvida, amplia para o próximo projeto
relevante em vez de saltar diretamente ao gate completo.

No projeto misto e pesado Foundation Integration, a fatia estável é registrada
antes do GREEN: todas as classes do módulo/contrato dono e os contratos
compartilhados aplicáveis. Falha ou novo consumidor somente amplia essa lista;
nunca a reduz para obter resultado verde.

### Gatilhos do gate local completo

O `verify-public.sh` local é permitido quando muda infraestrutura compartilhada
de teste/build, banco ou segurança transversal; quando o diff atravessa mais de
um projeto público; quando teste focado revela falha cruzada; quando o CI está
indisponível; ou por pedido explícito. Mudança localizada sem esses gatilhos usa
verificação local focada e o CI completo da PR.

Antes de comando Docker pesado, o agente resolve o contexto efetivo e atribui
PID/PPID/cwd/comando, sessões Testcontainers, os dois prefixos de verificadores
VINX e a stack `vinx-erp-groundwork`. Se outra worktree ou dono ambíguo usa o
daemon, espera sem parar processo nem remover recurso alheio. Storage local é
proibido com stack preexistente ou dono ambíguo; ao terminar, o agente confirma
que somente sua sessão desapareceu.

## BDD verificável

### Orçamento do assembly

| Classe | Resultado observável |
|---|---|
| Sucesso | Classes Foundation mantêm PostgreSQL real e o assembly agenda no máximo duas collections paralelas. |
| Validação/erros | Remover ou alterar o teto faz o teste público do atributo falhar. |
| Acesso | N/A — configuração do runner não altera usuário, tenant, capability, rota ou dado de produto. |
| Estado/exceções | Falhas continuam usando `DisposeAsync` e Ryuk; nenhuma reutilização ou limpeza global é adicionada. |
| Concorrência | Cada processo agenda no máximo duas collections; processos distintos permanecem independentes e sem lock compartilhado. |

### Seleção e momento dos testes

| Classe | Resultado observável |
|---|---|
| Sucesso | Mudança localizada executa RED/GREEN, projeto afetado ou fatia pesada definida e consumidores necessários antes do CI completo. |
| Validação/erros | Impacto ambíguo ou falha cruzada amplia para o próximo projeto relevante sem saltar direto ao gate total. |
| Acesso | N/A — política de desenvolvimento não altera autenticação, autorização, tenant ou dado de produto. |
| Estado/exceções | Gate local completo roda no máximo uma vez após o congelamento; correção documental posterior não o repete. |
| Concorrência | Worktree atribui todos os recursos no contexto efetivo e não inicia Docker pesado com dono/stack ambíguo. |

## Verificação

1. RED: o teste do atributo falha em `main` porque não existe configuração de
   assembly.
2. GREEN: o teste passa depois da inclusão do atributo.
3. A amostra causal selecionada, na qual cada caso que usa PostgreSQL cria no
   máximo um fixture, executa classes distintas e observa no máximo dois
   PostgreSQL simultâneos no processo.
4. Os testes focados de ciclo de vida e isolamento continuam verdes.
5. `./scripts/verify-public.sh` executa uma vez antes do push quando a mudança
   possui gatilho estratégico.
6. Extensão somente documental da política valida diff, caminhos/comandos,
   contrato rápido do atributo e revisão independente; não repete gate completo
   já verde sem alteração executável.

Evidências transitórias de contagem, CPU, duração e containers pertencem à
issue/PR, não a arquivos versionados.

## Limites e estimativa

Não altera produção, banco, migrations, Supabase, workflow de CI, pacote, imagem
ou configuração global da máquina. Não introduz seletor por caminho,
retentativa, timeout, lock, fila, semáforo, reutilização, container compartilhado
ou limite de CPU/memória.

Estimativa revisada da PR completa: 6 arquivos não-teste, 2 arquivos de
teste/configuração, 810–850 linhas manuais e nenhuma linha gerada. A exceção ao
alvo flexível de 800 é coesa: separar regras obrigatórias da matriz de contexto,
atribuição de dono e segurança Docker deixaria orientação incompleta e insegura.
