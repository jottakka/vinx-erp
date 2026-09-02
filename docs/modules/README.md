# Módulos

Contratos por módulo: [foundation](foundation.md) · [ai](ai.md) ·
[atendimento](atendimento.md) · [demandas](demandas.md) ·
[notifications](notifications.md) · [paguai](paguai.md) ·
[template](_template.md).

## Padrão de composição (raízes de composição por módulo)

- **Foundation é a dona do modelo compartilhado e das migrations** — um único
  `FoundationDbContext` cobre todos os schemas, de propósito. Módulos não têm
  DbContext próprio; o SQL cru de negócio permanece em funções SQL versionadas
  pela Foundation.
- **Cada módulo é dono dos seus serviços e da sua raiz de composição.** As
  extensões públicas `Add{Modulo}(this IServiceCollection)` vivem dentro do
  módulo (ex.: `NotificationServiceCollectionExtensions.AddWhatsAppIngress`,
  `AgentRunServiceCollectionExtensions.AddAgentRunSurface` em `Vinx.Erp.AI`) e
  os hosts apenas as chamam. Hosts são **transporte + chamadas de módulo**:
  sem hand-wiring de registros de módulo no `Program.cs` (as opções cuja
  validação difere por host permanecem no host, de forma explícita).
- **Arestas entre módulos são allowlist, não costume.** Módulo → Foundation é
  a base; as únicas arestas diretas adicionais são `AI → Paguai` (raw-SQL do
  legado no schema de notificações/paguai — cicatriz conhecida, documentada
  até virar função/contrato) e `Notifications → AI` (adapter do canal do
  agente). Nada referencia hosts. A matriz é executada por
  `ModuleDependencyMatrixTests` e descobre módulos novos sozinha.
- **Contratos de mensagem Wolverine pertencem ao módulo do domínio** (ex.:
  `DemandasAutomationTick` em `Vinx.Erp.Demandas`), com os nomes de fila
  canônicos expostos pelo módulo (`InboundQueues`, `PaguaiQueues`,
  `DemandasQueues`, `DeliveryQueues`). Hosts consomem as constantes; nunca
  duplicam literais de fila.
