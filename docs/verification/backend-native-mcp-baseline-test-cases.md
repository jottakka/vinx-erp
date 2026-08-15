# Casos públicos — baseline nativa do backend e MCP

## Escopo

Esta baseline substitui infraestrutura própria por Options, Health Checks,
Problem Details, route groups e Rate Limiting do ASP.NET Core. O
`McpHost` usa `ModelContextProtocol.AspNetCore` 2.2.0 em modo stateless.
`vinx_ping` é somente uma sonda do transporte: não mede dependências, não cria
contexto confiável e não conclui #133, #132 ou #125.

| Classe | Cenário | Evidência pública |
|---|---|---|
| sucesso | API, MCP e Worker expõem health nativo sem cache | `HealthEndpointTests.GetHealth_ReturnsHealthy`; `HostPlatformTests.Mcp_and_worker_hosts_expose_native_uncached_health` |
| validação/erros | Configuração obrigatória inválida falha no startup sem expor credencial | `ApiHostPlatformTests.Invalid_required_option_fails_host_startup_without_disclosing_credentials` |
| validação/erros | Provider exige HTTPS fora de loopback em desenvolvimento | `ApiHostPlatformTests.Production_rejects_plaintext_provider_endpoints`; `ApiHostPlatformTests.Jwt_metadata_requires_https_except_for_loopback_development` |
| segurança | Cliente Storage não segue redirect com a credencial privilegiada | `ApiHostPlatformTests.Storage_client_disables_redirects_to_keep_service_credentials_on_configured_origin` |
| validação/erros | Exceção inesperada retorna Problem Details 500 sem mensagem interna | `ApiHostPlatformTests.Unhandled_failure_returns_sanitized_problem_details` |
| acesso | MCP sem autenticação não descobre nem chama ferramenta | `HostPlatformTests.Mcp_requires_authentication` |
| acesso | `Host` não permitido e todo `Origin` presente são rejeitados | `HostPlatformTests.Mcp_rejects_untrusted_host_headers`; `HostPlatformTests.Mcp_rejects_every_browser_origin` |
| estado | Cliente oficial lista somente `vinx_ping`, chama duas vezes e não recebe session ID | `HostPlatformTests.Official_client_lists_and_calls_only_the_stateless_ping_tool` |
| concorrência | Tráfego anônimo e autenticado usa duas partições fixas independentes; ambas são limitadas sem limitar health | `ApiHostPlatformTests.Unauthenticated_requests_use_a_bounded_partition_without_limiting_health`; `ApiHostPlatformTests.Rate_limiter_bounds_accepted_requests_without_limiting_health`; `HostPlatformTests.Mcp_separates_anonymous_and_authenticated_rate_limits_and_keeps_health_available` |

## Comandos

```bash
dotnet test tests/public/Vinx.Erp.Foundation.IntegrationTests --filter 'FullyQualifiedName~ApiHostPlatformTests|FullyQualifiedName~HealthEndpointTests'
dotnet test tests/public/Vinx.Erp.Hosts.IntegrationTests
dotnet list Vinx.Erp.sln package --vulnerable --include-transitive
./scripts/verify-public.sh
```

## Limites

A autenticação MCP atual aceita apenas cliente first-party com JWT Supabase
pré-obtido. Não há Protected Resource Metadata, fluxo OAuth MCP, `ActorContext`,
empresa, capability, banco nem serviço de domínio. Esses requisitos devem ser
entregues antes de substituir `vinx_ping` por ferramenta funcional.
