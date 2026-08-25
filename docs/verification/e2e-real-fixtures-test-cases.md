# Casos de teste — fixture E2E real

## Matriz BDD

| Classe | Given | When | Then | Evidência automática |
|---|---|---|---|---|
| Dataset fresco | PostgreSQL migrado e vazio | o seeder determinístico executa | três empresas, quatro pessoas, cinco memberships, cargos e linhas dos módulos existem; segunda execução falha sem merge | `E2eFixtureSeederTests` |
| Profile multiempresa | profile `maria` | o host real inicia e chama bootstrap e módulos | Alfa e Beta aparecem, Gama não; employees, Paguai, cards e inbox retornam linhas autorizadas | `RealE2eFixtureHostTests` |
| Negação entre empresas | Maria autenticada | employees e Paguai de Gama são solicitados | a API responde 403/404 conforme o contrato e não entrega conteúdo | `RealE2eFixtureHostTests` |
| Isolamento concorrente | dois hosts reais iniciados juntos | um employee é criado pela API somente no host A | bancos, origins e portas são distintos; somente A contém a nova pessoa; ambos removem seus recursos | `RealE2eFixtureHostTests`; o teste existente de endpoints PostgreSQL é evidência complementar |
| Lifecycle do launcher | argumentos allow-listed e porta web livre | launcher recebe readiness, inicia Vite e depois recebe SIGINT | publica URL/profile, encerra Vite antes do host, fecha portas e remove o Testcontainer próprio | `E2eFixtureHostOptionsTests`; `scripts/run-real-e2e.test.mjs`; smoke real condicionado |

## Inspeção manual Luna

Executar serialmente com um único launcher/container/browser:

1. `/` — bootstrap e seletor mostram somente empresas autorizadas.
2. `/pessoas` — as pessoas acompanham a empresa selecionada; cargos e
   capabilities são dados de autorização do seed, não conteúdo exibido nessa
   página.
3. `/paguai` — rascunhos pertencem à empresa selecionada.
4. `/atendimento` e `/kanban` — cards aparecem nos estados esperados.
5. `/notificacoes` — itens lidos e não lidos aparecem sem payload bruto.
6. tentativa Gama com profile Maria — dado permanece oculto/negado.

Registrar URL loopback, profile, rotas observadas e resultado. Não registrar
IDs internos, e-mails, claims, connection string, token, payload ou conteúdo de
arquivo.

## Gate público desta fatia

- Testes focados C# de seeder, host real e parser.
- Testes Node do launcher e smoke real serial condicionado.
- Teste Vitest do proxy, lint, format, build e proibição de Supabase direto.
- Restore locked e build do executável test-only.
- `./scripts/verify-public.sh` uma vez após congelar o escopo, porque a fatia
  altera infraestrutura compartilhada de teste e banco.

O gate não amplia o escopo para `quality/private` nem para #115.
