namespace Vinx.Erp.Paguai;

/// <summary>
/// Nomes canônicos das filas Wolverine de propriedade do módulo Paguai.
/// Fonte única compartilhada pelos hosts: o ApiHost publica o rascunho criado
/// e o WorkerHost consome para notificar o requester.
/// </summary>
public static class PaguaiQueues
{
    /// <summary>Fila durável do evento <see cref="PaguaiDraftCreated"/>.</summary>
    public const string Events = "paguai-events";
}
