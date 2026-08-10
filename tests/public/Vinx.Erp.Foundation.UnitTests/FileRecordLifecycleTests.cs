using System.Reflection;
using Vinx.Erp.Foundation.Domain;
using Xunit;

namespace Vinx.Erp.Foundation.UnitTests;

public sealed class FileRecordLifecycleTests
{
    [Fact]
    public void Upload_metadata_starts_in_quarantine_with_canonical_fields()
    {
        var record = new FileRecord(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "invoice.pdf",
            ".pdf",
            128L,
            "application/pdf",
            "sha256:abc",
            "financial",
            "user_upload",
            DateTimeOffset.UtcNow.AddDays(30),
            Guid.NewGuid(),
            Guid.NewGuid());

        Assert.Equal("invoice.pdf", Property(record, "OriginalFileName"));
        Assert.Equal(".pdf", Property(record, "Extension"));
        Assert.Equal(128L, Property(record, "SizeBytes"));
        Assert.Equal("application/pdf", Property(record, "MimeType"));
        Assert.Equal("sha256:abc", record.Checksum);
        Assert.Equal("financial", record.Classification);
        Assert.Equal("user_upload", Property(record, "Origin"));
        Assert.Equal("quarantined", Property(record, "ProcessingState"));
        Assert.True(record.IsQuarantined);
    }

    [Fact]
    public void Verified_document_remains_in_quarantine_until_a_later_confirmation_slice()
    {
        var record = NewRecord();

        Invoke(record, "MarkVerified", Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal("verified", record.ProcessingState);
        Assert.True(record.IsQuarantined);
    }

    [Fact]
    public void Failed_verification_keeps_object_inaccessible()
    {
        var record = NewRecord();

        Invoke(record, "MarkVerificationFailed", "malware_detected", Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal("verification_failed", record.ProcessingState);
        Assert.Equal("malware_detected", Property(record, "FailureReason"));
        Assert.True(record.IsQuarantined);
    }

    [Fact]
    public void Verified_document_cannot_return_to_verification_failed()
    {
        var record = NewRecord();
        Invoke(record, "MarkVerified", Guid.NewGuid(), Guid.NewGuid());

        Assert.Throws<InvalidOperationException>(() =>
            Invoke(record, "MarkVerificationFailed", "content_type_mismatch", Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public void Retention_blocks_physical_delete_until_expiry()
    {
        var record = NewRecord();
        var actor = Guid.NewGuid();
        var request = Guid.NewGuid();
        Invoke(record, "MarkDeleted", "retention_test", actor, request);

        Assert.False(record.IsPhysicalDeletionAllowed(DateTimeOffset.UtcNow));
        Assert.True(record.IsPhysicalDeletionAllowed(DateTimeOffset.UtcNow.AddDays(31)));

        Invoke(record, "Restore", actor, request);
        Assert.Null(record.DeletedAt);
        Assert.False(record.IsPhysicalDeletionAllowed(DateTimeOffset.UtcNow.AddDays(31)));
    }

    private static FileRecord NewRecord() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        "invoice.pdf",
        ".pdf",
        128,
        "application/pdf",
        "sha256:abc",
        "financial",
        "user_upload",
        DateTimeOffset.UtcNow.AddDays(30),
        Guid.NewGuid(),
        Guid.NewGuid());

    private static void Invoke(FileRecord record, string name, params object[] arguments)
    {
        var method = typeof(FileRecord).GetMethod(name, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
        try
        {
            method.Invoke(record, arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }

    private static object? Property(FileRecord record, string name) =>
        typeof(FileRecord).GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(record);
}
