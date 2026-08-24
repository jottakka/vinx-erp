# Casos testados — orçamento de recursos Foundation (#295)

| Cenário/BDD | Teste ou caminho | Intenção | Situação |
|---|---|---|---|
| Sucesso — assembly limita collections sem remover paralelismo | `TestAssemblyConfigurationTests.Assembly_limits_parallel_test_collections_without_disabling_parallelism` | Impedir retorno ao teto por `ProcessorCount` | coberto |
| Validação/erro — configuração ausente ou diferente de 2 falha | Mesmo teste, RED confirmado antes do atributo | Provar que o teste detecta remoção ou regressão do contrato | coberto |
| Acesso — N/A | Revisão de diff restrita a testes e documentação | Não existe usuário, tenant, capability ou rota neste contrato | N/A — infraestrutura do runner |
| Estado/exceções — ciclo de vida e descarte continuam intactos | `FoundationPostgresFixtureTests` e `FoundationApiFactoryLifecycleTests` | Preservar falha primária, descarte e Ryuk sem limpeza duplicada | coberto |
| Concorrência — amostra causal selecionada observa até dois PostgreSQL | Comando focado da verificação + `docker ps --filter label=org.testcontainers=true` | Confirmar o efeito na amostra sem ampliar o contrato universal | coberto manualmente com evidência na PR |
| Sucesso — mudança localizada executa somente verificações relevantes | `AGENTS.md` + `docs/runbooks/public-verification.md` | Fixar antes do GREEN projeto afetado ou fatia completa que só amplia | coberto por revisão documental |
| Validação/erro — impacto ambíguo amplia um projeto por vez | Matriz do runbook | Incluir consumidores transitivos sem saltar direto ao gate total | coberto por revisão documental |
| Acesso — N/A | Política de desenvolvimento | Não altera autenticação, autorização, tenant ou dado de produto | N/A — processo de desenvolvimento |
| Estado/exceções — correção documental não repete gate verde | Gatilhos do runbook + evidência da PR | Evitar nova execução pesada sem alteração executável | coberto por revisão documental |
| Concorrência — worktree não sobrepõe Docker pesado | Contexto, PID/cwd e inventário Docker do runbook | Bloquear Storage com stack/dono ambíguo e não interromper recurso alheio | coberto manualmente; nenhuma automação global |
