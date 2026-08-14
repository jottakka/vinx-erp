using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Observability;
using Vinx.Erp.Foundation.Persistence;

namespace Vinx.Erp.Foundation.Authorization;

public sealed record ActorAuthorizationSnapshot(
    IReadOnlyList<MembershipCapabilitySummary> Companies);

public sealed record AuthorizedCompanySet(IReadOnlySet<Guid> CompanyIds);

public sealed class AuthorizedCompanySetResolver(
    FoundationDbContext database,
    ILogger<AuthorizedCompanySetResolver> logger)
{
    private const string Operation = "authorized_company_set";
    private const string Module = "foundation";
    private const string UnavailableResult = "unavailable";
    private const string UnavailableReason = "snapshot_or_rls_context_failed";
    private const string UnavailableEventTemplate =
        "Authorization context {Operation} {Module} {Result} {Reason}";

    public async Task<AuthorizationResult<ActorAuthorizationSnapshot>> ResolveSnapshotAsync(
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await FoundationRlsContext.ApplyActorAsync(database.Database, actor, cancellationToken);
            var snapshot = await ResolveSnapshotInTransactionAsync(cancellationToken);
            await transaction.RollbackAsync(cancellationToken);
            return AuthorizationResult<ActorAuthorizationSnapshot>.Success(snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogUnavailable(exception);
            return AuthorizationResult<ActorAuthorizationSnapshot>.Failure(
                AuthorizationErrorCode.AuditUnavailable);
        }
    }

    public Task<AuthorizationResult<AuthorizedCompanySet>> ResolveAsync(
        ActorContext actor,
        CapabilityRequirement requirement,
        CancellationToken cancellationToken) =>
        ExecuteReadAsync(
            actor,
            requirement,
            (_, companyIds) => Task.FromResult(new AuthorizedCompanySet(companyIds)),
            cancellationToken);

    public async Task<AuthorizationResult<T>> ExecuteReadAsync<T>(
        ActorContext actor,
        CapabilityRequirement requirement,
        Func<FoundationDbContext, IReadOnlySet<Guid>, Task<T>> read,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requirement.Name)
            || requirement.RequiredScope != CapabilityScope.Company)
        {
            return AuthorizationResult<T>.Failure(AuthorizationErrorCode.CapabilityDenied);
        }

        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await FoundationRlsContext.ApplyActorAsync(database.Database, actor, cancellationToken);
            var snapshot = await ResolveSnapshotInTransactionAsync(cancellationToken);
            var companyIds = snapshot.Companies
                .Where(company => company.Capabilities.Any(capability =>
                    capability.Name == requirement.Name
                    && capability.Scopes.Contains(CapabilityScope.Company)))
                .Select(company => company.CompanyId)
                .ToHashSet();
            await FoundationRlsContext.ApplyAuthorizedReadAsync(
                database.Database,
                companyIds,
                cancellationToken);
            var value = await read(database, companyIds);
            await transaction.RollbackAsync(cancellationToken);
            return AuthorizationResult<T>.Success(value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogUnavailable(exception);
            return AuthorizationResult<T>.Failure(AuthorizationErrorCode.AuditUnavailable);
        }
    }

    private async Task<ActorAuthorizationSnapshot> ResolveSnapshotInTransactionAsync(
        CancellationToken cancellationToken)
    {
        var memberships = await database
            .Database.SqlQuery<DiscoveredMembership>(
                $"SELECT membership_id, company_id, company_name FROM foundation.current_actor_memberships()")
            .ToListAsync(cancellationToken);
        var summaries = new List<MembershipCapabilitySummary>(memberships.Count);
        foreach (var membership in memberships)
        {
            await FoundationRlsContext.ApplyCompanyAsync(
                database.Database,
                membership.CompanyId,
                cancellationToken);
            var grants = await CapabilityMapService
                .CreateCapabilityGrantQuery(
                    database,
                    membership.CompanyId,
                    membership.MembershipId)
                .ToListAsync(cancellationToken);
            summaries.Add(new MembershipCapabilitySummary(
                membership.MembershipId,
                membership.CompanyId,
                membership.CompanyName,
                CapabilityMapService.ProjectEffectiveCapabilities(grants)));
        }

        return new ActorAuthorizationSnapshot(summaries);
    }

    private void LogUnavailable(Exception exception) =>
        logger.LogError(
            exception,
            UnavailableEventTemplate,
            Operation,
            Module,
            UnavailableResult,
            UnavailableReason);

    private sealed record DiscoveredMembership(
        Guid MembershipId,
        Guid CompanyId,
        string CompanyName);
}

internal static class FoundationRlsContext
{
    public static async Task ApplyActorAsync(
        DatabaseFacade database,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        await database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.user_id', {actor.UserId.ToString()}, true)",
            cancellationToken);
        await database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.company_id', {string.Empty}, true)",
            cancellationToken);
        await database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.authorized_company_ids', {string.Empty}, true)",
            cancellationToken);
        await database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.correlation_id', {new CorrelationContext(actor.CorrelationId).CorrelationId.ToString()}, true)",
            cancellationToken);
    }

    public static Task ApplyCompanyAsync(
        DatabaseFacade database,
        Guid companyId,
        CancellationToken cancellationToken) =>
        database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.company_id', {companyId.ToString()}, true)",
            cancellationToken);

    public static async Task ApplyAuthorizedReadAsync(
        DatabaseFacade database,
        IReadOnlySet<Guid> companyIds,
        CancellationToken cancellationToken)
    {
        await database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.company_id', {string.Empty}, true)",
            cancellationToken);
        var serialized = string.Join(",", companyIds.OrderBy(companyId => companyId));
        await database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.authorized_company_ids', {serialized}, true)",
            cancellationToken);
    }
}
