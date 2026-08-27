# Fixture E2E real descartável

## Objetivo

Este fluxo sobe, por um comando, um PostgreSQL descartável, dois ApiHosts reais
em Kestrel e dois Vites: Maria em `4174` e Clara em `4175`. Ambos usam o mesmo
seed e o mesmo banco, mas cada host fixa o seu principal; o browser não escolhe
identidade. O dataset contém pessoas, empresas, cargos e registros dos módulos
necessários para inspeção manual. Ele existe somente em projetos de teste e não
altera migrations, seed de produção ou Supabase local.

## Pré-requisitos

- Daemon Docker compatível com Testcontainers, disponível no contexto já
  selecionado.
- Node.js e .NET nas versões travadas pelo repositório.
- Dependências instaladas com `npm ci` e `dotnet restore --locked-mode`.
- Portas `4174` e `4175` livres. A porta PostgreSQL é dinâmica; nunca reutilize
  `54322`, que pode pertencer a uma stack local já existente.

Antes de iniciar, atribua containers e processos VINX já ativos. Não interrompa
recursos de outra worktree nem a stack Supabase. O launcher cria e remove apenas
os grupos de processo e o Testcontainer desta execução.

O teste Node executa o DLL do host diretamente para provar o contrato de processo
sem iniciar Docker quando os argumentos são inválidos. Portanto, no gate, mantenha
esta ordem explícita:

```bash
dotnet restore tests/tools/Vinx.Erp.Foundation.E2eHost/Vinx.Erp.Foundation.E2eHost.csproj --locked-mode
dotnet build tests/tools/Vinx.Erp.Foundation.E2eHost/Vinx.Erp.Foundation.E2eHost.csproj --no-restore
node --test scripts/run-real-e2e.test.mjs
```

O teste Node falha com uma instrução de build quando esses artefatos não existem;
ele não compila o host implicitamente em cada execução.

## Iniciar

Na raiz do repositório, o modo compartilhado é o roteiro multiempresa:

```bash
npm run dev:real-e2e -- --profiles maria,clara --agent-mode deterministic
```

O modo compatível individual continua aceitando `--profile maria`, `beatriz`,
`joao` ou `clara`. Todo comando exige exatamente `--agent-mode deterministic`
ou `--agent-mode real`; o modo compartilhado aceita somente o literal
`--profiles maria,clara`, sem inversão, espaços, profiles extras ou troca por
header, cookie, query string ou interface web.

`deterministic` não faz rede externa e remove
`VINX_AGENT_PROVIDER_API_KEY` de todos os filhos. `real` exige essa variável
válida antes de verificar portas ou iniciar Docker; a chave chega somente aos
ApiHosts, que fixam internamente o endpoint OpenAI-compatible e o modelo de
smoke. Vite, browser, readiness e diagnóstico não recebem nem publicam chave,
endpoint ou modelo. O smoke real só usa conteúdo sintético e continua sem
envio de mensagens por canais.

Depois do readback dos dois hosts e do HTTP dos dois Vites, o modo compartilhado
publica somente:

```text
E2E_BROWSER_URL_MARIA=http://127.0.0.1:4174
E2E_PROFILE_MARIA=maria
E2E_BROWSER_URL_CLARA=http://127.0.0.1:4175
E2E_PROFILE_CLARA=clara
E2E_DB_HOST=<loopback>
E2E_DB_PORT=<porta-dinamica>
E2E_DB_DATABASE=vinx_fixture_<hex>
E2E_DB_USER=postgres
E2E_DB_PASSWORD_FILE=<caminho-absoluto-protegido>
```

As origins da API, PID, claims, e-mail, senha, connection string e identificador
de container não são públicos. `VINX_API_ORIGIN` é entregue somente ao Vite
correspondente. O modo individual preserva as duas linhas `E2E_BROWSER_URL` e
`E2E_PROFILE`.

## Inspeção administrativa local

O arquivo indicado em `E2E_DB_PASSWORD_FILE` fica em diretório `0700`, é um
arquivo regular `0600` e contém a senha sem quebra de linha. O caminho pode ser
usado localmente; o conteúdo não deve entrar em variável, argumento, histórico,
log ou documento.

```bash
vinx_e2e_password_file='<valor de E2E_DB_PASSWORD_FILE>'
test -f "$vinx_e2e_password_file"
test "$(stat -f '%Lp' "$vinx_e2e_password_file")" = '600'
pbcopy < "$vinx_e2e_password_file"
unset vinx_e2e_password_file
```

No DBeaver, use `E2E_DB_HOST`, `E2E_DB_PORT`, `E2E_DB_DATABASE` e
`E2E_DB_USER`; cole a senha e não habilite a opção de salvá-la. Esta é uma
conexão administrativa `postgres`: ela ignora RLS, validação de domínio,
idempotência e auditoria da aplicação. Prefira inspeção somente leitura; uma
escrita direta contamina as duas sessões e não é evidência de autorização.
Depois de conectar, limpe o clipboard:

```bash
printf %s '' | pbcopy
```

## Dataset

- Empresas Alfa, Beta e Gama no mesmo grupo.
- Maria participa de Alfa e Beta; Beatriz, somente de Alfa; João, somente de
  Beta; Clara, somente de Gama.
- Unidades, equipes, cargos, assignments e capabilities distintos por empresa.
- Employees em cada empresa, quatro rascunhos Paguai, cinco cards Atendimento,
  notificações entregues e um anexo sintético confirmado `agentic_attachment`
  de Maria/Alfa. Não há token, endereço de canal, OCR bruto ou saída de provider.

No modo compartilhado, seed e verify executam uma vez antes dos dois Kestrels.
Uma mutação de Maria é persistida no mesmo PostgreSQL usado por Clara; Clara só
vê linhas permitidas por suas memberships, capabilities e RLS. Não há reset,
reseed ou rollback global durante a sessão: para voltar ao estado determinístico,
encerre a execução e crie outra.

No mesmo modo compartilhado, `POST /agent/runs` usa exclusivamente um modelo
determinístico de demonstração. Maria em Alfa recebe o texto fixo marcado como
`[Demonstração E2E]`; Maria em Beta e Clara em Gama recebem a negação tipada
`capability_denied`, antes de qualquer chamada ao modelo. Essa evidência é
automática e prova a ordem autorização-modelo com os principals reais do
fixture; ela não consulta provider externo nem avalia qualidade de resposta.

Maria/Alfa também possui um roteiro Pagaí compilado: o anexo sintético passa
pela fonte autorizada real, gera proposta tipada, permite a confirmação e cria
um único rascunho vinculado à proposta. A execução contra PostgreSQL/containers
permanece uma evidência runtime pendente; não a trate como já demonstrada neste
checkout sem executar a fatia focada aprovada.

## Roteiro manual mínimo

Abra Maria e Clara em contextos de browser separados; não faça login nem injete
header, token ou senha no browser.

1. Em `/`, Maria confirma Alfa/Beta e ausência de Gama; Clara confirma somente
   Gama.
2. Em `/pessoas`, selecione Alfa e confira Maria e Beatriz. Beta não aparece
   porque o cargo de Maria nessa empresa não concede o diretório de pessoas.
3. Em `/paguai`, confira os rascunhos da empresa selecionada.
4. Em `/atendimento`, confira cards nos estados esperados. O alias `/kanban` é
   intencionalmente ausente.
5. Em `/notificacoes`, confira itens lidos e não lidos.
6. Crie uma pessoa de Alfa por Maria, confira a linha em modo somente leitura no
   DBeaver e confirme que Clara continua restrita a Gama; prove também a negação
   server-side, não apenas a ausência visual.

O manifesto em
`docs/verification/e2e-real-fixtures-test-cases.md` separa evidência automática
e inspeção manual.

## Encerrar e conferir cleanup

Use `Ctrl+C` somente quando o responsável encerrar a sessão e aguarde o processo
sair. O launcher encerra Clara Vite, Maria Vite e o host composto. Dentro do
host, Clara, Maria, o arquivo/diretório protegido e o Testcontainer encerram em
ordem reversa. As portas `4174`, `4175`, APIs dinâmicas, PostgreSQL dinâmico e o
arquivo protegido devem desaparecer. Não use `pkill`, remoção por porta nem
prune amplo.

Se o terminal foi fechado de forma abrupta, atribua primeiro o PID, a worktree
e os labels do container antes de encerrar qualquer recurso. Preserve tudo que
tiver dono ambíguo.

## Diagnóstico seguro

Falhas do host expõem somente uma destas categorias:

- `fixture_start_failed`
- `seed_failed`
- `host_start_failed`
- `readback_failed`
- `credential_export_failed`
- `cleanup_failed`

O launcher rejeita readiness inválida, timeout, profile desconhecido e porta
ocupada com saída não zero. Ele não imprime connection string, senha, claims,
e-mail, IDs do dataset, payloads ou mensagens internas de provider.

## Limites de evidência

Este fluxo prova migrations, seed, autorização/RLS, session gate de teste,
ApiHost real, proxy Vite, leitura dos módulos contra PostgreSQL real e, apenas
no modo compartilhado, a demonstração determinística do endpoint do Agente.
O roteiro Pagaí determinístico acima está compilado, mas sua prova runtime fica
pendente quando Docker/Testcontainers não são autorizados.
Não prova login Supabase, MFA, provider externo, qualidade de IA, WorkerHost,
Meta, ClamAV, Storage, TLS/ingress, produção, reconnect ou revogação de sessão.
A automação browser real completa permanece no escopo de #115.
