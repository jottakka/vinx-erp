# Logging estruturado e portátil

## Status

Decisão aprovada para a atualização da PR #88 em 09/08/2026.

## Objetivo

Configurar os três hosts para logs JSON estruturados, correlação HTTP e IDs
de trace sem acoplar módulos a um provider de logging específico.

## Decisão

`ILogger<T>` e DI do .NET são a fronteira de logging. Não haverá
`IVinxLogger`, facade própria, Serilog, NLog ou exporter novo nesta fatia.

`Foundation.Observability` expõe duas extensões:

- `AddVinxLogging(this ILoggingBuilder)` centraliza formatter JSON, scopes e
  `TraceId`/`SpanId`.
- `UseVinxRequestCorrelation(this IApplicationBuilder)` aplica o contexto de
  correlação já existente em requisições HTTP.

API, MCP e Worker chamam a mesma extensão de logging. API e MCP usam também
o middleware de correlação; Worker só ganhará correlação quando existir uma
unidade de trabalho concreta.

O formatter usa JSON, timestamps UTC e scopes. Os filtros permanecem na
seção padrão `Logging` de configuração do host, inclusive por variáveis de
ambiente. A extensão não remove providers: registra somente o console JSON
de forma idempotente e preserva providers explicitamente adicionados pelo
deploy futuro.

O middleware abre scope somente com `CorrelationId`, mantém a tag
`vinx.correlation_id` no `Activity` e não emite log por requisição. Tags e
`Baggage` do `Activity` não entram automaticamente nos logs.

## Limites

Esta fatia não adiciona logs de negócio, OTLP, exporter, traces de banco ou
HTTP logging. Cada novo comportamento registra somente sinais previstos em
`AGENTS.md`, com nível e propriedades testados quando forem critério de
aceite.

Logs nunca contêm JWT, segredo, PII, payload bruto, conteúdo de arquivo,
prompt ou identificador externo do provider. Auditoria continua evidência
durável distinta do logging operacional.

## Verificação

Testes públicos cobrem provider JSON único, rastreio de `TraceId`/`SpanId` e
scope de correlação sem tags ou `Baggage`. A mudança passa pelo gate público
uma vez antes do push da PR #88.
