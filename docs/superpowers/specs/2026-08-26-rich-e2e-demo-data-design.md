# Dados comerciais ricos para o E2E real — desenho

## Status

Proposta pronta para revisão. Esta entrega registra somente a especificação;
não altera seed, banco, host, UI ou testes executáveis. A implementação não
está aprovada e só poderá começar depois que o responsável revisar e aprovar
este documento.

## Objetivo e valor

Enriquecer o fixture E2E real com volume comercial suficiente para avaliar
filtros multiempresa, listas, estados, relações e negações de autorização sem
depender de dados reais. Maria deve demonstrar uma operação ampla em Alfa e
Beta; Clara deve demonstrar um perfil restrito em Gama no mesmo PostgreSQL.

O valor observável é uma sessão descartável na qual o responsável consegue
comparar duas empresas autorizadas, uma empresa isolada, vínculos de pessoas,
rascunhos financeiros, fila de Atendimento, notificações e o Agente
determinístico. O dataset continua pequeno o bastante para inspeção manual e
asserções exatas.

## Escopo e exclusões explícitas

Esta fatia altera somente o dataset test-only e seus readbacks, testes e
documentação operacional. Ela reutiliza as entidades, constraints, services e
rotas já existentes de Foundation, Pagaí, Atendimento, Notifications e Agente.
IDs, datas, nomes, valores e relações serão determinísticos.

Ficam explicitamente fora desta fatia:

- qualquer tabela, rota, tela ou conceito novo de cadastro de clientes ou CRM;
- prioridade, SLA, escalonamento, automação ou configuração de quadro;
- estado novo de Pagaí, pagamento, aprovação, provedor, WorkerHost ou outbox;
- migration, snapshot, importação, seed de produção ou alteração no catálogo
  canônico de capabilities;
- login real, Supabase, Storage, conteúdo binário, provider de IA, segredo,
  dado pessoal real ou concessão de capability fora do fixture.

Não haverá mudança de frontend. Campos já retornados mas ainda não renderizados
pela UI, como participantes e anexo de Atendimento, serão provados pelo HTTP e
pela inspeção administrativa somente leitura; o documento não promete uma
visualização que a tela atual não possui.

## Dependências e limites atuais

| Dependência | Contrato reutilizado |
|---|---|
| Fixture compartilhado | `SharedE2eFixtureHost` possui um PostgreSQL e abre dois ApiHosts de principal fixo, Maria e Clara, por leases não proprietários. |
| Seed | `E2eFixtureSeeder.SeedAsync` exige banco vazio, insere em uma transação e `VerifyAsync` confere a assinatura exata. |
| Pagaí | `GET/POST /paguai/requests`; apenas `rascunho`, BRL, duas casas, solicitante atual e `invoiceFileId` opcional da mesma empresa. |
| Atendimento | `/atendimento/cards` e `/atendimento/assignment-options`; estados `novo`, `em_andamento`, `concluido`, `cancelado`, participantes, um anexo opcional e concorrência por versão. |
| Pessoas, inbox e Agente | Diretório condicionado a `foundation.employees.create`; inbox pessoal por memberships ativas; `POST /agent/runs` com modelo determinístico somente no host compartilhado. |

O diretório atual representa uma pessoa em `foundation.users`, seu vínculo em
`foundation.memberships` e a projeção listável em `foundation.employees`. Ele
expõe apenas nome, `employeeId` e `membershipId`; não possui cargo comercial,
departamento, telefone ou perfil detalhado. Papéis continuam sendo autorização,
não um novo cadastro de RH.

## Fronteira da futura entrega e estimativa

A implementação prevista é uma única fatia vertical com seis arquivos
alterados. Quatro arquivos são contados pelo limite do repositório; os dois
arquivos de teste não entram na contagem de arquivos, mas suas linhas entram na
contagem manual.

| Arquivo futuro | Mudança permitida | Contagem de arquivo |
|---|---|---|
| `tests/public/Vinx.Erp.Foundation.IntegrationTests/Infrastructure/E2eFixtureData.cs` | Dados, inserts parametrizados e verificação canônica. | contado |
| `tests/public/Vinx.Erp.Foundation.IntegrationTests/Infrastructure/E2eFixtureHost.cs` | Readback exato dos perfis e negações. | contado |
| `docs/runbooks/real-e2e-fixture.md` | Quantidades e roteiro manual atualizados. | contado |
| `docs/verification/e2e-real-fixtures-test-cases.md` | Mapeamento BDD curto e evidência. | contado |
| `tests/public/Vinx.Erp.Foundation.IntegrationTests/E2eFixtureSeederTests.cs` | Atomicidade, relações, contagens e assinaturas. | teste, não contado |
| `tests/public/Vinx.Erp.Foundation.IntegrationTests/RealE2eFixtureHostTests.cs` | Readback HTTP, RLS compartilhado e Agente. | teste, não contado |

Estimativa: **4 arquivos contados, 6 arquivos alterados, 680–780 linhas
manuais e 0 linhas geradas**. O recorte fica abaixo dos limites de 25 arquivos
e 800 linhas manuais. `SharedE2eFixtureHost`, o modelo determinístico, o
executável, o launcher, o proxy Vite, os clientes web, migrations, snapshots e
lockfiles não devem mudar. Se o contrato exigir um sétimo arquivo ou ultrapassar
800 linhas manuais, a implementação deve congelar o escopo e recalcular a fatia
antes de continuar.

## Arquitetura, owner único e estratégia de RLS

O modo `--profiles maria,clara` preserva um único owner:
`SharedE2eFixtureHost` cria, migra, semeia e verifica exatamente um PostgreSQL.
Maria e Clara recebem factories com leases não proprietários; nenhum ApiHost
pode reseed, resetar ou descartar o banco.

O seed é administrativo apenas na preparação e executa uma vez, em uma única
transação. Não usa APIs, `ON CONFLICT`, `DO NOTHING`, importador ou merge de
linhas. Qualquer FK inválida, capability ausente ou violação de constraint
aborta toda a transação. `VerifyAsync` compara IDs, quantidades, relações,
estados, versões e campos de lifecycle antes de iniciar os hosts.

Depois da preparação, toda evidência de aplicação passa pelos principals fixos,
membership, capability, contexto transacional e RLS existentes. Maria e Clara
compartilham fisicamente o banco, mas cada requisição aplica seu ator e empresa;
uma consulta administrativa serve somente para conferir persistência e nunca é
tratada como prova de autorização.

Não haverá bypass, header seletor de identidade ou company sem contexto. O
browser continua sem conhecer claims, conexão ou senha. Uma mutação manual
contamina a sessão compartilhada por definição; para recuperar o estado
determinístico, o responsável encerra a execução e inicia outra.

## Regras do dataset sintético

Todos os nomes são personas de teste e todos os e-mails usam `.example.test`.
Beneficiários usam nomes numerados de fornecedor fictício. Não haverá CPF,
CNPJ, telefone, endereço, conta bancária, conteúdo médico, mensagem externa,
prompt persistido, token ou conteúdo de arquivo.

As datas usam a âncora UTC fixa do fixture. Valores monetários são `decimal`
BRL exatos. IDs e request IDs pertencem a famílias UUID determinísticas e não
serão exibidos no roteiro destinado ao usuário.

### Totais canônicos propostos

| Conjunto | Quantidade | Distribuição/garantia |
|---|---:|---|
| Grupo / empresas | 1 / 3 | `Vinx Demo Group`; Alfa, Beta e Gama. |
| Pessoas / identidades externas | 10 / 10 | Somente Maria, Beatriz, João e Clara permanecem profiles allow-listed. |
| Memberships / employees | 12 / 12 | Alfa 5, Beta 5, Gama 2; um employee por membership. |
| Unidades / equipes / papéis | 3 / 3 / 5 | Mantém as estruturas atuais; não inventa lotação de employee em unidade/equipe. |
| Assignments / grants test-only | 12 / 24 | Um papel por membership; grants Company somente de capabilities canônicas existentes. |
| Metadados de arquivo | 8 | Quatro faturas Pagaí e quatro anexos Atendimento, todos vivos e `confirmed`. |
| Rascunhos Pagaí | 12 | Alfa 5, Beta 5, Gama 2; todos `rascunho`, versão 1 e BRL. |
| Cards / históricos / participantes | 12 / 13 / 16 | Três cards por estado; relações sempre da mesma empresa. |
| Notificações in-app | 10 | Maria 7 e Clara 3; 4 lidas e 6 não lidas. |

## Pessoas, vínculos e papéis

| Pessoa sintética | Vínculos e papel |
|---|---|
| Maria Silva | Alfa/Operations e Beta/Reader; profile amplo compartilhado. |
| Beatriz Costa | Alfa/Atendimento agent. |
| João Santos | Beta/Finance operator. |
| Clara Oliveira | Gama/Restricted; profile restrito compartilhado. |
| Ana Comercial | Alfa/Operations. |
| Bruno Financeiro | Alfa/Operations. |
| Daniela Compras | Beta/Finance operator. |
| Eduardo Comercial | Beta/Reader. |
| Renata Supervisão | Alfa/Operations e Beta/Reader. |
| Gustavo Auditoria | Gama/Restricted. |

Cada linha acima possui `user`, external identity sintética, membership ativa,
employee ativo e assignment do mesmo tenant. Maria e Renata são as duas pessoas
multiempresa. O profile catalog continua aceitando exatamente `maria`,
`beatriz`, `joao` e `clara`; adicionar external identity para uma persona de
lista não cria uma sessão nem a torna selecionável no browser.

O modelo não permite armazenar cargo comercial ou departamento no employee.
Os nomes de papéis expressam somente o conjunto de capabilities usado pelo
fixture. A página `/pessoas` mostra os cinco nomes de Alfa e os cinco de Beta
para Maria; não deve alegar que exibe papel, e-mail, unidade ou equipe.

| Papel | Assignments | Capabilities Company exatas |
|---|---:|---|
| Alfa/Operations | 4 | `foundation.employees.create`, `paguai.draft.read`, `paguai.draft.create`, `service_desk.ticket.read_queue`, `service_desk.ticket.create`, `service_desk.ticket.assign`, `service_desk.ticket.move`, `ai.read` |
| Alfa/Atendimento agent | 1 | `foundation.employees.create`, `service_desk.ticket.read_queue`, `service_desk.ticket.create`, `service_desk.ticket.assign`, `service_desk.ticket.move` |
| Beta/Reader | 3 | `foundation.employees.create`, `paguai.draft.read`, `paguai.draft.create`, `service_desk.ticket.read_queue`, `service_desk.ticket.create`, `service_desk.ticket.assign`, `service_desk.ticket.move`, `ai.read` |
| Beta/Finance operator | 2 | `paguai.draft.read`, `paguai.draft.create` |
| Gama/Restricted | 2 | `service_desk.ticket.read_queue` |

Os cinco conjuntos somam 12 assignments e 24 grants. Compartilhar um papel
compartilha suas capabilities entre as personas designadas; não cria profile,
sessão nem acesso fora da empresa do assignment.

## Dados Pagaí

A lista Pagaí é privada ao solicitante mesmo quando a pessoa possui
`paguai.draft.read`. Por isso, o dataset distribui solicitações entre personas,
mas atribui três rascunhos de Alfa e três de Beta a Maria para que a UI ampla
tenha seis exemplos visíveis.

| Empresa | Título sintético | Solicitante | Beneficiário sintético | Valor | Pagar até | Prova | Fatura |
|---|---|---|---|---:|---|---|---|
| Alfa | Licenças de colaboração | Maria | Fornecedor Exemplo Alfa 01 Ltda. | 1.299,90 | 2026-08-15 | required | sim |
| Alfa | Material de escritório | Beatriz | Fornecedor Exemplo Alfa 02 Ltda. | 480,50 | 2026-08-20 | optional | não |
| Alfa | Campanha regional | Maria | Fornecedor Exemplo Alfa 03 Ltda. | 3.200,00 | 2026-09-05 | required | não |
| Alfa | Participação em feira | Ana | Fornecedor Exemplo Alfa 04 Ltda. | 7.850,00 | 2026-09-18 | required | sim |
| Alfa | Manutenção preventiva | Maria | Fornecedor Exemplo Alfa 05 Ltda. | 950,00 | 2026-09-28 | optional | não |
| Beta | Serviços contábeis | Maria | Fornecedor Exemplo Beta 01 Ltda. | 8.500,00 | 2026-08-10 | required | sim |
| Beta | Hospedagem em nuvem | João | Fornecedor Exemplo Beta 02 Ltda. | 2.340,75 | 2026-08-25 | required | não |
| Beta | Pesquisa comercial | Maria | Fornecedor Exemplo Beta 03 Ltda. | 1.750,00 | 2026-09-02 | optional | não |
| Beta | Suprimentos técnicos | Daniela | Fornecedor Exemplo Beta 04 Ltda. | 2.190,30 | 2026-09-12 | required | sim |
| Beta | Treinamento de vendas | Maria | Fornecedor Exemplo Beta 05 Ltda. | 4.600,00 | 2026-09-25 | optional | não |
| Gama | Auditoria documental | Clara | Fornecedor Exemplo Gama 01 Ltda. | 6.100,00 | 2026-09-08 | required | não |
| Gama | Arquivo físico seguro | Gustavo | Fornecedor Exemplo Gama 02 Ltda. | 780,00 | 2026-09-22 | optional | não |

São sete `required`, cinco `optional`, quatro com `invoiceFileId` e oito sem
fatura. Cada solicitante possui membership na empresa da solicitação. As quatro
faturas referenciam metadados `confirmed`, vivos, da mesma empresa e com
classificação exata `paguai_request_invoice`.

O seed não cria outros estados, comprovante de pagamento, fornecedor
autoritativo, transição, aprovação ou pagamento. Maria lê somente os seis
rascunhos próprios de Alfa/Beta. Os dois rascunhos Gama são sentinelas de
isolamento e não ficam visíveis a Clara, cujo papel não possui capability
Pagaí.

## Dados Atendimento

| Empresa / caso | Estado | Responsável | Participantes | Anexo | Evolução / versão |
|---|---|---|---|---|---|
| Alfa — Nova solicitação comercial | novo | — | Ana, Renata | sim | sem histórico / 1 |
| Alfa — Dúvida sobre contrato | novo | Beatriz | Beatriz | não | atribuição / 2 |
| Alfa — Revisar integração do portal | em_andamento | Beatriz | Beatriz, Bruno | sim | `novo → em_andamento` / 3 |
| Alfa — Acompanhar renovação anual | em_andamento | Renata | Renata, Ana | não | `novo → em_andamento` / 3 |
| Alfa — Orientação concluída | concluido | Beatriz | Beatriz | não | `novo → em_andamento → concluido` / 4 |
| Alfa — Solicitação duplicada | cancelado | — | — | não | `novo → cancelado` / 2 |
| Beta — Nova oportunidade regional | novo | Eduardo | Eduardo, Renata | não | atribuição / 2 |
| Beta — Conferir divergência de cobrança | em_andamento | João | João, Daniela | sim | `novo → em_andamento` / 3 |
| Beta — Configuração de acesso concluída | concluido | Renata | Renata | não | `novo → em_andamento → concluido` / 4 |
| Beta — Pedido aberto por engano | cancelado | — | — | não | `novo → em_andamento → cancelado` / 3 |
| Gama — Revisão interna concluída | concluido | Clara | Clara, Gustavo | sim | `novo → em_andamento → concluido` / 4 |
| Gama — Registro encerrado por duplicidade | cancelado | Clara | Clara | não | atribuição e `novo → cancelado` / 3 |

O total é exatamente três cards em cada um dos quatro estados, nove cards com
responsável, dez com participantes, dezesseis relações de participante, quatro
com anexo e oito sem anexo. Todo responsável também aparece entre os
participantes do card; assignee, participante, criador, atualizador, cancelador
e arquivo pertencem à mesma empresa.

Os três cancelados recebem `cancelled_at`, `cancelled_by_user_id` e motivo
sintético completo. Maria cancela o card Alfa, João cancela o Beta e Clara
cancela o Gama. Os motivos são curtos, operacionais e sem conteúdo de pessoa.
Cada cancelamento possui histórico até `cancelado`; nenhuma linha parte de
`cancelado`. As versões reproduzem atribuições e mudanças já ocorridas.

| Card cancelado | Cancelador | Motivo exato |
|---|---|---|
| Alfa — Solicitação duplicada | Maria | `Solicitação registrada em duplicidade.` |
| Beta — Pedido aberto por engano | João | `Pedido aberto por engano na fila interna.` |
| Gama — Registro encerrado por duplicidade | Clara | `Registro duplicado na fila interna.` |

Os quatro anexos referenciam arquivos vivos `confirmed` e classificados como
`service_desk_attachment`. A API retorna somente o UUID. A tela atual mostra
estado e presença de responsável, mas não apresenta participantes, arquivo ou
motivo de cancelamento; essas três relações serão verificadas por HTTP e SQL
administrativo somente leitura, sem ampliar a UI.

## Arquivos de demonstração sem conteúdo

Os oito registros de `foundation.files` contêm apenas metadados sintéticos
necessários às FKs e validações atuais: nome `.pdf`, MIME `application/pdf`,
tamanho pequeno, checksum SHA-256 determinístico, classificação allow-listed,
uploader da mesma empresa, retenção futura fixa, lifecycle completo e chave
técnica opaca do fixture.

| Classificação | Relação | Uploader da mesma empresa |
|---|---|---|
| `paguai_request_invoice` | Licenças de colaboração / Alfa | Maria |
| `paguai_request_invoice` | Participação em feira / Alfa | Ana |
| `paguai_request_invoice` | Serviços contábeis / Beta | Maria |
| `paguai_request_invoice` | Suprimentos técnicos / Beta | Daniela |
| `service_desk_attachment` | Nova solicitação comercial / Alfa | Maria |
| `service_desk_attachment` | Revisar integração do portal / Alfa | Beatriz |
| `service_desk_attachment` | Conferir divergência de cobrança / Beta | João |
| `service_desk_attachment` | Revisão interna concluída / Gama | Clara |

O estado `confirmed` prova somente que as relações de domínio aceitam um arquivo
confirmado. Não existe objeto remoto, URL assinada, download, bytes, scan real
ou chamada a Storage. O roteiro não deve clicar, baixar nem interpretar esses
metadados como evidência de provedor.

## Notificações

| Destinatário / empresa | Total | Lidas / não lidas | Ações seguras |
|---|---:|---:|---|
| Maria / Alfa | 4 | 2 / 2 | 2 `/paguai`, 2 `/atendimento` |
| Maria / Beta | 3 | 1 / 2 | 2 `/paguai`, 1 `/atendimento` |
| Clara / Gama | 3 | 1 / 2 | 3 `/atendimento` |
| Total | 10 | 4 / 6 | 4 `/paguai`, 6 `/atendimento` |

Cada intent é `in_app`, `delivered`, tentativa 1, possui lifecycle consistente
e correlaciona-se pelo `correlation_id` ao request ID de um rascunho ou card do
mesmo tenant. Não há conteúdo, telefone, endereço, provider externo ou payload
de negócio.

As `event_key` sintéticas continuam fora do mapa de apresentação; portanto a
UI pode mostrar `Sistema` e evento nulo. Isso é o comportamento seguro atual,
não uma deficiência que esta fatia de dados deva contornar. Apenas os deep
links completos já allow-listed produzem ações.

## Exemplos determinísticos do Agente

O modelo de teste atual permanece inalterado e não consulta o dataset. A
mensagem usada nos testes é sintética, não sensível e não será copiada para
logs, fixture ou documentação. A resposta fixa prova somente composição e a
ordem autorização-modelo; não é um resumo dos dados comerciais.

| Principal / empresa | Resultado exato | Chamada ao modelo |
|---|---|---:|
| Maria / Alfa | HTTP 200, `result/ok`, texto fixo de demonstração | 1 |
| Maria / Beta | HTTP 200, `result/ok`, o mesmo texto fixo | 1 |
| Clara / Gama | HTTP 200, `denied/capability_denied`, sem texto | 0 |
| Maria / Gama | HTTP 200, `denied/company_not_authorized`, sem texto | 0 |

Os dois sucessos usam request IDs distintos. Um replay idêntico de um deles
retorna o mesmo recibo sem terceira chamada ao modelo; reutilizar seu request ID
com payload divergente retorna conflito sem chamar o modelo.

## Matriz exata Maria ampla × Clara restrita

| Superfície | Maria / Alfa | Maria / Beta | Clara / Gama |
|---|---|---|---|
| Membership e bootstrap | visível | visível | visível; Alfa/Beta ausentes |
| Pessoas | lista/cria; `foundation.employees.create` | lista/cria; `foundation.employees.create` | sem capability; rota direta 403 |
| Pagaí | lê/cria os próprios; `paguai.draft.read/create` | lê/cria os próprios; `paguai.draft.read/create` | sem capability; rota direta 403 |
| Atendimento | lê/cria/atribui/move/cancela; `read_queue/create/assign/move` | lê/cria/atribui/move/cancela; `read_queue/create/assign/move` | somente fila; `read_queue` |
| Opções de responsável | permitidas pela capability `assign` | permitidas pela capability `assign` | negadas |
| Agente | permitido; `ai.read` | permitido; `ai.read` | negado por capability |
| Inbox pessoal | 4 itens Alfa | 3 itens Beta | 3 itens Gama |

Maria recebe exatamente oito capabilities Company em cada uma de Alfa e Beta:
`foundation.employees.create`, `paguai.draft.read`, `paguai.draft.create`,
`service_desk.ticket.read_queue`, `service_desk.ticket.create`,
`service_desk.ticket.assign`, `service_desk.ticket.move` e `ai.read`. A leitura
do diretório exige a capability de criação porque esse é o contrato atual; não
será inventada uma capability read-only.

Clara recebe exatamente uma capability Company em Gama:
`service_desk.ticket.read_queue`. Notifications permanece disponível por
destinatário e membership, sem capability de módulo. O frontend projeta essa
matriz, mas servidor e RLS continuam sendo a autoridade.

Em chamadas diretas para Gama, Maria recebe 403 nas rotas People/Pagaí,
`membership_required` no Atendimento e `company_not_authorized` tipado no
Agente. Clara recebe o equivalente ao tentar Alfa ou Beta. Em Gama, mutações de
Atendimento por Clara retornam `capability_denied`; cards cancelados também são
terminais, independentemente do principal.

## Cenários de UI e inspeção manual

| Ordem | Cenário | Resultado observável |
|---:|---|---|
| 1 — iniciar | Uma sessão compartilhada `maria,clara`, um launcher/container e browsers separados. | Maria publica Alfa/Beta; Clara publica apenas Gama; nenhum login ou seletor de principal aparece. |
| 2 — Maria ampla | Abrir `/pessoas`, `/paguai` e `/atendimento`, alternando Alfa/Beta. | 5 employees por empresa; 3 rascunhos próprios por empresa; 6 cards Alfa e 4 Beta, cobrindo as quatro colunas no conjunto. |
| 3 — relações | Abrir detalhes Pagaí e inspecionar JSON/SQL de Atendimento em modo somente leitura. | Maria vê duas faturas entre seus seis rascunhos; cards preservam responsáveis, 16 participantes, 4 anexos e 13 históricos sem FK cruzada. |
| 4 — inbox e Agente | Abrir `/notificacoes` e executar os casos allow-listed sem registrar a mensagem. | Maria vê 7 itens, 3 lidos; Agente responde deterministicamente em Alfa/Beta e não chama provider externo. |
| 5 — Clara e isolamento | Abrir `/atendimento`, `/notificacoes`, Pessoas/Pagaí/Agente e tentar empresa alheia por HTTP. | Clara vê 2 cards e 3 itens Gama, não recebe ações de mutação e não obtém dados Alfa/Beta; as negações server-side confirmam o limite. |

O card cancelado deve aparecer na coluna Cancelados sem controles de movimento,
atribuição ou cancelamento. O motivo não é visível na UI atual e só pode ser
conferido por SQL administrativo. Qualquer mutação manual deve ser registrada
como estado contaminado; uma sessão nova restaura o baseline.

## BDD verificável

### Sucesso

| Dado | Quando | Então | Evidência futura |
|---|---|---|---|
| PostgreSQL migrado e vazio | o seed executa | as quantidades canônicas e todas as FKs/assinaturas acima existem em um único commit | `E2eFixtureSeederTests` |
| Maria e Clara em hosts fixos | bootstrap e módulos são lidos simultaneamente | Maria recebe Alfa/Beta e Clara somente Gama, sem troca de principal | `RealE2eFixtureHostTests` |
| rascunhos, cards, participantes e arquivos coerentes | os endpoints listam os módulos | cada resposta contém somente o próprio tenant/solicitante e referências confirmadas esperadas | ambos os testes futuros |
| modelo determinístico injetado | Maria executa Alfa e Beta | há dois resultados fixos e exatamente duas chamadas ao modelo | `RealE2eFixtureHostTests` |

### Validação e erros

| Dado | Quando | Então | Evidência futura |
|---|---|---|---|
| banco já contém qualquer user | o seed é repetido | falha com banco não vazio, não mescla e o resumo anterior permanece idêntico | `E2eFixtureSeederTests` |
| capability canônica usada pelo plano está ausente | o seed valida o catálogo | toda a transação falha antes de expor readiness | `E2eFixtureSeederTests` |
| participante, assignee ou arquivo pertence a outra empresa ou não existe | a relação é persistida | a FK/constraint recusa e nenhuma linha parcial do seed permanece | testes de schema atuais + atomicidade futura |
| profile ou argumento obrigatório está ausente/inválido | o host é solicitado | o parser recusa antes de iniciar Docker/fixture | testes atuais de options/profile |
| dependência PostgreSQL/Docker falha no startup | o owner inicia o fixture | saída permanece não zero e categorizada, sem host parcial ou dado exposto | lifecycle atual do host |

Não existe limite de volume configurável nesta fatia: o dataset é compilado e
possui quantidades exatas, sem gerador ou entrada de tamanho. Limites de campos,
arquivo, inbox e corpo HTTP continuam cobertos pelos contratos dos módulos e não
serão duplicados no seeder.

### Acesso e tenant

| Dado | Quando | Então | Evidência futura |
|---|---|---|---|
| Maria autenticada | consulta Alfa/Beta e depois Gama | Alfa/Beta seguem a matriz; Gama é ausente no bootstrap e negada no servidor | `E2eFixtureHost`; `RealE2eFixtureHostTests` |
| Clara autenticada | consulta Gama e depois Alfa/Beta | somente fila/inbox Gama são expostas; dados e mutações alheios são negados | mesmos readbacks |
| inbox contém intents para dois recipients | cada principal lista/conta | Maria recebe somente seus 7 e Clara somente seus 3; nenhum filtro cliente amplia escopo | `RealE2eFixtureHostTests` |
| dois ApiHosts usam o mesmo banco | leituras Maria/Clara ocorrem em paralelo | RLS não cruza empresas nem recipients | teste compartilhado estratégico |

### Estado e exceções

| Dado | Quando | Então | Evidência futura |
|---|---|---|---|
| 12 rascunhos Pagaí | seed e readback executam | todos permanecem `rascunho`, BRL, versão 1; nenhum estado futuro é criado | `E2eFixtureSeederTests` |
| cards nos quatro estados | assinatura é conferida | há três por estado, 13 históricos e versões coerentes com atribuições/transições | `E2eFixtureSeederTests` |
| card cancelado completo | assign, move ou novo cancel é tentado | retorna `409 integrity_conflict`, preserva estado/versão/histórico e não cria novo evento | testes atuais de endpoint + readback futuro |
| arquivo metadata-only confirmado | Pagaí/Atendimento leem a referência | só UUID é exposto; nenhum download, URL ou provider é acionado | readback HTTP + SQL somente leitura |
| startup/readback falha após adquirir recurso | cleanup executa | Clara, Maria, credencial e PostgreSQL são descartados em ordem e a falha original é preservada | lifecycle atual/futuro |

### Concorrência e idempotência

| Dado | Quando | Então | Evidência futura |
|---|---|---|---|
| um owner e banco fresco | duas tentativas de seed disputam o mesmo banco | no máximo uma confirma; a outra falha e não duplica nem mescla linhas | `E2eFixtureSeederTests` estratégico |
| Maria e Clara compartilham o banco | as filas e inboxes são lidas concorrentemente | cada resultado mantém contagem e tenant exatos | `RealE2eFixtureHostTests` |
| execução permitida do Agente já persistiu | o mesmo request ID/payload é repetido | o recibo é replayado sem nova chamada ao modelo | `RealE2eFixtureHostTests` |
| o mesmo request ID do Agente recebe payload divergente | a chamada é repetida | retorna 409, não substitui o recibo e não chama o modelo | `RealE2eFixtureHostTests` |

## Verificação, ownership de recursos e cleanup

A futura implementação executa primeiro o filtro de
`E2eFixtureSeederTests`; depois, com atribuição explícita do daemon, o filtro
serial de `RealE2eFixtureHostTests`. O projeto Foundation Integration é o
consumidor diretamente afetado. Como o diff altera fixture compartilhado e
banco test-only, `./scripts/verify-public.sh` roda no máximo uma vez depois do
congelamento de escopo; o CI continua o gate público completo normal.

Antes de qualquer teste Docker, o responsável identifica contexto, PID/PPID,
cwd/worktree e labels Testcontainers. Se outra worktree, stack Supabase ou dono
ambíguo usar o daemon, o teste espera. Não há `prune`, `pkill`, remoção por porta
ou intervenção em recurso alheio.

A inspeção manual usa uma única sessão compartilhada, um PostgreSQL, dois
ApiHosts e dois Vites. No encerramento, o launcher para os Vites; o owner
descarta Clara, Maria, arquivo/diretório de credencial e PostgreSQL em ordem
reversa. As portas dinâmicas, `4174`, `4175` e o arquivo protegido devem
desaparecer. Somente recursos atribuídos à sessão podem ser encerrados.

Evidência transitória de contagens, PIDs, portas, containers e saída de testes
fica no PR. O repositório mantém apenas esta decisão, o runbook e o manifesto.
Correção documental posterior a um gate verde usa `git diff --check` e não
repete Docker ou gate completo sem mudança executável.

## Migration e importação

**N/A — nenhuma migration ou importação é necessária.** O ambiente sempre
começa em PostgreSQL descartável vazio, aplica as migrations atuais e semeia o
dataset test-only depois delas. Não existe estado legado a converter, backfill,
downgrade, arquivo de importação, dump SQL ou compatibilidade com produção.

Os IDs determinísticos não são chaves de negócio e nunca saem do fixture. A
segunda execução no mesmo banco continua sendo erro deliberado; evolução do
dataset substitui a assinatura no código/teste em vez de migrar uma sessão
descartável existente.

## Adiamentos

| Tema adiado | Motivo e limite | Registro antes de implementar |
|---|---|---|
| Cadastro de clientes / CRM | Não existe entidade ou rota atual aprovada; nomes comerciais em títulos não criam esse domínio. | Nova issue vertical após aprovar objetivo, autoridade, tenancy e UI. |
| Prioridade / SLA | Atendimento não possui campo, regra, relógio, pausa ou escalonamento; inventar texto não provaria comportamento. | Nova issue após definir semântica, estados, horários e observabilidade. |
| Novos módulos ou migrations | As entidades e rotas atuais atendem todo este dataset. | Reavaliar somente se um requisito observável não couber nos contratos existentes. |
| Arquivo real / provider / download | Metadados confirmados não provam Storage, scan ou URL temporária. | Fatia própria com provider testável, segurança e cleanup aprovados. |
| Browser automatizado completo | Esta fatia mantém roteiro manual e readback HTTP; não amplia o launcher. | Continuar no recorte já separado de automação E2E (#115). |

Nenhum adiamento autoriza código antecipado, tabela vazia, capability sem uso,
abstração, dependência ou dado provisório nesta fatia.
