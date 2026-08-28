using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class RlsSessionContextTests
{
    [Fact]
    public async Task Capability_evaluation_uses_transaction_local_context_and_does_not_leave_company_access_on_a_runtime_connection()
    {
        await using var factory = await FoundationApiFactory.CreateAsync();
        var scenario = await RoleAssignmentServiceIntegrationTests.SeedScenarioAsync(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<CapabilityMapService>();
        var result = await service.GetCapabilitiesAsync(
            new ActorContext(factory.ActorUserId, factory.CorrelationId),
            scenario.CompanyId,
            CancellationToken.None);
        Assert.Null(result.ErrorCode);
        await using var runtime = factory.Fixture.CreateRuntimeDbContext();
        Assert.Empty(await runtime.Companies.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Authorized_read_context_is_transaction_local_and_does_not_cross_pooled_requests()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seeded = await MembershipMapEndpointTests.SeedMembershipMapAsync(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var resolver = scope.ServiceProvider.GetRequiredService<AuthorizedCompanySetResolver>();
        var result = await resolver.ExecuteReadAsync(
            new ActorContext(factory.ActorUserId, factory.CorrelationId),
            new CapabilityRequirement("payment.approve", Domain.CapabilityScope.Company),
            (database, _, cancellationToken) => database.Companies
                .AsNoTracking()
                .Select(company => company.Id)
                .ToListAsync(cancellationToken),
            CancellationToken.None);

        Assert.Null(result.ErrorCode);
        Assert.Equal(
            new[] { seeded.AlfaCompanyId, seeded.BetaCompanyId }.OrderBy(companyId => companyId),
            result.Value!.OrderBy(companyId => companyId));
        await using var runtime = factory.Fixture.CreateRuntimeDbContext();
        Assert.Empty(await runtime.Companies.AsNoTracking().ToListAsync());
    }
}
