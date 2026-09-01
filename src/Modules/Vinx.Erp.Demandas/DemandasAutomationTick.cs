namespace Vinx.Erp.Demandas;

/// <summary>
/// Mensagem de tick da automação de demandas (fila durável local). A geração
/// cerca o fence da identidade durável: apenas a cadeia portadora da geração
/// vigente do claim renova o lease e perpetua o próximo tick. O contrato vive
/// no módulo (não no host): produtor e consumidor é o WorkerHost, mas o tipo
/// pertence ao domínio de Demandas.
/// </summary>
public sealed record DemandasAutomationTick(DateTimeOffset ScheduledAt, long Generation);
