# Paguai — linguagem e contrato de descoberta V1

> Decisão de produto da issue [#45](https://github.com/jottakka/vinx-erp/issues/45). Este documento não cria entidades, migrations, endpoints ou capabilities.

Qualquer PR futuro de comportamento Paguai registra `docs/verification/issue-<numero>[-be|-fe]-test-cases.md`: cenário/BDD, teste ou caminho, intenção e situação. Estimativas contam todos os arquivos não teste, inclusive migrations, snapshots e lockfiles. O limiar de linhas inclui todo conteúdo manual, inclusive testes, e separa apenas linhas geradas de migrations, snapshots e lockfiles.

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
