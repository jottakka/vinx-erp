# Orçamento de Testcontainers da integração Foundation — issue 295

## Status

Desenho aprovado para limitar o paralelismo por processo. A issue #295 mantém
a evidência causal da reprodução; este documento registra somente decisões
duráveis.

## Objetivo

Impedir que o projeto `Vinx.Erp.Foundation.IntegrationTests` transforme todos
os processadores visíveis em PostgreSQL descartáveis simultâneos. O teste
continua usando PostgreSQL real e isolamento por fixture, mas cada processo
executa no máximo duas collections xUnit em paralelo.

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

## BDD verificável

| Classe | Resultado observável |
|---|---|
| Sucesso | Classes Foundation mantêm PostgreSQL real e o assembly agenda no máximo duas collections paralelas. |
| Validação/erros | Remover ou alterar o teto faz o teste público do atributo falhar. |
| Acesso | N/A — configuração do runner não altera usuário, tenant, capability, rota ou dado de produto. |
| Estado/exceções | Falhas continuam usando `DisposeAsync` e Ryuk; nenhuma reutilização ou limpeza global é adicionada. |
| Concorrência | Cada processo agenda no máximo duas collections; processos distintos permanecem independentes e sem lock compartilhado. |

## Verificação

1. RED: o teste do atributo falha em `main` porque não existe configuração de
   assembly.
2. GREEN: o teste passa depois da inclusão do atributo.
3. A amostra causal selecionada, na qual cada caso que usa PostgreSQL cria no
   máximo um fixture, executa classes distintas e observa no máximo dois
   PostgreSQL simultâneos no processo.
4. Os testes focados de ciclo de vida e isolamento continuam verdes.
5. `./scripts/verify-public.sh` executa uma vez antes do push.

Evidências transitórias de contagem, CPU, duração e containers pertencem à
issue/PR, não a arquivos versionados.

## Limites e estimativa

Não altera produção, banco, migrations, Supabase, CI, pacote, imagem ou
configuração global da máquina. Não introduz retentativa, timeout, lock, fila,
semáforo, reutilização, container compartilhado ou limite de CPU/memória.

Estimativa pré-código revisada após o plano: 4 arquivos não-teste, 2 arquivos
de teste/configuração, 380–500 linhas manuais e nenhuma linha gerada. A fatia
permanece abaixo de 25 arquivos e 800 linhas manuais.
