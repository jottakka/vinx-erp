using Vinx.Erp.Foundation.Domain;
using Xunit;

namespace Vinx.Erp.Foundation.UnitTests;

public sealed class FileRecordLifecycleTests
{
    [Fact]
    public void Upload_metadata_starts_in_quarantine_with_canonical_fields()
    {
        var record = NewRecord();

        Assert.Equal("invoice.pdf", record.OriginalFileName);
        Assert.Equal(".pdf", record.Extension);
        Assert.Equal(128L, record.SizeBytes);
        Assert.Equal("application/pdf", record.MimeType);
        Assert.Equal("sha256:abc", record.Checksum);
        Assert.Equal("financial", record.Classification);
        Assert.Equal("user_upload", record.Origin);
        Assert.Equal(FileRecord.QuarantinedState, record.ProcessingState);
        Assert.StartsWith("quarantine/", record.ProviderKey);
    }

    [Fact]
    public void Verified_document_can_be_confirmed_only_after_verification()
    {
        var record = NewRecord();

        record.MarkVerified(Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(FileRecord.VerifiedState, record.ProcessingState);

        record.MarkConfirmed(Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(FileRecord.ConfirmedState, record.ProcessingState);
    }

    [Fact]
    public void Confirmed_agentic_attachment_can_be_promoted_once_for_a_paguai_request()
    {
        var record = NewRecord("agentic_attachment");
        var requestId = Guid.NewGuid();
        record.MarkVerified(Guid.NewGuid(), requestId);
        record.MarkConfirmed(Guid.NewGuid(), requestId);

        record.PromoteAgenticAttachmentToPaguaiInvoice(Guid.NewGuid(), requestId, DateTimeOffset.UtcNow);

        Assert.Equal("paguai_request_invoice", record.Classification);
        Assert.Throws<InvalidOperationException>(() =>
            record.PromoteAgenticAttachmentToPaguaiInvoice(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Failed_verification_keeps_object_inaccessible()
    {
        var record = NewRecord();

        record.MarkVerificationFailed("malware_detected", Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(FileRecord.VerificationFailedState, record.ProcessingState);
        Assert.Equal("malware_detected", record.FailureReason);
    }

    [Fact]
    public void Verified_document_cannot_return_to_verification_failed()
    {
        var record = NewRecord();
        record.MarkVerified(Guid.NewGuid(), Guid.NewGuid());

        Assert.Throws<InvalidOperationException>(() =>
            record.MarkVerificationFailed("content_type_mismatch", Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public void Deleted_record_can_be_restored_once()
    {
        var record = NewRecord();
        var actor = Guid.NewGuid();
        var request = Guid.NewGuid();
        record.MarkDeleted("retention_test", actor, request);
        Assert.NotNull(record.DeletedAt);

        record.Restore(actor, request);

        Assert.Null(record.DeletedAt);
        Assert.Null(record.DeletedReason);
        Assert.Throws<InvalidOperationException>(() => record.Restore(actor, request));
    }

    private static FileRecord NewRecord(string classification = "financial") => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        "invoice.pdf",
        ".pdf",
        128,
        "application/pdf",
        "sha256:abc",
        classification,
        "user_upload",
        DateTimeOffset.UtcNow.AddDays(30),
        Guid.NewGuid(),
        Guid.NewGuid());
}
