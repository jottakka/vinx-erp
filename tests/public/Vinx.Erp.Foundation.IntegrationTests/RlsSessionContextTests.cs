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
}
