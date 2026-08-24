# Casos testados — orçamento de recursos Foundation (#295)

| Cenário/BDD | Teste ou caminho | Intenção | Situação |
|---|---|---|---|
| Sucesso — assembly limita collections sem remover paralelismo | `TestAssemblyConfigurationTests.Assembly_limits_parallel_test_collections_without_disabling_parallelism` | Impedir retorno ao teto por `ProcessorCount` | coberto |
| Validação/erro — configuração ausente ou diferente de 2 falha | Mesmo teste, RED confirmado antes do atributo | Provar que o teste detecta remoção ou regressão do contrato | coberto |
| Acesso — N/A | Revisão de diff restrita a testes e documentação | Não existe usuário, tenant, capability ou rota neste contrato | N/A — infraestrutura do runner |
| Estado/exceções — lifecycle e cleanup continuam intactos | `FoundationPostgresFixtureTests` e `FoundationApiFactoryLifecycleTests` | Preservar falha primária, descarte e Ryuk sem cleanup duplicado | coberto |
| Concorrência — amostra causal não excede dois PostgreSQL por processo | Comando focado da verificação + `docker ps --filter label=org.testcontainers=true` | Confirmar o efeito real do atributo no runner | coberto manualmente com evidência na PR |
