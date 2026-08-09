using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Domain;
using Xunit;

namespace Vinx.Erp.Foundation.UnitTests;

public sealed class AuditEventDataTests
{
    [Fact]
    public void Global_scope_allows_no_requested_company()
    {
        var eventData = Create(AuditScope.Global, null);

        Assert.Equal(AuditScope.Global, eventData.Scope);
        Assert.Null(eventData.RequestedCompanyId);
    }

    [Fact]
    public void Global_scope_rejects_company_to_prevent_permitting_global_uuid()
    {
        Assert.Throws<ArgumentException>(() => Create(AuditScope.Global, Guid.NewGuid()));
    }

    [Fact]
    public void Company_scope_rejects_no_company_to_prevent_permitting_company_null()
    {
        Assert.Throws<ArgumentException>(() => Create(AuditScope.Company, null));
    }

    [Fact]
    public void Company_scope_allows_requested_company()
    {
        var companyId = Guid.NewGuid();

        var eventData = Create(AuditScope.Company, companyId);

        Assert.Equal(AuditScope.Company, eventData.Scope);
        Assert.Equal(companyId, eventData.RequestedCompanyId);
    }

    [Fact]
    public void Undefined_scope_is_rejected_to_prevent_persisting_undefined_enum()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create((AuditScope)999, null));
    }

    private static AuditEventData Create(AuditScope scope, Guid? requestedCompanyId) => new(
        Guid.NewGuid(),
        scope,
        requestedCompanyId,
        AuditSource.Web,
        "x",
        "x",
        null,
        AuditOutcome.Success,
        Guid.NewGuid(),
        null,
        "{}");
}
