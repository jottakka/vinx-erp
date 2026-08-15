# Harness package-first do Agent Framework (issue #127, PR 1)

## Status

Aprovada em 13 de agosto de 2026 e complementada pela política durável de
14/08. O harness real é usado em produção desde a primeira fatia; o PR 2
adiciona Foundation, single-flight e veto.

## Decisão package-first

O loop modelo/ferramenta é o Microsoft Agent Framework real. Não há loop manual,
substituto de `IChatClient`, agente sintético de produção ou pacote citado sem
uso. Os pacotes centrais permanecem:

| Pacote | Versão | Uso verificado |
|---|---:|---|
| `Microsoft.Extensions.AI` | 10.9.0 | APIs de AI fornecidas ao harness. |
| `Microsoft.Extensions.AI.Abstractions` | 10.9.0 | Funções, aprovação de ferramenta, conteúdo de chat e `IChatClient`. |
| `Microsoft.Agents.AI` | 1.17.0 | Sessão e resposta usadas pelo feixe interno. |
| `Microsoft.Agents.AI.Harness` | 1.17.0 | Harness Microsoft construído pela fábrica. |
| `Microsoft.Extensions.Logging.Abstractions` | 10.0.11 | Logging do gate. |

As versões estão fixadas em `Directory.Packages.props` e os restores travados
revalidam os lockfiles commitados.

## Padrões desligados

`VinxHarnessFactory` desliga web search, autoaprovação de ferramenta, file
memory, skills, todo, agent mode e OpenTelemetry; limita a cinco iterações.
`AllowMultipleToolCalls = false` mantém uma única solicitação de aprovação por
resposta. A aprovação automática fica fail-closed.

## Fronteira pública

O consumidor recebe apenas `HarnessAgentBundle` e seus dois métodos públicos:

```csharp
var session = await bundle.CreateSessionAsync(cancellationToken);
var response = await bundle.RunUserTurnAsync(session, userText, cancellationToken);
```

O segundo método aceita texto de usuário; conteúdo de resposta de aprovação
injetado como turno falha fechado. Agente cru, permit, fonte de `RequestId` e
entrega de aprovação não são APIs do consumidor. O `RequestId` de execução é
criado pelo servidor e emparelhado ao feixe antes do turno.

## Ferramentas e aprovação

Sondas são registradas por `AIFunctionFactory`; escrita usa
`ApprovalRequiredAIFunction` e leitura passa pelo framework sem aprovação. A
allowlist e o risco vêm de `VinxToolPolicy`, o ator/empresa vêm de
`VinxExecutionContext`, e o gate rejeita contexto ausente, lote ambíguo ou
ferramenta fora da allowlist.

Depois do claim durável, o coordenador entrega a decisão pelo gate e o loop do
framework continua na mesma sessão. O coordenador não chama a ferramenta
manualmente. O permit interno é vinculado a proposta e `CallId`, de uso único;
o gate revalida a decisão antes da entrega. Veto usa a mesma fronteira e
`VetoStart` publica o trabalho fora do lock somente depois de publicar o estado.

## Limites

Não há host HTTP, endpoints de aprovação, adaptadores de provedor ou MCP nesta
fatia. A autenticação do host é N/A aqui e permanece vinculada à issue #138;
`GET /ai/whoami` segue na issue #125. A remoção física de linhas expiradas é a
issue #201.

Hardening não bloqueante deferido pelo dono: matriz de gatilhos forçados e
advisory locks; contagens exatas de descarte e instrumentação privada; reflexão
ampla; agente sintético grande; combinações exaustivas; stress/soak e
repetição em grande escala. O ajuste pequeno de `VetoStart` não foi deferido.

## Validação

```text
dotnet restore tests/public/Vinx.Erp.Foundation.IntegrationTests/Vinx.Erp.Foundation.IntegrationTests.csproj --use-lock-file
dotnet test tests/public/Vinx.Erp.AI.UnitTests --no-restore --nologo -v q
dotnet test tests/public/Vinx.Erp.ArchitectureTests --no-restore --filter Category!=SupabaseStorage -v q
dotnet format whitespace --verify-no-changes
```

As evidências finais do PR 2, com 44 testes AI, 6 wired PostgreSQL, 31 de
serviço, 6 de migration e 118 unitários Foundation, estão no manifesto de
`docs/verification/issue-127-vinx-policy-test-cases.md`.
