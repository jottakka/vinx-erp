using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Observability;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class MembershipMapEndpointTests
{
    [Fact]
    public async Task Authorized_company_set_filters_exact_company_capability_without_losing_unfiltered_snapshot()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seeded = await SeedMembershipMapAsync(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var resolver = scope.ServiceProvider.GetRequiredService<AuthorizedCompanySetResolver>();
        var actor = new Vinx.Erp.Foundation.Identity.ActorContext(
            factory.ActorUserId,
            factory.CorrelationId);

        var snapshot = await resolver.ResolveSnapshotAsync(actor, CancellationToken.None);
        var authorized = await resolver.ResolveAsync(
            actor,
            new CapabilityRequirement("payment.approve", CapabilityScope.Company),
            CancellationToken.None);

        Assert.Null(snapshot.ErrorCode);
        Assert.Equal(["Alfa", "Beta", "Gama"], snapshot.Value!.Companies.Select(company => company.CompanyName));
        Assert.Empty(snapshot.Value.Companies.Single(company => company.CompanyId == seeded.GamaCompanyId).Capabilities);
        Assert.Null(authorized.ErrorCode);
        Assert.Equal([seeded.AlfaCompanyId], authorized.Value!.CompanyIds);
    }

    [Fact]
    public async Task Revoked_membership_is_absent_from_the_next_authorized_company_snapshot()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seeded = await SeedMembershipMapAsync(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var resolver = scope.ServiceProvider.GetRequiredService<AuthorizedCompanySetResolver>();
        var actor = new Vinx.Erp.Foundation.Identity.ActorContext(factory.ActorUserId, factory.CorrelationId);
        var requirement = new CapabilityRequirement("payment.approve", CapabilityScope.Company);

        var before = await resolver.ResolveAsync(actor, requirement, CancellationToken.None);
        await using (var administrator = factory.Fixture.CreateAdministratorDbContext())
        {
            var membership = await administrator.Memberships.SingleAsync(candidate =>
                candidate.Id == seeded.AlfaMembershipId);
            membership.Revoke("membership_revoked", factory.ActorUserId, Guid.NewGuid());
            await administrator.SaveChangesAsync();
        }

        var after = await resolver.ResolveAsync(actor, requirement, CancellationToken.None);

        Assert.Equal([seeded.AlfaCompanyId], before.Value!.CompanyIds);
        Assert.Empty(after.Value!.CompanyIds);
    }

    [Fact]
    public async Task Concurrent_revoke_does_not_change_an_in_flight_set_but_changes_the_next_read()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seeded = await SeedMembershipMapAsync(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var resolver = scope.ServiceProvider.GetRequiredService<AuthorizedCompanySetResolver>();
        var actor = new Vinx.Erp.Foundation.Identity.ActorContext(factory.ActorUserId, factory.CorrelationId);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = resolver.ExecuteReadAsync(
            actor,
            new CapabilityRequirement("payment.approve", CapabilityScope.Company),
            async (database, _) =>
            {
                started.SetResult();
                await release.Task;
                return await database.Companies.AsNoTracking().Select(company => company.Id).ToListAsync();
            },
            CancellationToken.None);

        await started.Task;
        await using (var administrator = factory.Fixture.CreateAdministratorDbContext())
        {
            var membership = await administrator.Memberships.SingleAsync(candidate =>
                candidate.Id == seeded.AlfaMembershipId);
            membership.Revoke("membership_revoked", factory.ActorUserId, Guid.NewGuid());
            await administrator.SaveChangesAsync();
        }

        release.SetResult();
        var inFlightResult = await inFlight;
        var next = await resolver.ResolveAsync(
            actor,
            new CapabilityRequirement("payment.approve", CapabilityScope.Company),
            CancellationToken.None);

        Assert.Equal([seeded.AlfaCompanyId], inFlightResult.Value);
        Assert.Empty(next.Value!.CompanyIds);
    }

    [Fact]
    public async Task Non_company_or_empty_requirement_fails_closed_without_an_authorized_set()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var resolver = scope.ServiceProvider.GetRequiredService<AuthorizedCompanySetResolver>();
        var actor = new Vinx.Erp.Foundation.Identity.ActorContext(factory.ActorUserId, factory.CorrelationId);

        var nonCompany = await resolver.ResolveAsync(
            actor,
            new CapabilityRequirement("payment.approve", CapabilityScope.Group),
            CancellationToken.None);
        var empty = await resolver.ResolveAsync(
            actor,
            new CapabilityRequirement(string.Empty, CapabilityScope.Company),
            CancellationToken.None);

        Assert.Equal("capability_denied", nonCompany.ErrorCode?.ToStableCode());
        Assert.Equal("capability_denied", empty.ErrorCode?.ToStableCode());
    }

    [Fact]
    public async Task Membership_map_returns_only_the_current_actor_memberships_with_company_scoped_grants()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seeded = await SeedMembershipMapAsync(factory);

        var response = await factory.CreateClient().GetAsync("/foundation/memberships");

        response.EnsureSuccessStatusCode();
        var memberships =
            await response.Content.ReadFromJsonAsync<List<MembershipResponse>>()
            ?? throw new InvalidOperationException("The membership response body was empty.");
        Assert.Equal(
            ["Alfa", "Beta", "Gama"],
            memberships.Select(membership => membership.CompanyName)
        );
        Assert.Equal(
            [seeded.AlfaMembershipId, seeded.BetaMembershipId, seeded.GamaMembershipId],
            memberships.Select(membership => membership.MembershipId)
        );
        Assert.Equal(seeded.AlfaCompanyId, memberships[0].CompanyId);
        Assert.Equal(seeded.BetaCompanyId, memberships[1].CompanyId);
        Assert.Equal(seeded.GamaCompanyId, memberships[2].CompanyId);
        Assert.Equal("payment.approve", Assert.Single(memberships[0].Capabilities).Name);
        Assert.Equal(["Company"], Assert.Single(memberships[0].Capabilities).Scopes);
        Assert.Equal("foundation.files.upload", Assert.Single(memberships[1].Capabilities).Name);
        Assert.Equal(["Company"], Assert.Single(memberships[1].Capabilities).Scopes);
        Assert.Empty(memberships[2].Capabilities);

        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("role", responseBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("email", responseBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("issuer", responseBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("subject", responseBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", responseBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain", responseBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Membership_map_rejects_an_anonymous_call()
    {
        await using var factory = await FoundationApiFactory.CreateWithClaimsAsync(null, null);

        var response = await factory.CreateClient().GetAsync("/foundation/memberships");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Membership_map_discovery_failure_returns_a_minimal_stable_service_error()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        using var client = factory.CreateClient();

        using (var admitted = await client.GetAsync("/foundation/memberships"))
        {
            admitted.EnsureSuccessStatusCode();
        }

        await RevokeMembershipDiscoveryAsync(factory);
        using var telemetry = new AuthorizationTelemetryCollector();

        var response = await client.GetAsync("/foundation/memberships");
        var responseBody = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("{\"code\":\"audit_unavailable\"}", responseBody);
        Assert.DoesNotContain("Npgsql", responseBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Postgres", responseBody, StringComparison.OrdinalIgnoreCase);
        telemetry.AssertRecorded("membership_map", "authorization");
    }

    [Fact]
    public async Task Membership_map_discovery_failure_is_logged_with_safe_operational_context()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await RevokeMembershipDiscoveryAsync(factory);
        using var logProvider = new MembershipMapLogProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logProvider));
        await using var runtime = factory.Fixture.CreateRuntimeDbContext();
        var resolver = new AuthorizedCompanySetResolver(
            runtime,
            loggerFactory.CreateLogger<AuthorizedCompanySetResolver>());
        var service = new ActorMembershipMapService(
            resolver,
            new AuthorizationTelemetry()
        );

        var result = await service.GetCurrentActorAsync(
            new Vinx.Erp.Foundation.Identity.ActorContext(
                factory.ActorUserId,
                factory.CorrelationId
            ),
            CancellationToken.None
        );

        Assert.Equal("audit_unavailable", result.ErrorCode?.ToStableCode());
        var log = Assert.Single(logProvider.Entries);
        Assert.Equal(LogLevel.Error, log.Level);
        Assert.NotNull(log.Exception);
        Assert.Equal(
            new Dictionary<string, object?>
            {
                ["Operation"] = "authorized_company_set",
                ["Module"] = "foundation",
                ["Result"] = "unavailable",
                ["Reason"] = "snapshot_or_rls_context_failed",
            },
            log.Properties
        );
    }

    internal static async Task<MembershipMapSeed> SeedMembershipMapAsync(
        FoundationApiFactory factory
    )
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var requestId = Guid.NewGuid();
        var otherUser = new User(Guid.NewGuid(), "Bea", "bea@example.test");
        var group = new Group(Guid.NewGuid(), "Vinx Group", factory.ActorUserId, requestId);
        var alfa = new Company(Guid.NewGuid(), group.Id, "Alfa", factory.ActorUserId, requestId);
        var beta = new Company(Guid.NewGuid(), group.Id, "Beta", factory.ActorUserId, requestId);
        var gama = new Company(Guid.NewGuid(), group.Id, "Gama", factory.ActorUserId, requestId);
        var otherCompany = new Company(
            Guid.NewGuid(),
            group.Id,
            "Outra",
            factory.ActorUserId,
            requestId
        );
        var alfaMembership = new Membership(
            Guid.NewGuid(),
            alfa.Id,
            factory.ActorUserId,
            factory.ActorUserId,
            requestId
        );
        var betaMembership = new Membership(
            Guid.NewGuid(),
            beta.Id,
            factory.ActorUserId,
            factory.ActorUserId,
            requestId
        );
        var gamaMembership = new Membership(
            Guid.NewGuid(),
            gama.Id,
            factory.ActorUserId,
            factory.ActorUserId,
            requestId
        );
        var otherMembership = new Membership(
            Guid.NewGuid(),
            otherCompany.Id,
            otherUser.Id,
            factory.ActorUserId,
            requestId
        );
        var alfaRole = new Role(
            Guid.NewGuid(),
            alfa.Id,
            "Alfa approver",
            factory.ActorUserId,
            requestId
        );
        var betaRole = new Role(
            Guid.NewGuid(),
            beta.Id,
            "Beta uploader",
            factory.ActorUserId,
            requestId
        );
        var paymentApprove = new Capability(Guid.NewGuid(), "payment.approve");
        var filesUpload = await administrator.Capabilities.SingleAsync(capability =>
            capability.Name == KnownCapabilities.FilesUpload
        );

        administrator.AddRange(
            otherUser,
            paymentApprove,
            group,
            alfa,
            beta,
            gama,
            otherCompany,
            alfaMembership,
            betaMembership,
            gamaMembership,
            otherMembership,
            alfaRole,
            betaRole,
            new RoleAssignment(
                Guid.NewGuid(),
                alfa.Id,
                alfaRole.Id,
                alfaMembership.Id,
                factory.ActorUserId,
                requestId
            ),
            new RoleAssignment(
                Guid.NewGuid(),
                beta.Id,
                betaRole.Id,
                betaMembership.Id,
                factory.ActorUserId,
                requestId
            ),
            new RoleCapability(
                Guid.NewGuid(),
                alfa.Id,
                alfaRole.Id,
                paymentApprove.Id,
                CapabilityScope.Company,
                factory.ActorUserId,
                requestId
            ),
            new RoleCapability(
                Guid.NewGuid(),
                beta.Id,
                betaRole.Id,
                filesUpload.Id,
                CapabilityScope.Company,
                factory.ActorUserId,
                requestId
            )
        );
        await administrator.SaveChangesAsync();

        return new MembershipMapSeed(
            alfaMembership.Id,
            betaMembership.Id,
            gamaMembership.Id,
            alfa.Id,
            beta.Id,
            gama.Id
        );
    }

    private static async Task RevokeMembershipDiscoveryAsync(FoundationApiFactory factory)
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "REVOKE EXECUTE ON FUNCTION foundation.current_actor_memberships() FROM vinx_runtime;",
            administrator
        );
        await command.ExecuteNonQueryAsync();
    }

    private sealed class AuthorizationTelemetryCollector : IDisposable
    {
        private readonly ConcurrentQueue<IReadOnlyDictionary<string, string>> _measurements = new();
        private readonly MeterListener _listener = new();

        public AuthorizationTelemetryCollector()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (
                    instrument.Meter.Name == "Vinx.Erp.Foundation.Authorization"
                    && instrument.Name == "foundation.authorization.audit_unavailable"
                )
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>(
                (_, _, tags, _) =>
                {
                    _measurements.Enqueue(
                        tags.ToArray()
                            .ToDictionary(
                                tag => tag.Key,
                                tag => tag.Value?.ToString() ?? string.Empty,
                                StringComparer.Ordinal
                            )
                    );
                }
            );
            _listener.Start();
        }

        public void AssertRecorded(string operation, string phase) =>
            Assert.Contains(
                _measurements,
                measurement =>
                    measurement.Count == 2
                    && measurement.GetValueOrDefault("operation") == operation
                    && measurement.GetValueOrDefault("phase") == phase
            );

        public void Dispose() => _listener.Dispose();
    }

    private sealed record CapturedLog(
        LogLevel Level,
        Exception? Exception,
        IReadOnlyDictionary<string, object?> Properties
    );

    private sealed class MembershipMapLogProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<CapturedLog> _entries = new();

        public IReadOnlyCollection<CapturedLog> Entries => _entries.ToArray();

        public ILogger CreateLogger(string categoryName) => new MembershipMapLogger(_entries);

        public void Dispose() { }

        private sealed class MembershipMapLogger(ConcurrentQueue<CapturedLog> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter
            )
            {
                var properties = ((IEnumerable<KeyValuePair<string, object?>>)state!)
                    .Where(pair => pair.Key != "{OriginalFormat}")
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                entries.Enqueue(new CapturedLog(logLevel, exception, properties));
            }
        }
    }

    internal sealed record MembershipMapSeed(
        Guid AlfaMembershipId,
        Guid BetaMembershipId,
        Guid GamaMembershipId,
        Guid AlfaCompanyId,
        Guid BetaCompanyId,
        Guid GamaCompanyId
    );

    private sealed record MembershipResponse(
        Guid MembershipId,
        Guid CompanyId,
        string CompanyName,
        IReadOnlyList<CapabilityResponse> Capabilities
    );

    private sealed record CapabilityResponse(string Name, IReadOnlyList<string> Scopes);
}
