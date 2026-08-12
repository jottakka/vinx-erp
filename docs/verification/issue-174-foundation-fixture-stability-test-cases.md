# BDDs de estabilidade do fixture PostgreSQL Foundation

Manifesto durável da issue #174. A execução dos gates Docker pertence ao fluxo
principal da PR; esta tabela define a cobertura esperada sem registrar logs,
segredos ou evidências transitórias.

| Cenário/BDD | Teste ou caminho | Intenção | Situação |
| --- | --- | --- | --- |
| Dado um container PostgreSQL recém-iniciado, quando o fixture valida o endpoint publicado, então a conexão aponta para PostgreSQL e as strings efêmeras não usam pooling | `FoundationPostgresFixtureTests.StartAsync_uses_nonpooled_connections_to_a_verified_postgresql_endpoint` | Confirmar host/porta publicados, banco, porta interna `5432`, versão PostgreSQL e `Pooling=false` para administrador, migrador e runtime | Coberto; repetição serial do fixture é gate pendente |
| Dado um endpoint que responde `H` ao SSLRequest, quando a verificação tenta conectar, então a inicialização falha com diagnóstico sanitizado | `FoundationPostgresFixtureTests.VerifyEndpointAsync_rejects_a_non_postgresql_handshake` e `FoundationPostgresFixture.VerifyEndpointAsync` | Impedir bootstrap em endpoint incompatível e expor somente fase, host, porta e tipo da exceção; nunca senha ou string de conexão | Coberto |
| Dado o caminho de acesso autorizado ao serviço, quando a factory cria a aplicação e o cliente consulta a saúde, então o serviço fica saudável | `HealthEndpointTests.GetHealth_ReturnsHealthy` via `FoundationApiFactory.CreateAsync` | Verificar que o endpoint validado, a migração e o ciclo de vida da factory continuam utilizáveis por um consumidor autorizado | Coberto; execução pública pertence ao gate da PR |
| Dado uma falha durante início, bootstrap, migração ou seed, quando o ciclo termina, então o host e o fixture são descartados e um novo ciclo pode começar | `FoundationPostgresFixture.StartAsync`, `FoundationApiFactory.CreateAsync` e demais `Create*Async`; repetição de `FoundationPostgresFixtureTests` | Evitar estado parcial, conexões físicas reaproveitadas e contaminação entre ciclos; manter a ordem de descarte documentada no plano | Coberto no caminho de lifecycle; novo ciclo é validado pela repetição serial planejada |
| Dado o fixture executado em ciclos sucessivos, quando o projeto de integração é repetido em série, então cada ciclo alcança o PostgreSQL correto sem handshake incompatível | Repetição serial planejada de `FoundationPostgresFixtureTests` e execução normal do projeto `Vinx.Erp.Foundation.IntegrationTests` | Detectar regressão de endpoint, pooling ou cleanup entre execuções | Gate pendente; não afirmar cobertura paralela |

O escopo desta issue não prova concorrência entre fixtures ou testes paralelos.
A cobertura de concorrência fica limitada à repetição serial e à execução normal
do projeto, conforme o plano; não há alegação de cobertura paralela.
