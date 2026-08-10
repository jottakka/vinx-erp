# Casos públicos: logging portátil

| Classe | Dado / Quando / Então | Teste planejado | Quebra coberta | Situação |
| --- | --- | --- | --- | --- |
| Sucesso | Dado dois registros da extensão, quando logger é criado, então há console JSON e TraceId/SpanId. | LoggingInfrastructureTests.AddVinxLogging_is_idempotent_and_tracks_trace_and_span_ids | Remover idempotência ou rastreio. | Executado PR #88 |
| Validação/erros | Dado host configurado, quando diagnóstico HTTP é Information, então ele é filtrado; formatter JSON usa timestamp UTC e scopes. | LoggingInfrastructureTests.AddVinxLogging_filters_routine_http_diagnostics | Elevar diagnósticos HTTP ou remover UTC/scopes. | Executado PR #88 |
| Acesso | N/A: não há endpoint, principal, capability nem decisão de acesso. | N/A | N/A | N/A |
| Estado/exceções | Dado scope de requisição, quando downstream lança, então scope de CorrelationId termina. | LoggingInfrastructureTests.RequestCorrelation_disposes_scope_when_downstream_fails | Remover descarte do scope. | Executado PR #88 |
| Concorrência | Dadas duas requisições simultâneas, quando ambas logam, então cada uma tem somente seu CorrelationId. | LoggingInfrastructureTests.RequestCorrelation_isolates_concurrent_scopes | Reutilizar contexto ou scope compartilhado. | Executado PR #88 |

Comandos focados: dotnet test tests/public/Vinx.Erp.Foundation.UnitTests/Vinx.Erp.Foundation.UnitTests.csproj --filter FullyQualifiedName~LoggingInfrastructureTests; dotnet build Vinx.Erp.sln --no-restore.

Antes do push: ./scripts/verify-public.sh.
