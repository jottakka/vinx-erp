namespace Vinx.Erp.Foundation.Files;

public sealed record SupabaseStorageOptions(Uri BaseUri, string ServiceRoleKey, string ContainerId)
{
    public const string PrivateContainerId = "vinx-foundation-private";

    public void Validate()
    {
        if (!BaseUri.IsAbsoluteUri)
        {
            throw new InvalidOperationException("Supabase Storage URL must be absolute.");
        }

        if (string.IsNullOrWhiteSpace(ServiceRoleKey))
        {
            throw new InvalidOperationException("Supabase Storage service role key must be configured.");
        }

        if (!string.Equals(ContainerId, PrivateContainerId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Supabase Storage container must be {PrivateContainerId}.");
        }
    }
}
