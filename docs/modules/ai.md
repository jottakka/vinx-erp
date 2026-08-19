# Módulo AI

## Estado

A fatia #127+#132 entrega a base mínima hospedada no módulo `Vinx.Erp.AI`:
catálogo server-side de perfis aprovados, resolução de contexto confiável
fail-closed e um runner que aplica a política antes de qualquer chamada de
modelo, com desfecho normalizado. Nesta fatia não existe provider real, API,
UI nem execução de ferramentas.

O único perfil aprovado é `read_only_summarize_v1` (journey `summarize`,
escopo de empresa, capability `ai.read`, `ModelSelection` nulo, timeout de 30
s, `TurnLimit` 1, orçamento de 8.000 tokens e `AllowedTools` vazio). Como
shipa sem `ModelSelection`, a produção devolve
`unavailable/provider_not_configured` até a #135 atribuir o provider
aprovado.

## Ownership

O módulo AI é responsável somente pela orquestração de inferência: perfis
aprovados, resolução de contexto confiável, seleção de provider e uma única
chamada de modelo por execução. Identidade, autorização, capabilities, RLS e
auditoria pertencem à Foundation; o módulo consome o snapshot de autorização
pronto e nunca o produz. Regras de negócio continuam nos módulos de domínio.

## Company scope

O contexto é sempre fail-closed na ordem fixa: `company_required` →
`company_not_authorized` → `capability_denied` → resolvido. A empresa vem
exclusivamente do snapshot de autorização do ator criado pelo servidor; a
mensagem do usuário, o canal ou qualquer argumento nunca escolhem a empresa.
O contexto mínimo (`AgentExecutionContext`) carrega somente identificadores e
nomes normativos da única empresa autorizada — nunca prompt, JWT, e-mail ou
segredo — e nenhuma empresa fora do snapshot é consultada ou exposta.

## Execução e desfechos

O `AgentRunner` segue a ordem fixa: validação de entrada, resolução de
contexto, seleção de provider e, no máximo, uma chamada de modelo. Nenhuma
falha intermediária chama o modelo. O desfecho normalizado (`AgentOutcome`)
cobre `Completed`, `NeedsClarification`, `RequiresConfirmation`, `Denied`,
`Invalid`, `Unavailable` e `TimedOut`, com reason codes estáveis:
`completed`, `message_empty`, `profile_unknown`, `company_required`,
`company_not_authorized`, `capability_denied`, `context_unavailable`,
`provider_not_configured`, `model_unavailable`, `model_timeout` e
`tool_proposal_requires_confirmation`. Tool call volta como proposta que
exige confirmação; o runner não executa ferramentas, não mantém loop de chat
nem estado entre execuções, e nunca loga prompt, resposta ou PII.

## Histórico e próximos passos

A simplificação anterior removeu o runtime AI não hospedado; esta fatia
reintroduz código AI somente com a base verificada, sem provider real. Quando
a integração voltar, a primeira opção continua sendo Microsoft Agent
Framework e o SDK oficial do provider. Próximas fatias:

1. #135 integra o provider real por SDK oficial e `IChatClient`, atribuindo o
   `ModelSelection` aprovado ao perfil;
2. #138 semeia a capability `ai.read`, conecta contexto, provider, guardrails
   e aprovação numa API supervisionada.

Não serão recriados dispatcher de ferramentas, timers, fila, checkpoint ou
workflow próprios sem caso de uso hospedado e evidência de que a plataforma
não atende.

## Contract version

`3.0`

## Deletion strategy

O módulo não mantém dados para excluir. Aprovações duráveis continuam sob a
retenção, soft-delete e futura purga física definidas pela Foundation.

## Migration notes

Nenhuma migração nesta fatia. A capability `ai.read` existe apenas como
constante `AgentCapabilities.Read` no módulo, com UUIDv5 canônico documentado
no código; a seed no catálogo da Foundation vem com a #138. Sem seed, a
produção falha fechado (default deny). As tabelas, policies, grants e
capabilities de aprovação permanecem migrações da Foundation e não serão
duplicadas.
