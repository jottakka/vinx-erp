namespace Vinx.Erp.Foundation.Files;

/// <summary>
/// Guardas de fronteira do storage, compartilhados pela implementação real e pelas
/// dobras de teste: nenhuma implementação assina URL ou resolve chave fora das regras.
/// </summary>
internal static class FileStorageGuards
{
    internal static void ValidateObjectKey(string providerKey, bool allowEmpty = false)
    {
        if ((!allowEmpty && string.IsNullOrWhiteSpace(providerKey))
            || providerKey.Contains("..", StringComparison.Ordinal)
            || providerKey.StartsWith('/')
            || providerKey.EndsWith('/')
            || providerKey.Contains('\\'))
        {
            throw new ArgumentException("Storage object key is invalid.", nameof(providerKey));
        }
    }

    internal static void ValidateDownloadLifetime(TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero || lifetime > SupabaseFileStorage.MaximumDownloadUrlLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }
    }
}
