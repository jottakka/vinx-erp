# Casos testados — orçamento de recursos Foundation (#295)

| Cenário/BDD | Teste ou caminho | Intenção | Situação |
|---|---|---|---|
| Sucesso — assembly limita collections sem remover paralelismo | `TestAssemblyConfigurationTests.Assembly_limits_parallel_test_collections_without_disabling_parallelism` | Impedir retorno ao teto por `ProcessorCount` | coberto |
| Validação/erro — configuração ausente ou diferente de 2 falha | Mesmo teste, RED confirmado antes do atributo | Provar que o teste detecta remoção ou regressão do contrato | coberto |
| Acesso — N/A | Revisão de diff restrita a testes e documentação | Não existe usuário, tenant, capability ou rota neste contrato | N/A — infraestrutura do runner |
| Estado/exceções — ciclo de vida e descarte continuam intactos | `FoundationPostgresFixtureTests` e `FoundationApiFactoryLifecycleTests` | Preservar falha primária, descarte e Ryuk sem limpeza duplicada | coberto |
| Concorrência — amostra causal selecionada observa até dois PostgreSQL | Comando focado da verificação + `docker ps --filter label=org.testcontainers=true` | Confirmar o efeito na amostra sem ampliar o contrato universal | coberto manualmente com evidência na PR |
