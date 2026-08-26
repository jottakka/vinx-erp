# Atendimento operacional — prioridade, canal e prazo

## Status

Desenho aprovado pelo responsável na conversa de 26 de agosto de 2026. A
estimativa RED real do frontend confirmou a necessidade de duas PRs verticais:
`codex/atendimento-operational-api`, empilhada sobre
`codex/professional-multicompany-ui` no commit `f57d952`, e
`codex/atendimento-operational-v1`, empilhada sobre a API. A divisão preserva o
limite de 800 linhas sem reduzir testes e não depende da PR de Agente/OCR. A
execução é rastreada pela issue
[#334](https://github.com/jottakka/vinx-erp/issues/334), filha do épico #50.

## Objetivo e valor

Transformar o cartão atual em um registro operacional útil sem criar um motor
genérico de quadro. Ao criar e consultar um atendimento, a pessoa deve ver sua
prioridade, o canal de origem e um prazo explícito, com indicação textual de
atraso. Os quatro estados atuais, a empresa explícita, as permissões e todas as
garantias de concorrência permanecem iguais.

## Escopo das duas PRs

1. Acrescentar ao cartão:
   - `priority`: `baixa | media | alta | critica`;
   - `channel`: `manual | web | whatsapp | email`;
   - `slaDueAt`: `timestamp with time zone` opcional;
   - `createdAt`, `updatedAt` e `isOverdue` na resposta pública; os dois
     instantes já são persistidos e o atraso é calculado, nunca armazenado.
     Criação e fila novas sempre retornam os instantes; replay de receipt
     legado pode retorná-los como `null`, pois o JSON antigo não os preservou.
2. Estender somente a criação e a leitura. A criação aceita os três campos;
   quando omitidos, usa `media`, `manual` e `null`.
3. Preservar o hash/receipt de idempotência legado quando os novos campos usam
   exatamente os defaults e não há anexo nem participantes. Um receipt antigo
   sem as novas propriedades deve desserializar como os mesmos defaults.
4. Mostrar os campos nos cartões e no detalhe atual, sem UUIDs, com cores,
   rótulos e ajuda em português.
5. Incluir os novos campos no formulário de criação e no comando de retry. Uma
   mudança em prioridade, canal ou prazo precisa gerar payload novo; um retry
   de resultado ambíguo conserva o mesmo `requestId` e o payload exato.
6. Derivar `isOverdue` no servidor a partir de um único instante UTC capturado
   por operação: há atraso quando `slaDueAt` é menor que esse instante e a
   coluna não é `concluido` nem `cancelado`. O banco nunca persiste um booleano
   que possa ficar desatualizado; a UI apenas apresenta o valor autorizado.
7. Tornar a ordem da fila determinística por `createdAt DESC, id DESC`, sem
   mudar o envelope ou criar paginação nesta PR.

## Exclusões e stacks seguintes

Esta PR não entrega:

- filtros server-side, busca ou paginação;
- paginação agregada entre várias empresas;
- categoria company-scoped ou administração de categorias;
- edição de prioridade, canal ou prazo depois da criação;
- timeline/histórico visível e nomes dos atores;
- notificações, outbox novo, ingestão WhatsApp/e-mail, IA, OCR, chatbot, MCP;
- cliente/solicitante externo, engine de SLA, calendário comercial ou
  escalonamento automático;
- nova coluna, regra ou engine de Kanban.

As exclusões não são descartes. A segunda PR operacional tratará atualização,
filtros e paginação. A terceira tratará categoria e timeline, depois que o
contrato de paginação multiempresa for definido. Customers continuará sendo a
fonte futura de solicitantes externos.

## Contrato de domínio e banco

`service_desk.atendimento_cards` recebe:

| Coluna | Contrato |
| --- | --- |
| `priority` | `varchar(10) NOT NULL DEFAULT 'media'`, check fechado dos quatro valores. |
| `channel` | `varchar(12) NOT NULL DEFAULT 'manual'`, check fechado dos quatro valores. |
| `sla_due_at` | `timestamp with time zone NULL`. |

A migration preserva linhas existentes com os defaults. O downgrade é
fail-closed quando alguma linha contém prioridade/canal não padrão ou prazo:
esses dados não podem ser descartados silenciosamente. RLS, grants e chaves
estrangeiras existentes não mudam. A consulta da fila passa a consumir um
índice parcial `(company_id, created_at DESC, id DESC)` para cartões vivos; não
são adicionados índices de combinações de filtros ainda inexistentes.

O domínio usa constantes/allowlists fechadas; nenhum valor controlado pelo
browser é transformado em nome de coluna, SQL ou label não validada. `slaDueAt`
aceita qualquer instante ISO válido, inclusive passado, porque a fila pode
registrar trabalho já atrasado. A UI recebe `DateTimeOffset` e formata no fuso
do navegador; a API e o banco preservam o instante.

## Contrato HTTP

`POST /atendimento/cards` continua estrito, autenticado, company-scoped,
protegido por antiforgery para cookie BFF e idempotente. O corpo adiciona:

```json
{
  "priority": "alta",
  "channel": "web",
  "slaDueAt": "2026-08-28T18:00:00Z"
}
```

As três propriedades são opcionais para compatibilidade. Prioridade/canal
ausentes ou `null` usam os defaults do servidor; valor vazio ou desconhecido
recebe `400 invalid_request`. `slaDueAt` ausente ou `null` significa sem prazo.
Essa equivalência evita um parser próprio apenas para distinguir propriedade
ausente de JSON `null`.

`GET /atendimento/cards?companyId=` continua retornando a lista atual por uma
empresa. Cada item adiciona `priority`, `channel`, `slaDueAt`, `isOverdue`,
`createdAt` e `updatedAt`. Os dois últimos são anuláveis apenas para replay de
receipt legado; a fila e respostas novas sempre os preenchem. A resposta usa
ordem estável `createdAt DESC, id DESC`. Não há mudança de envelope nesta PR,
evitando uma quebra pública antes da paginação multiempresa ser decidida.

## Autorização, auditoria e concorrência

- Criar continua exigindo `service_desk.ticket.create` em escopo Company.
- Ler continua exigindo `service_desk.ticket.read_queue` em escopo Company.
- Nenhum novo grant é concedido por migration.
- A empresa vem do comando explícito e do contexto RLS, nunca de texto ou
  metadado do canal.
- O receipt de idempotência e o evento de auditoria continuam na mesma
  transação do cartão.
- O payload hash inclui os valores normalizados quando qualquer novo campo não
  é default.
- Mesmo `requestId` e mesmo payload devolvem replay sem duplicar cartão.
- Mesmo `requestId` com prioridade, canal ou prazo diferente devolve
  `409 idempotency_conflict`.
- O contrato de versão para move/assign/cancel continua inalterado.

## UX e aparência

O formulário usa os controles MUI existentes:

- `Prioridade`: select, default `Média`;
- `Canal`: select, default `Web` no formulário, pois a criação ocorreu neste
  canal; `Manual` continua disponível para registrar demanda recebida fora dos
  canais digitais. O default de compatibilidade do backend permanece `manual`;
- `Prazo`: `datetime-local`, opcional, com label sempre reduzida;
- texto de ajuda curto: `Data limite operacional; não agenda uma ação.`

Os cartões preservam `CompanyIdentity` e apresentam:

- prioridade com texto e cor: Crítica/error, Alta/warning, Média/info,
  Baixa/default;
- canal em chip outlined: Manual, Web, WhatsApp ou E-mail;
- prazo formatado em português;
- `Atrasado` com texto e tom de alerta, nunca apenas cor;
- nenhum ID interno, URL privada ou comentário técnico.

O detalhe atual recebe os mesmos metadados e continua responsivo. A coluna
`Cancelados` usa seu tom terminal; atraso nunca aparece em `concluido` ou
`cancelado`.

## BDD

| Classe | Dado / Quando / Então |
| --- | --- |
| Sucesso | Dado um ator autorizado, quando cria com prioridade Alta, canal Web e prazo, então o cartão e uma nova leitura retornam exatamente esses valores e a UI os mostra com rótulos profissionais. |
| Sucesso/compatibilidade | Dado o payload legado sem os novos campos, quando cria ou repete um receipt antigo, então o resultado usa Média, Manual e sem prazo sem alterar o hash legado. |
| Validação | Dado prioridade/canal vazio, nulo ou desconhecido, ou data malformada, quando envia, então recebe `400 invalid_request` e nenhum cartão/receipt/auditoria de sucesso é criado. |
| Acesso | Dado ator sem membership/capability ou empresa forjada, quando cria/lê, então recebe a negação tipada existente, não recebe dados de outra empresa e não amplia grants. |
| Estado | Dado prazo vencido em cartão aberto, quando renderiza, então mostra `Atrasado`; ao concluir ou cancelar o mesmo cartão, a indicação deixa de aparecer. |
| Exceção | Dada falha de auditoria/DB, quando cria, então cartão, receipt e auditoria de sucesso são revertidos e a API retorna indisponibilidade segura. |
| Concorrência | Dado o mesmo `requestId`, quando dois creates iguais concorrem, então existe um cartão e um replay; quando um novo campo difere, um lado recebe conflito e nenhum segundo cartão é criado. |
| UX | Dado viewport mobile ou desktop, quando abre o formulário/detalhe, então selects parecem selects, o prazo não sobrepõe label e as ações permanecem acessíveis. |

## Estimativa e limite

Estimativa da PR de API:

- 10–12 arquivos não teste, incluindo migration, snapshots e documentação;
- 500–650 linhas manuais, incluindo testes e esta especificação;
- 1.890–2.150 linhas geradas de migration designer, EF snapshot e schema
  snapshot, reportadas separadamente;
- nenhuma dependência nova.

Estimativa medida da PR web:

- 4 arquivos não teste e 3 arquivos de teste;
- aproximadamente 515 linhas manuais;
- nenhum artefato gerado ou dependência nova.

Se qualquer PR superar 25 arquivos contados ou 800 linhas manuais, o escopo
congela antes do RED seguinte. Filtros, paginação, update, categoria e timeline
não podem ser puxados para nenhuma das duas PRs.

## Verificação proporcional

Durante RED/GREEN:

- testes focados de endpoint/schema/migration/model boundary de Atendimento;
- testes Vitest do client, diálogo e página;
- builds de `Vinx.Erp.Atendimento`, `Vinx.Erp.ApiHost` e web;
- Biome somente nos arquivos web alterados.

Depois do escopo congelado:

- fatia estável completa de Atendimento definida no plano;
- `verify-foundation-database.sh` e `verify-ef-negative-guards.sh`, após
  atribuição do Docker;
- web lint, format, test, build e no-direct-Supabase uma vez;
- uma revisão independente Sol e uma única rodada consolidada de correções;
- CI como gate público completo da PR empilhada.

Nenhum teste acessa `quality/private`, nenhum provider externo é chamado e o
run Maria/Clara atual não é interrompido para esta fatia.
