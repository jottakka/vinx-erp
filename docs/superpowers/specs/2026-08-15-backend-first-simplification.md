# Simplificação backend-first do runtime AI

## Contexto

O PR 205 adicionou duas coisas diferentes: aprovações duráveis na Foundation e
um coordenador em memória para retomar sessões do agente. As aprovações
duráveis são uma primitiva de backend com autorização, RLS, auditoria e
idempotência. Já o coordenador não está registrado em nenhum host, perde o
estado em reinício e implementa localmente timers, single-flight, shutdown,
retenção e entrega de decisões antes de existir uma API consumidora.

A prioridade atual é estabilizar APIs do backend para o frontend. Manter um
runtime de execução de escrita do agente sem host ou caso de uso real aumenta
o acoplamento e o custo de mudança sem entregar funcionalidade ao usuário.

## Decisão

1. Manter as aprovações duráveis da Foundation e suas garantias de banco. Não
   haverá rollback de migração nem remoção de dados nesta fatia.
2. Remover o coordenador em memória, o permit de entrega, o vínculo local de
   `RequestId` e os testes exclusivos desse runtime não registrado.
3. Manter o Microsoft Agent Framework como fronteira package-first, com
   aprovação automática desabilitada. A sonda de escrita continua produzindo
   uma pendência e não pode ser executada pela superfície pública.
4. Manter um feixe público opaco, limitado a criar sessão e executar turno
   textual. O agente bruto e a entrada de respostas de aprovação não serão
   expostos.
5. Reduzir `HarnessApprovalGate` à classificação fail-closed de propostas com
   contexto confiável do servidor. A retomada de sessão só volta quando houver
   endpoint e caso de uso reais.
6. Remover a dependência AI -> Foundation criada apenas pela coordenação. A
   autorização de capability continua pertencendo ao backend; a camada AI
   conserva apenas nome e risco das ferramentas de demonstração.
7. Não adicionar framework novo nesta fatia. Primeiro será usada a plataforma
   ASP.NET Core; dependências externas entram quando substituírem código atual
   ou atenderem um caso de uso já escolhido.

## Garantias preservadas

- `DisableToolAutoApproval` permanece ativo e coberto por teste.
- Escrita sem decisão do servidor permanece pendente e sem efeito colateral.
- Ator e empresa usados pelo gate vêm de `VinxExecutionContext`, nunca do
  modelo.
- `CallId` vazio, ferramenta desconhecida e múltiplas solicitações são negados.
- As aprovações duráveis continuam isoladas por empresa, autorizadas e
  auditadas na Foundation, mas não são anunciadas como fluxo hospedado.

## Fora do escopo

- criar endpoints de aprovação ou executar escritas do agente;
- alterar migrações ou o modelo de aprovação durável;
- integrar MCP, provedor de IA ou frontend;
- adotar workflow engine antes de medir a necessidade no primeiro fluxo real.

## Ordem de entrega

A sequência durável fica em
`docs/handoffs/2026-08-15-backend-api-fe-priorities.md`. Em resumo: baseline
ASP.NET Core, identidade/contexto de empresa e contratos HTTP primeiro; APIs
verticais depois; frontend pode avançar em paralelo quando esses contratos
estiverem estáveis; MCP e provedores reutilizam os mesmos casos de uso por
último.

## Estimativa e limite do PR

A fatia altera até 25 caminhos, adiciona menos de 700 linhas manuais e remove
mais de 3.000 linhas manuais. O volume excede a referência de 800 linhas apenas
por deleção coesa de um runtime e de seus testes. Não mistura banco, nova API,
provedor externo ou CI. O responsável pediu explicitamente um único PR.
