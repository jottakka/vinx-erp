# Harness package-first do Agent Framework (issue #127, PR 1)

> **Substituição parcial em 2026-08-15:** integração package-first, ferramentas
> protegidas e bloqueio fail-closed permanecem. Entrega de aprovação,
> coordenador e vínculo local de execução foram removidos; consulte
> `docs/superpowers/specs/2026-08-15-backend-first-simplification.md`.

## Status

O harness real permanece como prova não hospedada. Nenhum host o registra e
nenhuma superfície pública aceita resposta de aprovação. Foundation conserva a
primitiva durável sem coordenação de sessão.

## Decisão package-first

O loop modelo/ferramenta usa o Microsoft Agent Framework real sobre
`IChatClient`; não existe loop manual nem contrato próprio de chat.

| Pacote | Versão central | Uso atual |
|---|---:|---|
| `Microsoft.Extensions.AI.Abstractions` | 10.9.0 | `IChatClient`, funções e conteúdos de chat. |
| `Microsoft.Agents.AI` | 1.17.0 | sessão e resposta do agente. |
| `Microsoft.Agents.AI.Harness` | 1.17.0 | harness criado pela fábrica. |
| `Microsoft.Extensions.Logging.Abstractions` | 10.0.11 | decisão estruturada do gate. |

As versões ficam em `Directory.Packages.props` e os lockfiles são verificados
por restore travado.

## Padrões desligados

`VinxHarnessFactory` desliga web search, autoaprovação, file memory, skills,
todo, agent mode e OpenTelemetry; limita a cinco iterações e desabilita lotes
de chamadas. A fábrica pública não recebe sonda ou estado de teste.

## Fronteira pública

`HarnessAgentBundle` expõe somente:

```csharp
var session = await bundle.CreateSessionAsync(cancellationToken);
var response = await bundle.RunUserTurnAsync(session, userText, cancellationToken);
```

O segundo método constrói uma mensagem textual. O agente bruto e qualquer rota
de entrega de aprovação permanecem inacessíveis ao consumidor.

## Ferramentas e aprovação

As sondas não usam dados do ERP e não são registradas em host. Leitura passa
pelo framework; escrita usa `ApprovalRequiredAIFunction` e termina pendente,
sem efeito. O gate somente classifica: nega `CallId` vazio, contexto inválido,
ferramenta desconhecida, leitura protegida e lote ambíguo. Ele não confirma,
veta ou retoma a sessão.

## Limites e validação

Não há endpoint, provider, MCP ou execução de escrita nesta fatia. O manifesto
vigente está em
`docs/verification/issue-127-package-first-harness-test-cases.md`.

```text
dotnet test tests/public/Vinx.Erp.AI.UnitTests --no-restore --nologo -v q
dotnet test tests/public/Vinx.Erp.ArchitectureTests --no-restore --filter Category!=SupabaseStorage -v q
```
