# Ambiente E2E real e dados multiempresa — desenho da issue #114

## Status

Desenho aprovado para execução autônoma em 2026-08-25. A autorização vem do
pedido explícito do responsável para criar o banco completo, testar o sistema e
tomar as decisões durante sua ausência. #115 permanece a fatia separada de
Playwright automatizado.

## Objetivo

Subir, com um comando, um ambiente descartável para inspeção manual contra
browser, ApiHost e PostgreSQL reais. O banco contém empresas, pessoas, papéis,
capabilities e linhas dos módulos atuais. A identidade existe somente no host
de teste e não simula login/provider/MFA de produção.

## Decisão

Reusar `FoundationPostgresFixture` e `FoundationApiFactory`. A factory já cria
PostgreSQL isolado, aplica roles/migrations e substitui autenticação apenas por
`ConfigureTestServices`; autorização, RLS, auditoria, sessão interna e serviços
de domínio continuam reais.

Um executável pequeno em `tests/tools/Vinx.Erp.Foundation.E2eHost` referencia o
projeto de integração existente, chama `UseKestrel(0)` antes de inicializar a
factory, valida o dataset por HTTP e fica vivo até SIGINT. Não haverá migration
de seed, tabela técnica, Supabase Auth, password, token ou segundo fixture.

Um launcher Node, sem pacote novo, inicia E2eHost e Vite. Vite recebe
`VINX_API_ORIGIN` somente no processo filho. No encerramento, o launcher para
Vite primeiro; E2eHost para Kestrel e só então descarta PostgreSQL. Os filhos
rodam em grupos de processo próprios; sinais atingem somente grupos criados
pelo launcher, incluindo wrappers e descendentes.

## Identidade de teste

O argumento `--profile` aceita apenas nomes de um catálogo compilado. Nenhum
header, query, cookie ou campo do browser escolhe usuário, subject, empresa ou
capability.

Cada profile contém UUID interno, UUID de external identity, subject, nome e
e-mail `.example.test`. A autenticação de teste publica claims com o issuer já
usado pela factory; `InternalIdentityResolver` e o session gate resolvem a
identidade persistida normalmente.

Profiles desta fatia:

| Profile | Memberships | Uso |
|---|---|---|
| `maria` | Alfa e Beta | padrão manual; capabilities diferentes |
| `beatriz` | Alfa | atendimento e employee |
| `joao` | Beta | financeiro/leitura |
| `clara` | Gama | dado proibido para Maria |

Não incluir `operator`: `/administracao` depende de uma jornada própria e não é
necessária para pessoas/empresas/cargos.

## Dataset determinístico

IDs e timestamps são constantes. O seed usa uma única transação de
administrador, comandos parametrizados e inserts normais. Não usa `ON CONFLICT`
nem `DO NOTHING`: repetição no mesmo banco prova estado parcial/sujo e falha.

### Organização e acesso

- Grupo `Vinx Demo Group`.
- Empresas Alfa, Beta e Gama.
- Unidades/equipes: Operações/Atendimento em Alfa; Financeiro/Contas em Beta;
  Restrita em Gama.
- Memberships: Maria Alfa/Beta; Beatriz Alfa; João Beta; Clara Gama.
- Employees para cada membership, com vários nomes visíveis.
- Papéis: Operations, Atendimento agent, Reader, Finance operator e
  Restricted.
- Grants somente de capabilities canônicas e scope Company.

Maria recebe em Alfa leitura/criação Paguai, fila/criação/assign/move de
Atendimento e criação de employees. Em Beta recebe apenas leituras Paguai e
Atendimento. Não recebe membership nem papel em Gama.

### Linhas de módulo

- Paguai: dois rascunhos Alfa e dois Beta, BRL, solicitantes distintos, datas e
  valores fixos.
- Atendimento/Kanban: cards Alfa em `novo`, `em_andamento` e `concluido`, um
  atribuído a Beatriz; um card Beta; um card Gama invisível a Maria.
- Notificações: intents `in_app`/`delivered` para Maria em Alfa/Beta, uma lida e
  uma não lida, com deep links `/paguai` e `/atendimento`; uma Gama para Clara.
  Os campos de lifecycle do provider respeitam as constraints atuais.

Não semear files, approvals, WhatsApp, outbox, provider AI, storage ou conteúdo
de documento. Essas linhas exigiriam serviços externos e não ajudam o objetivo
de pessoas/empresas/cargos.

## Fluxo de startup

1. Resolver profile allow-listed e argumentos estritos.
2. Iniciar `FoundationPostgresFixture` e aplicar migrations sem Wolverine
   storage.
3. Confirmar banco fresco e executar o seed em uma transação.
4. Confirmar contagens/IDs e external identity pelo administrador.
5. Configurar `FoundationApiFactory`, chamar `UseKestrel(0)` e obter
   `HttpClient.BaseAddress`.
6. Recusar origem não-loopback.
7. Verificar `/health` e `/foundation/bootstrap` para o profile.
8. Verificar por HTTP pessoas, Paguai, Atendimento e notificações autorizadas.
9. Verificar uma leitura direta Gama negada.
10. Somente então escrever o prefixo literal
    `E2E_API_ORIGIN=http://127.0.0.1:` seguido da porta decimal de Kestrel.

Falha imprime apenas fase e categoria estável, encerra com código não zero e
descarta, dentro do `StartAsync`, client, Kestrel/factory e fixture nessa ordem
antes de relançar a falha tipada. Não imprime connection string, senha,
subject, claims, e-mail, IDs do dataset ou payloads.

## Launcher

`node scripts/run-real-e2e.mjs --profile maria`:

- rejeita argumentos/profile desconhecidos;
- verifica que a porta web configurada está livre;
- inicia E2eHost e aguarda a linha de prontidão com timeout;
- não inicia Vite se o host falhar;
- resolve o CLI absoluto em `<repo>/node_modules/vite/bin/vite.js` e inicia-o
  com `process.execPath`, `cwd=<repo>/apps/web` e argumentos exatos
  `--host 127.0.0.1 --port 4174 --strictPort`, em grupo de processo próprio e
  com `VINX_API_ORIGIN` somente no filho;
- aguarda HTTP do Vite por condição;
- imprime apenas URL do browser e profile;
- encaminha SIGINT/SIGTERM e espera cleanup dos dois filhos.

Vite adiciona o prefixo `/notifications`; `/agent/runs`, `/foundation`,
`/paguai` e `/atendimento` continuam inalterados.

## Testes

- Unitários: catálogo/profile, argumentos, seed plan/IDs, URLs e diagnóstico
  redigido.
- Integração única e serial: seed + Kestrel + bootstrap + quatro módulos +
  negação Gama, reutilizando um container.
- Estado sujo: segunda execução do seed na mesma fixture falha e não faz merge.
- Isolamento concorrente: um teste estratégico inicia dois `E2eFixtureHost`
  simultâneos, prova bancos e origins/portas distintos, cria um employee Alfa
  por API em apenas um host e confirma por HTTP que o outro não mudou. O teste
  existente de endpoints PostgreSQL permanece evidência complementar.
- Launcher: testes Node nativos de argumentos, readiness, falha antes de Vite e
  ordem de shutdown com processos fake injetados.
- Manual Luna: um único container/API/Vite/browser; rotas `/`, `/pessoas`,
  `/paguai`, `/atendimento`, `/kanban`, `/notificacoes` e tentativa Gama.

## Segurança e privacidade

- Test auth existe somente no assembly/factory de teste.
- Sem endpoint seletor de identidade, header mágico ou bypass em produção.
- Sem secrets, dados reais, conteúdo médico, arquivo, prompt ou token.
- Logs operacionais da fixture usam fase/categoria fixa; URL loopback é o único
  valor dinâmico publicado.
- Browser continua sem acesso direto a PostgreSQL/Supabase.

## Escopo e limites

Alvo de 20 arquivos totais: 19 arquivos manuais/source/documentação e um
lockfile gerado. Estimativa de 1.900–2.500 linhas manuais, incluindo as 613
linhas já versionadas de desenho/plano; o seed rico torna a estimativa inicial
de 450–650 irreal. A exceção ao alvo de 800 é explícita e indivisível: seed,
host, launcher e proxy compõem o único comando consumível solicitado. Continua
abaixo de 25 arquivos, sem dependência ou migration nova.

## Adiamentos

- #115: Playwright automatizado real, anonymous/login/reconnect/revocation.
- Profile operator e `/administracao`.
- Mutação de notification/Paguai/Atendimento além do readback mínimo.
- Supabase Auth/Storage, provider AI, WorkerHost e integração externa.
