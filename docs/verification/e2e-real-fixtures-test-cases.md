# Casos de teste — fixture E2E real

## Matriz BDD

| Classe | Given | When | Then | Evidência automática |
|---|---|---|---|---|
| Dataset fresco | PostgreSQL migrado e vazio | o seeder determinístico executa | três empresas, quatro pessoas, cinco memberships, cargos e linhas dos módulos existem; segunda execução falha sem merge | `E2eFixtureSeederTests` |
| Profile multiempresa | profile `maria` | o host real inicia e chama bootstrap e módulos | Alfa e Beta aparecem, Gama não; employees, Paguai, cards e inbox retornam linhas autorizadas | `RealE2eFixtureHostTests` |
| Negação entre empresas | Maria autenticada | employees e Paguai de Gama são solicitados | a API responde 403/404 conforme o contrato e não entrega conteúdo | `RealE2eFixtureHostTests` |
| Fixture compartilhada | Maria e Clara têm principals fixos e o banco ainda não existe | o host composto migra, semeia e verifica uma vez | duas origins API distintas compartilham host/porta/database PostgreSQL; Maria lê Alfa/Beta, Clara lê somente Gama e uma mutação Maria fica no owner | `RealE2eFixtureHostTests.Shared_host_seeds_once_and_keeps_profile_RLS_isolated` |
| Agente demonstrativo compartilhado | as duas origins compartilham o seed e um modelo fixo somente de teste | Maria solicita Alfa; depois Maria solicita Beta e Clara solicita Gama | Alfa retorna `200 result/ok` com texto fixo; Beta e Gama retornam `200 denied/capability_denied` sem texto; somente Alfa consulta o modelo, com seleção sintética | `RealE2eFixtureHostTests.Shared_host_seeds_once_and_keeps_profile_RLS_isolated` |
| Export seguro | fixture compartilhada pronta | o host cria o arquivo administrativo local | diretório `0700`, arquivo regular `0600`, sem newline, sem symlink e removido antes do banco | `E2eFixtureHostOptionsTests` |
| Lifecycle do launcher | argumentos allow-listed e portas `4174`/`4175` livres | launcher recebe readiness estrita, inicia dois Vites e depois recebe SIGINT | publica apenas URLs/profile/endpoint DBeaver/caminho protegido; encerra Clara, Maria e host; saída não zero do host no cleanup falha | `E2eFixtureHostOptionsTests`; `scripts/run-real-e2e.test.mjs` |

## Inspeção manual Maria/Clara

Executar uma única sessão compartilhada com `maria,clara`, dois browsers e um
único launcher/container:

1. Maria em `4174`: `/` mostra Alfa/Beta e não Gama; Clara em `4175`: `/` mostra
   somente Gama.
2. `/pessoas` — Alfa mostra Maria e Beatriz; Beta fica fora do seletor porque o
   cargo de Maria não concede o diretório nessa empresa. Cargos/capabilities não
   são conteúdo exibido na página.
3. `/paguai` — rascunhos pertencem à empresa selecionada.
4. `/atendimento` — cards aparecem nos estados esperados; o alias `/kanban` é
   intencionalmente ausente.
5. `/notificacoes` — itens lidos e não lidos aparecem sem payload bruto.
6. Criar uma pessoa Alfa por Maria, confirmar a linha no DBeaver em modo
   somente leitura e confirmar que Clara permanece sem acesso a Alfa.

Registrar apenas URLs loopback, profiles, campos do endpoint DBeaver, caminho
protegido, rotas observadas e resultado. Não registrar IDs internos, e-mails,
claims, origins API, connection string, senha, token, payload ou conteúdo de
arquivo.

## Gate público desta fatia

- Restore locked e build do executável test-only antes dos testes Node, porque o
  contrato de subprocesso executa o DLL já verificado e não faz build implícito.
- Testes focados C# de lifecycle, parser/output e host compartilhado; o último
  requer uma única atribuição explícita do daemon Docker.
- Testes Node do launcher single/compartilhado, sem smoke condicionado.
- CI continua o gate público completo; não executar o gate local completo nesta
  fatia isolada.

Ordem mínima do trecho executável:

```bash
dotnet restore tests/tools/Vinx.Erp.Foundation.E2eHost/Vinx.Erp.Foundation.E2eHost.csproj --locked-mode
dotnet build tests/tools/Vinx.Erp.Foundation.E2eHost/Vinx.Erp.Foundation.E2eHost.csproj --no-restore
node --test scripts/run-real-e2e.test.mjs
```

O gate não amplia o escopo para `quality/private` nem para #115.

O caso do Agente é uma demonstração determinística interna ao fixture: não
realiza HTTP para provider, não prova qualidade do modelo nem login Supabase, e
não deve ser interpretado como evidência de provider, rede ou produção.
