# Estabilidade do fixture PostgreSQL Foundation — issue 174

## Objetivo

Cada fixture de integração deve conectar ao PostgreSQL descartável que acabou
de criar. Um endpoint publicado que não fala PostgreSQL deve falhar com
diagnóstico sanitizado antes de bootstrap, migração ou testes de produto.

## Decisões aprovadas

- A PR é exclusivamente infraestrutura de testes e é independente da correção
  funcional da issue #173.
- `PostgreSqlContainer.StartAsync` não é evidência suficiente: a prontidão deve
  ser confirmada a partir do host/porta publicados consumidos pelo processo de
  teste.
- A confirmação de prontidão é semântica: uma conexão Npgsql real e uma consulta
  PostgreSQL verificável, nunca um atraso arbitrário ou retry cego.
- Connection strings efêmeras do fixture não compartilham conexões físicas entre
  containers descartáveis. A decisão será validada com `Pooling=false` no
  fixture, não por limpeza global de pools.
- Falha parcial de criação descarta o fixture; a factory descarta o host antes
  do banco. Paralelismo global permanece habilitado.

## Evidência e hipótese de trabalho

Depois de `PostgreSqlContainer.StartAsync`, `OpenAdministratorConnectionAsync`
pode falhar com `NpgsqlException: Received unknown response H for SSLRequest`.
`H` não é resposta PostgreSQL para esse handshake. A versão instalada
Testcontainers 4.9.0 confirma o banco internamente com `pg_isready`; isso não
prova que o host e a porta publicados apontam para o mesmo processo.

As causas ainda não estão confirmadas. O teste RED precisa diferenciar endpoint
externo incorreto, readiness incompleta, ciclo de descarte parcial e estado de
pool antes de mudar o fixture.

## Desenho

1. Após iniciar o container, o fixture deriva uma connection string administrativa
   do host e da porta publicados naquele ciclo, sem expor senha ou string completa.
2. Antes do bootstrap, valida que host/porta usados correspondem ao container e
   abre uma conexão Npgsql real. Uma consulta curta confirma usuário, banco,
   porta do servidor e versão PostgreSQL; valores sensíveis não são registrados.
3. Se essa etapa falhar, lança erro com fase, host, porta e tipo de exceção
   sanitizados, descarta o container e não executa bootstrap ou migração.
4. As strings de administrador, migrador e runtime do fixture usam conexões sem
   pooling. A factory tem cleanup para falha em `StartAsync`, bootstrap, migração
   ou seed e encerra primeiro `WebApplicationFactory`, depois o fixture.
5. Testes públicos exercitam ciclo único, falha de endpoint/diagnóstico por
   uma dependência controlada e repetição serial de criação/descarte. Eles não
   desligam paralelismo nem fazem limpeza manual de volumes.

## Limites

Não altera confirmações, idempotência, RLS, endpoints, banco de produção,
frontend, imagem PostgreSQL sem evidência, nem configurações globais do runner.
Não engole `SSLRequest`, registra senhas, `connection string` completa ou logs
integrais do container.

## BDD verificável

| Classe | Resultado observável |
|---|---|
| Sucesso | O fixture conecta ao endpoint publicado, confirma PostgreSQL e conclui bootstrap/migração. |
| Validação/erros | Handshake incompatível falha antes de bootstrap com fase, host e porta sanitizados. |
| Acesso | N/A — o fixture não expõe rota, usuário nem tenant; o teste confirma esse limite por não iniciar host HTTP. |
| Estado/exceções | Falha parcial descarta o container; o próximo fixture não reutiliza dados, conexão ou porta inválidos. |
| Concorrência | Fixtures criados/descartados no paralelismo configurado usam endpoints próprios, sem resposta SSL incompatível ou vazamento. |

## Estimativa e entrega

Estimativa antes de código: 3–6 arquivos não-teste e 180–380 linhas manuais,
incluindo este desenho, plano e manifesto. Se o diagnóstico demonstrar defeito
externo de Colima/Testcontainers sem correção local segura, a PR entrega
diagnóstico e reprodução, e abre issue de adiamento vinculada em vez de mascarar
o problema.
