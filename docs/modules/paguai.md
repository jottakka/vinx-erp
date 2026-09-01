# Paguai — linguagem e contrato de descoberta V1

> A descoberta da issue [#45](https://github.com/jottakka/vinx-erp/issues/45) continua canônica para o domínio financeiro futuro. A issue #227 entrega apenas o recorte abaixo e substitui, para esse recorte, a antiga afirmação de que este documento não cria contrato técnico.

## Ownership

- Serviços `PaguaiDraftService` (rascunho, comprovante, link de prova) e
  `PaguaiProposalService` (proposta de documento do Agente) em
  `Vinx.Erp.Paguai`; composição via `AddPaguai` (ApiHost) e
  `AddPaguaiProposal` (WorkerHost).
- Mensagem durável `PaguaiDraftCreated` na fila `paguai-events`
  (`PaguaiQueues.Events`); DLQ redige para `PaguaiDraftCreated(Guid.Empty,
  Guid.Empty, Guid.Empty)`.
- Referência AI → Paguai é a única aresta entre módulos autorizada (o módulo
  AI lê regras de amount/proposta). Exceção registrada: leitura raw-SQL de
  `notifications.read_inbound_whatsapp_invoice_candidate` (ver #345).

## Company scope

Empresa isolada por RLS (`paguai.requests` com policy requester/company);
todo comando abre a transação com o contexto `app.company_id` da sessão.
Replays por `foundation.idempotency_records` com hash canônico por comando.

## Contract version

- Valor: `decimal` com gramática `PaguaiAmountRules` (entrada pt-BR
  `1.250,00`, wire canônico `1250.00`); nunca `double` nem parse
  culture-sensitivo.
- Link de comprovante: TTL de 2 minutos, alinhado ao teto
  `SupabaseFileStorage.MaximumDownloadUrlLifetime` (contrato testado entre
  camadas).

## Deletion strategy

Rascunhos e pedidos vivem na máquina de estados (`draft` → proposta →
solicitação); não há exclusão física na V1. Comprovantes seguem a retenção
do subsistema de arquivos (`foundation.file_records.retention_until`).

## Migration notes

- `20260820061738` converteu `amount_minor` (bigint) para `amount`
  (numeric); replay legado lê `AmountMinor/100m` no shim de desserialização.
- `20260829060000` adicionou CAS por versão no comprovante
  (`proof_version`).

## MVP #227 — rascunho financeiro mínimo

O MVP persiste `id`, `companyId`, `requestId`, `title`, `description`,
`amount`, `currency`, `dueDate`, `beneficiaryDisplayName`,
`proofRequirement`, `state: "rascunho"`, `version: 1` e `createdAt`.
`amount` é um decimal positivo, com no máximo duas casas, persistido como
`numeric(18,2)` no PostgreSQL e `decimal` no C#. O JSON usa texto decimal exato,
com duas casas na resposta; valores com escala superior a duas casas são
rejeitados, sem arredondamento. A
moeda é sempre `BRL`; `dueDate` usa `date`/`DateOnly` e JSON `YYYY-MM-DD`.
`beneficiaryDisplayName` é texto
informativo aparado de 1 a 200 caracteres e não é uma identificação autoritativa.
`proofRequirement` exige escolha explícita entre `optional` e `required`.

`invoiceFileId` é opcional e representa somente a fatura ou documento privado
da solicitação. Quando informado, referencia um arquivo vivo, confirmado, da
mesma empresa e classificado exatamente como `paguai_request_invoice`; a API
retorna apenas o UUID, nunca URL, chave de storage ou metadados do arquivo.
Um UUID vazio é rejeitado na fronteira como `400` antes de qualquer mutação.
O arquivo integra o hash de idempotência. Replays idênticos são resolvidos antes
da validação do arquivo para preservar o recibo mesmo depois de retenção ou
remoção; hashes anteriores, sem `invoiceFileId`, só continuam aceitos quando o
novo comando também não informa fatura. Referência de fatura inválida
(ausente, de outra empresa, não confirmada ou classificação errada) nega a
criação com auditoria `invoice_file_invalid`, sem UUID de arquivo ou metadados.
`proofRequirement` continua sendo a regra futura e independente do
comprovante de pagamento.

No caminho `POST /paguai/requests/from-document`,
`notifications.read_inbound_whatsapp_invoice_candidate` exige, além do
membership do ator, que o contexto RLS `app.company_id` da transação seja
exatamente a empresa do candidato; o serviço revalida `company_id` do
candidato contra a empresa do comando e recusa com `400` sem copiar dados
entre empresas. O documento inbound aceita um único rascunho
(`uq_paguai_requests_source_inbound_message`): a corrida entre request ids
distintos é reconciliada com reautorização, auditoria
`source_document_conflict` e `409`, sem expor dados do rascunho ocupante
além do seu id quando visível ao próprio ator.

Este corte não representa documento binário, cobrança ou pagamento. Também não oferece
edição, remoção, envio, conferência, aprovação, devolução, cancelamento,
integração de provedor, PIX, cartão, WhatsApp, MCP ou automação financeira.
O estado permanece sempre `rascunho`; esses comportamentos permanecem bloqueados
até as decisões de #45. A #355 apenas publica `PaguaiDraftCreated` no mesmo
commit da criação, com `CompanyId`, `DraftId` e `RequesterUserId`, e o worker
cria alertas in-app idempotentes para aprovadores explícitos atuais. Para cada
intent realmente criada, a função worker-only devolve um delivery id e o mesmo
commit persiste um envelope `NotificationDeliveryRequested` por id; replay não
devolve id nem cria envelope. O deep-link persistido é exatamente `/paguai`.
Na inbox, somente a gramática ASCII lowercase
`paguai.<32-hex>.draft-created` projeta `Pagaí` e `Novo rascunho para aprovação`;
qualquer lookalike continua `Sistema` sem ecoar a chave. O evento, os metadados
e os logs não carregam conteúdo financeiro, arquivo, URL ou PII.

O módulo Paguai mantém DTOs, validação, hash e SQL parametrizado fora do
domínio de negócio Foundation. Ele usa o único `FoundationDbContext` injetado
como transação/conexão compartilhada, além de `CapabilityEvaluator` e
`AuditAppender` como contratos transversais. A migration Foundation apenas
implanta o schema `paguai`; ela não torna Foundation dona do comportamento.

As capabilities de escopo `Company` são `paguai.draft.read`
(`766bdb56-80c0-5dee-982f-be7f0d3eaea8`) e `paguai.draft.create`
(`aecaa296-6a6f-5815-93ac-bd1ccdff3f44`). O catálogo é semeado somente por
migration. A tabela `paguai.requests` tem RLS forçado: runtime só enxerga ou
insere a própria solicitação quando `app.user_id` e `app.company_id` explícitos
correspondem; sem contexto, o acesso falha fechado. Runtime recebe somente
`USAGE` no schema e `SELECT`/`INSERT` na tabela; desde o #345 (fatia de
vínculo + fatia de troca), também somente `UPDATE (proof_file_id,
version)` — nunca a tabela inteira; `PUBLIC` não recebe acesso.

A criação valida a estrutura antes da auditoria, abre uma transação, aplica o
contexto RLS, exige membership ativo e capability, e só então consulta a
idempotência. O mesmo `requestId` com o mesmo hash retorna o resultado já
persistido sem nova auditoria; hash diferente recebe `409` e auditoria de
negação. Criação bem-sucedida grava solicitação, recibo Foundation e auditoria
`paguai.draft.create`/`paguai_request` com metadados `{}` no mesmo commit. Não
se registra título, descrição, valor, vencimento, beneficiário, comprovante,
corpo ou tokens em logs/auditoria. Leitura exige
membership e `paguai.draft.read`; lista limita ao solicitante e detalhe fora do
escopo retorna `resource_not_found`.

A migration do corte remove os rascunhos antigos de `paguai.requests` e os
registros de idempotência de `paguai.draft.create` antes de adicionar as novas
colunas `NOT NULL`. O banco aplica `CHECK (currency = 'BRL')`.

A migration de conversão posterior renomeia `amount_minor` para `amount`,
converte centavos para reais em `numeric(18,2)` e preserva os valores já
persistidos. Ela também converte o valor dos recibos de idempotência e mantém
o hash legado aceito pelo replay durante a transição.

## Notificações — primeiro produtor real (P1, faixa B)

A criação do rascunho publica `PaguaiDraftCreated` pelo Wolverine
`IDbContextOutbox.Enroll/PublishAsync` dentro da MESMA transação do insert em
`paguai.requests`, da auditoria e do recibo de idempotência: o envelope durável
só existe se o commit do rascunho vencer, e sem outbox operante a criação falha
fechado (`503 paguai_unavailable`). O worker consome a fila `paguai-events`,
resolve destinatários SÓ NA ENTREGA via
`paguai.list_draft_approver_user_ids(company_id, exclude_user_id)` — memberships
ativas com `paguai.draft.approve`, deduplicadas por usuário, sempre excluindo o
solicitante — e enfileira uma intenção in-app por destinatário com chave
determinística `paguai.<draftId:N>.draft-created` (a unicidade por destinatário
fica na restrição `(event_key, recipient_user_id, channel, template_key)` da
própria tabela de intents; nenhum discriminador extra entra na chave). Falha de
enfileiramento é WARNING best-effort: nudges não são estado de negócio.

A capability `paguai.draft.approve`
(`e3d8be82-1c79-59e7-9dd4-2f8b9c69c55a`) segue o catálogo canônico UUIDv5,
semeada somente por migration e concedida de forma EXPLÍCITA por papéis da
empresa: administradores não ganham autoridade financeira implícita — a
política de acesso do módulo (e o contrato de `ensure_company_default_roles`)
não incluem `paguai.draft.approve` nos papéis padrão.

Pré-requisito de implantação: enquanto o template in-app
`paguai.draft_created.approver` não existir/aprovado na empresa, a intenção NÃO
é registrada (o enfileiramento rejeita) e o evento segue consumido sem erro —
o rascunho criado permanece válido.

Qualquer PR futuro de comportamento Paguai registra
`docs/verification/issue-<numero>[-be|-fe]-test-cases.md`: cenário/BDD, teste ou caminho, intenção e situação. Estimativas contam todos os arquivos não teste, inclusive migrations, snapshots e lockfiles. O limiar de linhas inclui todo conteúdo manual, inclusive testes, e separa apenas linhas geradas de migrations, snapshots e lockfiles.

### Exceção registrada de entrega — #345

A decisão de escopo, autorizada pelo responsável com fundamento na revisão
independente Sol, é uma emenda única ao congelamento sem exceções deste corte.
Incluindo este registro, o PR #357 totaliza 2.199 linhas manuais em 14 caminhos:
858 de produção/documentação/migration e
1.341 de testes públicos, além de 2.214 linhas EF geradas. O conteúdo comportamental
revisado no commit `212858f2` era 2.184 (843 + 1.341); este registro acrescenta
15 linhas manuais de documentação. A fatia CAS já separada na branch
`codex/issue-345-proof-cas` totaliza 911 linhas manuais estritas, incluindo 75
linhas SQL escritas manualmente; ela recebe a mesma exceção para compor a pilha.
Isto não eleva o limiar reutilizável: o escopo create+read+link e CAS permanece
congelado, sem comportamento, dependência, refatoração ou expansão documental adicional.
Qualquer correção executável exige nova contagem antes da prontidão de merge.

## MVP #345 — comprovante do rascunho (documentation-only)

O comprovante é documentação anexada a um rascunho pelo próprio requerente:
vincular ou abrir NÃO confirma pagamento, não cria fato financeiro,
evento, lançamento, notificação, aprovação ou transição de estado. O estado
continua exatamente `rascunho` em toda operação do corte; nenhum selo de
"pago" existe. `proof_file_id uuid NULL` é a única coluna nova (migration
`20260828032135_AddPaguaiRequestProofFile`); não há tabela própria, histórico
nem ciclo de vida independente — o histórico é a auditoria append-only.

Três conceitos independentes: `invoiceFileId` (fatura fundadora),
`proofFileId` (evidência documental de pagamento) e `proofRequirement`
(declaração sobre o que a futura confirmação de pagamento exigirá).
`required` sem comprovante e `optional` com comprovante são ambos válidos; o
MVP não aplica a regra.

**Ator autorizado.** Somente o requerente do rascunho, com membership ativo e
`paguai.draft.create`, pode vincular ou trocar o comprovante; o arquivo deve
ter sido enviado pelo mesmo membership. Admin, aprovador ou rótulo Financeiro
não adicionam nada; rascunho alheio permanece oculto pela RLS requester.
Leitura e link exigem posse mais `paguai.draft.read`, revalidados a cada
chamada.

**Validação na fronteira Files.** O comprovante deve ser não removido, da
mesma empresa e do mesmo uploader, classificado exatamente
`paguai_request_proof`, `confirmed` (pós-quarentena/scan) e com
`retention_until > clock_timestamp()` — regra aplicada no vínculo inicial,
na troca e imediatamente antes de cada link assinado. Arquivo oculto/permanente
inválido no vínculo responde `404 proof_file_not_found`; classe correta ainda
em quarentena responde `400 proof_file_not_ready`; já vinculado em outro
rascunho responde `409 proof_file_bound` (índice parcial único
`uq_paguai_requests_proof_file` — o único 23505 assim classificado).

**Endpoints.** `POST /paguai/requests` aceita `proofFileId` opcional e
atômico com o rascunho (`201` cria; replay idêntico `200`; mesmo `requestId`
com comprovante distinto `409 idempotency_conflict`; o hash canônico permanece
byte-idêntico quando o campo está ausente). `PATCH
/paguai/requests/{id}/proof` com `{ companyId, proofFileId, expectedVersion }`
anexa (`proof_attached`) ou substitui (`proof_replaced`) via CAS: sucesso faz
`version = expectedVersion + 1`; versão defasada responde `409
proof_version_conflict`; substituir não apaga o arquivo anterior nem altera
sua retenção; rascunho ausente/alheio responde `404 resource_not_found` com
negação durable auditada; sessão de cookie mantém antiforgery. `GET
/paguai/requests[/{id}]` expõe apenas o ponteiro e, no detalhe,
nome/MIME/tamanho — nunca chave do provedor, checksum, retenção ou URL. `GET /paguai/requests/{id}/proof-link?companyId=`
devolve `{ url, expiresAt }` sob autorização fresca, `private, no-store`, com
TTL `min(5 min, retenção restante)`; a URL nunca é persistida.

**Auditoria.** Sucesso: `paguai.draft.create` com razão `proof_attached`
(criação com comprovante) e `paguai.draft.replace_proof` com razão
`proof_attached`/`proof_replaced` (troca), sempre no mesmo commit da mutação —
falha de auditoria reverte o vínculo/troca. Negações usam razões controladas
(`proof_file_not_found`, `proof_file_not_ready`, `proof_file_bound`,
`proof_version_conflict`, `resource_not_found`, `capability_denied`); nenhum
UUID, nome de arquivo, conteúdo ou metadado entra em auditoria/logs. Falha
transacional/storage responde `503 paguai_unavailable` sem mutação parcial.

**Privilégio mínimo.** O runtime recebe apenas `GRANT UPDATE (proof_file_id)`
e `GRANT UPDATE (version)` em `paguai.requests`: o SQL de runtime não pode
alterar valor, beneficiário, estado, requerente ou fatura. O Down da migration
de vínculo revoga seu grant e recusa (fail-closed) enquanto houver comprovante
vinculado; a migration de troca (`20260829060000_AllowPaguaiProofVersionCas`)
relaxa para `CHECK (version >= 1)`, concede a coluna da versão e seu Down
revoga, recusa sob trava ACCESS EXCLUSIVE enquanto houver versão `<> 1` e
restaura `CHECK (version = 1)`. Ambas as guardas enxergam através do FORCE
RLS como dono da tabela e travam a tabela antes de ler o estado.

**Troca versionada (fatia empilhada).** Este PR adiciona a substituição de
comprovante via `PATCH /paguai/requests/{id}/proof` com CAS em
`expectedVersion`, o relaxamento `CHECK (version >= 1)` e o
`GRANT UPDATE (version)` sobre a fatia create+read+link do PR #357.

Fora do corte: confirmação de pagamento, execução bancária/PIX, estados
além de `rascunho`, desvinculação/remoção, navegador de comprovantes
substituídos, prorrogação de retenção, upload no próprio Paguai (o upload
segue a fronteira Foundation com classificação exata), enforcement de
`proofRequirement` (pertence à futura confirmação de pagamento) e qualquer
notificação disparada por comprovante.

## Proposta de documento do Agente (#335)

`paguai_from_document_v1` aceita exatamente um `FileRecord` autorizado,
classificado como `agentic_attachment`, e materializa somente proposta tipada e
fechada. Prompt, OCR, resposta bruta e bytes não são persistidos. A proposta
fica `pending` por 15 minutos e pode tornar-se `consumed`, `rejected` ou
`invalidated`, sempre com RLS forçado e leitura limitada ao mesmo ator/empresa.

O rascunho só nasce após confirmação Web/BFF autenticada e antiforgery. Na única
transação, o serviço reautoriza sessão, membership, capabilities e arquivo,
promove internamente a classificação para `paguai_request_invoice`, cria no
máximo um rascunho por proposta, consome a proposta e grava
idempotência/auditoria. A primeira confirmação publica o mesmo
`PaguaiDraftCreated` no outbox da transação; replay, conflito e falha não
publicam outro evento. A revisão é imutável. WhatsApp e Telegram não confirmam,
não alteram classificação e não criam rascunho.

## Limite da V1

Paguai é a fonte de verdade para uma solicitação financeira empresarial: rascunho, conferência, alçadas, fila financeira e confirmação manual do pagamento. Kanban é somente projeção operacional; arrastar, concluir ou apagar card não movimenta a solicitação.

V1 não tem integração bancária, agendamento, tentativa/falha de pagamento, baixa parcial, parcelamento, recorrência ou ajuste posterior ao pagamento. Esses recortes permanecem nas issues [#84](https://github.com/jottakka/vinx-erp/issues/84), [#85](https://github.com/jottakka/vinx-erp/issues/85), [#87](https://github.com/jottakka/vinx-erp/issues/87) e [#82](https://github.com/jottakka/vinx-erp/issues/82).

## Linguagem ubíqua

| Termo | Significado V1 |
| --- | --- |
| Solicitação | Obrigação financeira de uma empresa, com valor total único e um único `pagar até`. |
| Rascunho | Solicitação editável, privada ao solicitante e fora de filas/notificações financeiras. |
| Enviar | Comando que registra ator, data e snapshot e leva `rascunho` a `em_conferencia`; não é estado. |
| Fatura | Documento/evidência da solicitação; inicialmente só metadados mínimos aparecem ao Financeiro. |
| Comprovante | Documento da confirmação de pagamento, distinto da fatura, mas armazenado na mesma coleção segura. |
| Pagar até | Data-limite para pagar a obrigação. Não é agendamento nem data efetiva de pagamento. |
| Confirmação de visualização | Ação humana explícita no aplicativo que registra notificação e evento imutáveis; não altera estado financeiro. |
| Autoconferência | Consulta/checagem do próprio solicitante, auditável e sem efeito de conferência oficial. |
| Conferência oficial | Decisão de conferente elegível: encaminhar, devolver ou rejeitar. |
| Snapshot submetido | Versão imutável dos dados e regras aplicadas no envio/reenvio. |
| Pagamento confirmado | Fato financeiro manual, único e integral; preserva data efetiva, operador, versão e comprovante quando exigido. |

## Pessoas e responsabilidades

| Persona | Objetivo | Limite |
| --- | --- | --- |
| Solicitante | Criar, editar, enviar, acompanhar e corrigir a própria solicitação. | Não confere oficialmente nem aprova a própria solicitação. |
| Visualizador | Receber alerta e confirmar que abriu a fatura. | Não altera fluxo nem recebe atribuição automática. |
| Conferente | Validar a solicitação antes da alçada. | Não executa pagamento sem público Financeiro executor. |
| Aprovador | Decidir uma etapa de alçada elegível. | Não decide fora de faixa, empresa, vigência ou segregação. |
| Financeiro executor | Acompanhar fila e confirmar pagamento manual. | Não agenda nem executa transferência na V1. |
| Administrador da empresa | Configurar política padrão, públicos e regras específicas. | Não ganha autorização financeira implícita. |

## Máquina de estados

| Estado | Entra por | Sai por | Observação |
| --- | --- | --- | --- |
| `rascunho` | criar | enviar; cancelar | Gera indicador visual ao solicitante; não alerta Financeiro. |
| `em_conferencia` | enviar; reenviar devolvido | encaminhar; devolver; rejeitar; cancelar permitido | Alerta in-app aos destinatários Financeiro elegíveis. |
| `devolvido` | devolver | reenviar; cancelar | Exige categoria controlada e descrição; reenvio cria nova versão. |
| `aguardando_aprovacao` | encaminhar quando há alçada | aprovar; devolver; rejeitar; cancelar permitido | Toda decisão é append-only. |
| `pronto_para_financeiro` | última aprovação, ou encaminhamento sem alçada | confirmar pagamento; cancelar permitido | Entra na fila manual de Financeiro. |
| `pago` | confirmar pagamento manual integral | — | Terminal. Ajuste posterior é V2 (#82), nunca reversão. |
| `rejeitado` | rejeitar | — | Terminal, com motivo. Nova necessidade cria outra solicitação. |
| `cancelado` | cancelamento autorizado antes de `pago` | — | Terminal, com motivo e ator auditados. |

Transições por Kanban, abertura de rota, leitura de e-mail/WhatsApp e confirmação de visualização são inválidas para mover o estado. Todo comando de negócio aplica empresa explícita, autorização, versão esperada, idempotência, auditoria e publicação de evento somente após commit.

## Documentos e visualização

1. Ao entrar em `em_conferencia`, o visualizador Financeiro vê metadados mínimos da fatura e a ação explícita **Visualizar fatura**.
2. A confirmação é individual: registra evento/log de notificação e libera URL temporária apenas ao usuário que a confirmou, respeitando autorização atual.
3. A visualização comprova acesso confirmado, não leitura integral do conteúdo.
4. O solicitante escolhe, no rascunho, se o comprovante de pagamento daquela solicitação será opcional ou obrigatório; a escolha e eventual justificativa ficam no snapshot. A configuração empresarial oferece o padrão, mas não altera silenciosamente a escolha já submetida.
5. Fatura exigida e não processada bloqueia envio. Comprovante exigido e ausente bloqueia confirmação de pagamento. Após envio, mudança documental exige devolução e nova versão.

## Política de acesso e alertas

A política padrão é configurada por empresa e pode ser refinada por tipo de despesa, centro de custo e faixa de valor. Seus públicos são solicitante, visualizador, conferente, aprovador e Financeiro executor; usam apenas papel, equipe, unidade ou pessoa nomeada do mesmo tenant. Solicitante sempre pode consultar o próprio pedido e fazer autoconferência.

Indicadores obrigatórios no frontend:

- solicitante: rascunhos pendentes de envio e devolvidos que exigem correção;
- Financeiro elegível: pendência nova em `em_conferencia`, visualização ainda não confirmada e itens prontos para pagamento;
- todos os estados: texto, ícone e cor não exclusiva; a próxima ação permitida deve ser visível.

In-app é o alerta operacional-base. E-mail e WhatsApp usam preferências/opt-in, entrega idempotente e conteúdo mínimo. WhatsApp faz parte da V1 apenas como alerta e deep-link seguro: não mostra dados bancários, fatura ou comprovante e não aprova, confere ou paga por mensagem.

## Matriz ator × ação

| Ação | Solicitante | Visualizador | Conferente | Aprovador | Financeiro executor | Administrador |
| --- | --- | --- | --- | --- | --- | --- |
| Criar/editar rascunho próprio | Sim | Não | Não | Não | Não | Conforme público de solicitante |
| Enviar/reenvia próprio | Sim | Não | Não | Não | Não | Conforme público de solicitante |
| Consultar solicitação | Própria/autorizada | Autorizada | Autorizada | Pendência/autorizada | Autorizada | Conforme política |
| Confirmar visualização/fatura | Própria, sem efeito oficial | Sim, individual | Se também visualizador | Se também visualizador | Se também visualizador | Conforme política |
| Encaminhar/devolver/rejeitar | Não | Não | Sim | Conforme política de decisão | Não, salvo dupla função autorizada | Não implícito |
| Aprovar | Nunca a própria | Não | Não implícito | Sim, com alçada/MFA aplicável | Não implícito | Não implícito |
| Cancelar | Própria nos estados permitidos | Não | Conforme política | Conforme política | Conforme política antes de `pago` | Configura regra; não executa implicitamente |
| Confirmar pagamento | Não | Não | Não | Não | Sim | Não implícito |
| Configurar política | Não | Não | Não | Não | Não | Sim |

## Exceções e decisões obrigatórias antes do domínio

Estas decisões são gates da implementação de [#46](https://github.com/jottakka/vinx-erp/issues/46), [#47](https://github.com/jottakka/vinx-erp/issues/47), [#23](https://github.com/jottakka/vinx-erp/issues/23) e [#54](https://github.com/jottakka/vinx-erp/issues/54), não autorização para iniciá-las:

1. Moeda V1, representação de imposto, arredondamento e igualdade entre linhas e total.
2. Regra de duplicidade: critérios, alerta versus bloqueio e quem pode justificar exceção.
3. Precedência/empate de política, comportamento sem regra, versão congelada no envio e efeito de mudança durante fluxo.
4. Matriz exata de cancelamento por ator e estado.
5. Snapshot do favorecido/dados protegidos e se há reconfirmação antes de pagar.
6. Data efetiva, fuso e regra para data de pagamento futura.
7. Lembretes, escalonamento de vencidos e quais alertas in-app são obrigatórios.

## BDD da descoberta (#45)

```gherkin
@happy @discovery
Cenário: glossário e máquina canônica orientam o envio
  Dado que o solicitante possui um rascunho válido da empresa Alfa
  Quando ele executa enviar na versão atual
  Então o estado passa diretamente para em_conferencia
  E o sistema registra ator, instante e snapshot submetido
  E nenhum estado enviado é persistido

@happy @documents @audit
Cenário: visualizador confirma acesso individual à fatura
  Dado que Marcos é visualizador elegível da Alfa
  E a solicitação está em em_conferencia
  Quando Marcos confirma Visualizar fatura no aplicativo
  Então é criado um evento e log de notificação para Marcos
  E a solicitação permanece em em_conferencia
  E somente Marcos recebe acesso temporário à fatura enquanto autorizado

@happy @return
Cenário: devolução cria correção versionada
  Dado que um conferente elegível encontrou inconsistência
  Quando devolve selecionando categoria e escrevendo descrição
  Então a solicitação entra em devolvido
  E o solicitante pode corrigir e reenviar uma nova versão para em_conferencia

@error @state
Cenário: comando incompatível não muda fluxo
  Dado que uma solicitação está aguardando_aprovacao
  Quando um visualizador tenta marcá-la como paga ou concluí-la no Kanban
  Então a operação é recusada sem alteração de estado, decisão ou notificação

@error @tenant
Cenário: política não vaza acesso entre empresas
  Dado que Ana é Financeiro somente da Alfa
  Quando ela consulta ou recebe alerta de uma solicitação da Beta
  Então o acesso e a entrega são recusados sem expor conteúdo da Beta

@error @evolution
Cenário: recorte V2 não entra disfarçado na V1
  Dado que Financeiro possui solicitação pronta_para_financeiro
  Quando tenta agendar, pagar parcialmente ou criar recorrência
  Então a V1 recusa a ação e aponta para a issue V2 aplicável
```

## Evidência do projeto original

O repositório legado foi consultado em somente leitura no commit [`2ccb0b40fa38925b8aa1bd32a38ade12d7edab47`](https://github.com/cbqoliveira/vinx-erp/tree/2ccb0b40fa38925b8aa1bd32a38ade12d7edab47). A rotina “Pagamento a fornecedor” é template de demanda Kanban, não agregado financeiro. São aproveitáveis somente padrões de UX operacional (contadores, filtros, prazo e alertas), nunca estado ou autorização financeira.
