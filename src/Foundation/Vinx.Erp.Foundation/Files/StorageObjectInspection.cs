namespace Vinx.Erp.Foundation.Files;

public sealed record StorageObjectInspection(
    StorageObjectMetadata Metadata,
    MalwareScanStatus MalwareScanStatus,
    ReadOnlyMemory<byte> LeadingBytes = default);
