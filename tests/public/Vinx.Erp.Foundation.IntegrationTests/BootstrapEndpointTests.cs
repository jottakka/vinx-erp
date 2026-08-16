using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Bootstrap;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class BootstrapEndpointTests
{
    [Fact]
    public async Task Bootstrap_returns_the_current_profile_and_only_its_ordered_memberships()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seeded = await MembershipMapEndpointTests.SeedMembershipMapAsync(factory);

        using var response = await factory.CreateClient().GetAsync("/foundation/bootstrap");
        var body = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        var bootstrap = await response.Content.ReadFromJsonAsync<BootstrapResponse>()
            ?? throw new InvalidOperationException("The bootstrap response body was empty.");
        Assert.Equal(factory.ActorUserId, bootstrap.User.Id);
        Assert.Equal("Ana", bootstrap.User.DisplayName);
        Assert.Equal(["Alfa", "Beta", "Gama"], bootstrap.Companies.Select(company => company.CompanyName));
        Assert.Equal(
            [seeded.AlfaMembershipId, seeded.BetaMembershipId, seeded.GamaMembershipId],
            bootstrap.Companies.Select(company => company.MembershipId));
        Assert.Equal(seeded.AlfaCompanyId, bootstrap.Companies[0].CompanyId);
        Assert.Equal("payment.approve", Assert.Single(bootstrap.Companies[0].Capabilities).Name);
        Assert.Equal(["Company"], Assert.Single(bootstrap.Companies[0].Capabilities).Scopes);
        Assert.Empty(bootstrap.Companies[2].Capabilities);
        Assert.Empty(bootstrap.Flags);
        Assert.DoesNotContain("email", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("role", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("issuer", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("subject", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("session", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Outra", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bootstrap_allows_an_authenticated_person_without_memberships()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();

        using var response = await factory.CreateClient().GetAsync("/foundation/bootstrap");

        response.EnsureSuccessStatusCode();
        var bootstrap = await response.Content.ReadFromJsonAsync<BootstrapResponse>()
            ?? throw new InvalidOperationException("The bootstrap response body was empty.");
        Assert.Equal(factory.ActorUserId, bootstrap.User.Id);
        Assert.Equal("Ana", bootstrap.User.DisplayName);
        Assert.Empty(bootstrap.Companies);
        Assert.Empty(bootstrap.Flags);
    }

    [Fact]
    public async Task Bootstrap_rejects_an_anonymous_request()
    {
        await using var factory = await FoundationApiFactory.CreateWithClaimsAsync(null, null);

        using var response = await factory.CreateClient().GetAsync("/foundation/bootstrap");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Bootstrap_rejects_an_unknown_external_identity_before_reading_a_profile()
    {
        await using var factory = await FoundationApiFactory.CreateWithClaimsAsync(
            "https://project.supabase.co/auth/v1",
            "unknown-subject");

        using var response = await factory.CreateClient().GetAsync("/foundation/bootstrap");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Empty(await administrator.UserSessions.ToListAsync());
        Assert.Empty(await administrator.AuditEvents.ToListAsync());
    }

    [Fact]
    public async Task Bootstrap_excludes_a_revoked_membership_on_the_next_request()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seeded = await MembershipMapEndpointTests.SeedMembershipMapAsync(factory);
        using var client = factory.CreateClient();

        using (var before = await client.GetAsync("/foundation/bootstrap"))
        {
            before.EnsureSuccessStatusCode();
        }

        await using (var administrator = factory.Fixture.CreateAdministratorDbContext())
        {
            var membership = await administrator.Memberships.SingleAsync(candidate =>
                candidate.Id == seeded.AlfaMembershipId);
            membership.Revoke("membership_revoked", factory.ActorUserId, Guid.NewGuid());
            await administrator.SaveChangesAsync();
        }

        using var after = await client.GetAsync("/foundation/bootstrap");
        var bootstrap = await after.Content.ReadFromJsonAsync<BootstrapResponse>()
            ?? throw new InvalidOperationException("The bootstrap response body was empty.");

        after.EnsureSuccessStatusCode();
        Assert.Equal(["Beta", "Gama"], bootstrap.Companies.Select(company => company.CompanyName));
        Assert.DoesNotContain(
            bootstrap.Companies,
            company => company.CompanyId == seeded.AlfaCompanyId);
    }

    [Fact]
    public async Task Bootstrap_returns_a_safe_service_error_when_profile_function_execution_is_denied()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await RevokeExecuteAsync(factory, "foundation.current_actor_profile()");

        using var response = await factory.CreateClient().GetAsync("/foundation/bootstrap");

        await AssertOnlyAuditUnavailableAsync(response);
    }

    [Fact]
    public async Task Bootstrap_returns_a_safe_service_error_when_membership_function_execution_is_denied()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await RevokeExecuteAsync(factory, "foundation.current_actor_memberships()");

        using var response = await factory.CreateClient().GetAsync("/foundation/bootstrap");

        await AssertOnlyAuditUnavailableAsync(response);
    }

    [Fact]
    public async Task Bootstrap_projects_configured_flags_and_keeps_the_host_snapshot()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        using var configuredHost = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Foundation:Bootstrap:Flags:release.dashboard:Enabled", "false");
            builder.UseSetting("Foundation:Bootstrap:Flags:release.dashboard:Scope", "release");
            builder.UseSetting("Foundation:Bootstrap:Flags:release.dashboard:Owner", "foundation");
            builder.UseSetting("Foundation:Bootstrap:Flags:release.dashboard:Reason", "test");
            builder.UseSetting("Foundation:Bootstrap:Flags:release.dashboard:RemoveAfter", "2026-12-31");
        });
        using var client = configuredHost.CreateClient();

        var first = await GetBootstrapAsync(client);
        var configuration = configuredHost.Services.GetRequiredService<IConfiguration>();
        configuration["Foundation:Bootstrap:Flags:release.dashboard:Enabled"] = "true";
        Assert.Equal("true", configuration["Foundation:Bootstrap:Flags:release.dashboard:Enabled"]);
        var second = await GetBootstrapAsync(client);

        Assert.False(Assert.Single(first.Flags).Value);
        Assert.Equal("release.dashboard", Assert.Single(first.Flags).Key);
        Assert.Equal(first.Flags.ToArray(), second.Flags.ToArray());
        Assert.False(Assert.Single(second.Flags).Value);
    }

    [Fact]
    public async Task Bootstrap_service_returns_only_audit_unavailable_when_profile_is_absent()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await using var database = factory.Fixture.CreateRuntimeDbContext();
        var service = CreateService(database);

        var result = await service.GetCurrentActorAsync(
            new ActorContext(Guid.NewGuid(), factory.CorrelationId),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(AuthorizationErrorCode.AuditUnavailable, result.ErrorCode);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Bootstrap_service_logs_one_safe_warning_when_profile_is_absent()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await using var database = factory.Fixture.CreateRuntimeDbContext();
        var logs = new BootstrapLogCapture();
        var service = CreateService(database, logs);
        var actor = new ActorContext(Guid.NewGuid(), factory.CorrelationId);

        var result = await service.GetCurrentActorAsync(
            actor,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        var log = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Warning, log.Level);
        Assert.Null(log.Exception);
        Assert.Equal("Foundation bootstrap {Operation} {Module} {Result} {Reason}", log.Template);
        Assert.Equal("bootstrap_profile", log.Properties["Operation"]);
        Assert.Equal("foundation", log.Properties["Module"]);
        Assert.Equal("unavailable", log.Properties["Result"]);
        Assert.Equal("profile_not_found", log.Properties["Reason"]);
        Assert.Equal(["Module", "Operation", "Reason", "Result"], log.Properties.Keys.OrderBy(key => key));
        Assert.DoesNotContain(actor.UserId.ToString(), log.Properties.Values.Select(value => value?.ToString()));
        Assert.DoesNotContain(factory.CorrelationId.ToString(), log.Properties.Values.Select(value => value?.ToString()));
    }

    [Fact]
    public async Task Bootstrap_service_does_not_relog_a_resolver_failure()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await RevokeExecuteAsync(factory, "foundation.current_actor_memberships()");
        await using var database = factory.Fixture.CreateRuntimeDbContext();
        var logs = new BootstrapLogCapture();
        var service = CreateService(database, logs);

        var result = await service.GetCurrentActorAsync(
            new ActorContext(factory.ActorUserId, factory.CorrelationId),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(AuthorizationErrorCode.AuditUnavailable, result.ErrorCode);
        Assert.Null(result.Value);
        Assert.Empty(logs.Entries);
    }

    [Fact]
    public async Task Bootstrap_service_propagates_cancellation()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await using var database = factory.Fixture.CreateRuntimeDbContext();
        var service = CreateService(database);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetCurrentActorAsync(
            new ActorContext(factory.ActorUserId, factory.CorrelationId),
            cancellation.Token));
    }

    private static FoundationBootstrapService CreateService(
        Vinx.Erp.Foundation.Persistence.FoundationDbContext database,
        ILogger<FoundationBootstrapService>? logger = null)
    {
        var resolver = new AuthorizedCompanySetResolver(
            database,
            NullLogger<AuthorizedCompanySetResolver>.Instance);
        return new FoundationBootstrapService(
            database,
            resolver,
            Options.Create(new FoundationBootstrapFeatureFlagsOptions()),
            logger ?? NullLogger<FoundationBootstrapService>.Instance);
    }

    private static async Task<BootstrapResponse> GetBootstrapAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/foundation/bootstrap");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<BootstrapResponse>()
            ?? throw new InvalidOperationException("The bootstrap response body was empty.");
    }

    private static async Task RevokeExecuteAsync(FoundationApiFactory factory, string functionName)
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"REVOKE EXECUTE ON FUNCTION {functionName} FROM vinx_runtime;",
            administrator);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertOnlyAuditUnavailableAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("{\"code\":\"audit_unavailable\"}", await response.Content.ReadAsStringAsync());
    }

    private sealed record BootstrapResponse(
        BootstrapUser User,
        IReadOnlyList<BootstrapCompany> Companies,
        IReadOnlyDictionary<string, bool> Flags);

    private sealed record BootstrapUser(Guid Id, string DisplayName);

    private sealed record BootstrapCompany(
        Guid MembershipId,
        Guid CompanyId,
        string CompanyName,
        IReadOnlyList<BootstrapCapability> Capabilities);

    private sealed record BootstrapCapability(string Name, IReadOnlyList<string> Scopes);

    private sealed record CapturedBootstrapLog(
        LogLevel Level,
        Exception? Exception,
        string Template,
        IReadOnlyDictionary<string, object?> Properties);

    private sealed class BootstrapLogCapture : ILogger<FoundationBootstrapService>
    {
        public List<CapturedBootstrapLog> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = ((IEnumerable<KeyValuePair<string, object?>>)state!)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var template = values["{OriginalFormat}"]?.ToString()
                ?? throw new InvalidOperationException("The structured log template was missing.");
            values.Remove("{OriginalFormat}");
            Entries.Add(new CapturedBootstrapLog(logLevel, exception, template, values));
        }
    }
}
