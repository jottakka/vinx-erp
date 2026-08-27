# Paguai — linguagem e contrato de descoberta V1

> A descoberta da issue [#45](https://github.com/jottakka/vinx-erp/issues/45) continua canônica para o domínio financeiro futuro. A issue #227 entrega apenas o recorte abaixo e substitui, para esse recorte, a antiga afirmação de que este documento não cria contrato técnico.

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

Este corte não representa documento binário, cobrança ou pagamento. Também não oferece
envio, conferência, aprovação, devolução, cancelamento,
integração de provedor, PIX, cartão, WhatsApp, MCP ou automação de fluxo financeiro.
O estado permanece sempre `rascunho`; esses comportamentos permanecem bloqueados
até as decisões de #45.

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
`USAGE` no schema e `SELECT`/`INSERT` na tabela; `PUBLIC` não recebe acesso.

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

## Comprovante vinculado (P1, faixa A)

O `POST /paguai/requests` aceita `proofFileId` opcional e o PATCH
`/paguai/requests/{id}/proof` troca o comprovante com verificação otimista
(`expectedVersion` → `version + 1`). Antes de vincular, o serviço valida pela
fronteira pública de arquivos Foundation (conjunto `FileRecord` do único
`FoundationDbContext`, sem nova dependência): arquivo existente, mesma empresa,
mesma membership autora do upload, estado `confirmed` (quarentena e scan
antimalware concluídos) e ainda não vinculado a outro rascunho. Rejeições usam
códigos tipados (`proof_file_not_found` 404 fail-closed para arquivo ausente,
de outra empresa ou de outro autor — sem distinguir existência;
`proof_file_not_ready`; `proof_file_bound`) e ganham auditoria de negação; a
troca bem-sucedida audita `paguai.draft.replace_proof` no MESMO commit do
rebind. Em storage, o índice parcial único
`uq_paguai_requests_proof_file (proof_file_id) WHERE proof_file_id IS NOT NULL`
e a FK composta para `foundation.files (company_id, id)` tornam duplo vínculo
e referência cruzada de empresa impossíveis, inclusive em corrida.

Leitura: lista/detalhe retornam apenas metadados (`proofFileId`,
`proofFileName`, mimeType, tamanho). `GET /paguai/requests/{id}/proof-link`
emite sob autorização fresca (`paguai.draft.read` + RLS por GUC) uma URL
assinada de 5 minutos via `FileStorage`; URL durável nunca entra em payload.
Sem comprovante no comando, o hash canônico permanece byte-idêntico ao corte
anterior, preservando replay de recibos antigos.

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

A capability nova segue o catálogo canônico UUIDv5 e o padrão das irmãs:
`paguai.draft.approve`
(`e3d8be82-1c79-59e7-9dd4-2f8b9c69c55a`) é concedida por padrão ao papel
`admin` criado por `foundation.ensure_company_default_roles` — mesma escolha já
estabelecida para ações privilegiadas de módulo como
`foundation.files.confirm`; `member` não recebe. O backfill cobre empresas cujo
admin já existia, idempotente por grant ativo.

Pré-requisito de implantação: enquanto o template in-app
`paguai.draft_created.approver` não existir/aprovado na empresa, a intenção NÃO
é registrada (o enfileiramento rejeita) e o evento segue consumido sem erro —
o rascunho criado permanece válido.

Qualquer PR futuro de comportamento Paguai registra
`docs/verification/issue-<numero>[-be|-fe]-test-cases.md`: cenário/BDD, teste ou caminho, intenção e situação. Estimativas contam todos os arquivos não teste, inclusive migrations, snapshots e lockfiles. O limiar de linhas inclui todo conteúdo manual, inclusive testes, e separa apenas linhas geradas de migrations, snapshots e lockfiles.

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
