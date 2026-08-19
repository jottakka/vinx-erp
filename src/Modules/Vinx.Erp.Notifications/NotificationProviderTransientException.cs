namespace Vinx.Erp.Notifications;

/// <summary>
/// Falha transitiva de infraestrutura do provider (por exemplo 5xx ou rate
/// limit no adapter Meta) que pode ser repetida de forma finita pelo worker;
/// timeout do provider NÃO usa este tipo — vira resultado ambíguo.
/// </summary>
public sealed class NotificationProviderTransientException(string message)
    : Exception(message);
