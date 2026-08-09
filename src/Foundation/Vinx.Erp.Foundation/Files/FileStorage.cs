namespace Vinx.Erp.Foundation.Files;

public interface FileStorage
{
    Task EnsurePrivateContainerAsync(CancellationToken cancellationToken);
}
