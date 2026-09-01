namespace Vinx.Erp.Demandas;

/// <summary>
/// Nomes canônicos das filas Wolverine de propriedade do módulo Demandas.
/// Fonte única compartilhada pelos hosts: o ApiHost não publica aqui, e o
/// WorkerHost publica e consome o tick na mesma fila durável dedicada.
/// </summary>
public static class DemandasQueues
{
    /// <summary>Fila durável do tick auto-perpetuante da automação.</summary>
    public const string Automation = "demandas-automation";
}
