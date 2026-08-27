using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vinx.Erp.Foundation.Approvals;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Observability;
using Vinx.Erp.Foundation.Persistence;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

/// <summary>
/// Prova dos DOIS modos de participação transacional da costura de aprovações
/// (GATE-PENDING: executa na janela do controller):
/// 1. standalone — serviço abre/commita a própria transação (legado ai.*);
/// 2. ambiente — com transação já aberta no mesmo contexto, o serviço
///    participa SEM Begin/Commit próprio (orquestração demanda) e o rollback
///    externo desfaz o preparo inteiro.
/// </summary>
public sealed class ApprovalsAmbientTransactionTests
{
    [Fact]
    public async Task Standalone_prepare_commits_its_own_transaction()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var (actor, companyId, scope) = await BuildScopeAsync(factory);

        var proposal = await PrepareAsync(scope, actor, companyId, Guid.NewGuid());

        Assert.NotEqual(Guid.Empty, proposal.ApprovalId);
        await using var verify = factory.Fixture.CreateAdministratorDbContext();
        Assert.Equal(1, await verify.Approvals.CountAsync(row => row.CompanyId == companyId));
    }

    [Fact]
    public async Task Ambient_participation_rolls_back_with_the_outer_owner()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var (actor, companyId, scope) = await BuildScopeAsync(factory);
        var database = scope.GetRequiredService<FoundationDbContext>();

        await using var outer = await database.Database.BeginTransactionAsync();

        // Mesmo contexto com transação ambiente: o preparo participa sem abrir a sua.
        var prepared = await new PrepareApprovalService(
            database,
            scope.GetRequiredService<AuditAppender>(),
            scope.GetRequiredService<AuthorizationTelemetry>(),
            scope.GetRequiredService<CapabilityEvaluator>(),
            scope.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PrepareApprovalService>>()
        ).PrepareAsync(actor, ToPrepareCommand(companyId, Guid.NewGuid()), default);

        Assert.True(prepared.IsSuccess, $"prepare denied: {prepared.ErrorCode}");
        Assert.NotNull(prepared.Value);
        Assert.NotEqual(Guid.Empty, prepared.Value!.ApprovalId);
        // Participou da transação ambiente (não abriu a própria).
        Assert.Equal(outer.TransactionId, database.Database.CurrentTransaction!.TransactionId);

        await outer.RollbackAsync();

        await using var verify = factory.Fixture.CreateAdministratorDbContext();
        Assert.Equal(0, await verify.Approvals.CountAsync(row => row.CompanyId == companyId));
    }

    private static PrepareApprovalCommand ToPrepareCommand(Guid companyId, Guid requestId)
    {
        var payload = JsonSerializer.SerializeToElement(new { demandId = Guid.NewGuid(), cycle = 1 });
        return new PrepareApprovalCommand(companyId, requestId, "demanda.aprovar-etapa", payload, "demand.v1");
    }

    private static async Task<PrepareApprovalResult> PrepareAsync(
        IServiceProvider provider,
        ActorContext actor,
        Guid companyId,
        Guid requestId) =>
        (
            await new PrepareApprovalService(
                provider.GetRequiredService<FoundationDbContext>(),
                provider.GetRequiredService<AuditAppender>(),
                provider.GetRequiredService<AuthorizationTelemetry>(),
                provider.GetRequiredService<CapabilityEvaluator>(),
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PrepareApprovalService>>()
            ).PrepareAsync(actor, ToPrepareCommand(companyId, requestId), default)
        ).Value!;

    private static async Task<(ActorContext Actor, Guid CompanyId, IServiceProvider Scope)> BuildScopeAsync(
        FoundationApiFactory factory)
    {
        var actor = new ActorContext(factory.ActorUserId, factory.CorrelationId);
        return (actor, await SeedCompanyWithGrantAsync(factory), factory.Services);
    }

    private static async Task<Guid> SeedCompanyWithGrantAsync(FoundationApiFactory factory)
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var requestId = Guid.NewGuid();
        var group = new Group(Guid.NewGuid(), "Vinx Approvals", factory.ActorUserId, requestId);
        var company = new Company(Guid.NewGuid(), group.Id, "Vinx Aprovações", factory.ActorUserId, requestId);
        var membership = new Membership(Guid.NewGuid(), company.Id, factory.ActorUserId, factory.ActorUserId, requestId);
        var role = new Role(Guid.NewGuid(), company.Id, "Aprovador", factory.ActorUserId, requestId);
        var assignment = new RoleAssignment(Guid.NewGuid(), company.Id, role.Id, membership.Id, factory.ActorUserId, requestId);
        administrator.Groups.Add(group);
        administrator.Companies.Add(company);
        administrator.Memberships.Add(membership);
        administrator.Roles.Add(role);
        administrator.RoleAssignments.Add(assignment);
        var capability = await administrator.Capabilities.SingleAsync(row => row.Name == KnownCapabilities.AiWritesConfirm);
        administrator.RoleCapabilities.Add(new RoleCapability(Guid.NewGuid(), company.Id, role.Id, capability.Id, CapabilityScope.Company, factory.ActorUserId, requestId));
        await administrator.SaveChangesAsync();
        return company.Id;
    }

    private static async Task<Guid> SeedCompanyAsync(FoundationApiFactory factory)
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var group = new Group(Guid.NewGuid(), "Vinx Approvals", factory.ActorUserId, Guid.NewGuid());
        var company = new Company(Guid.NewGuid(), group.Id, "Vinx Aprovações", factory.ActorUserId, Guid.NewGuid());
        administrator.Groups.Add(group);
        administrator.Companies.Add(company);
        await administrator.SaveChangesAsync();
        return company.Id;
    }
}
