using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class AuditTransactionTests
{
    [Fact]
    public async Task Failed_success_audit_rolls_back_the_role_assignment_and_returns_a_stable_audit_error()
    {
        await using var factory = await FoundationApiFactory.CreateAsync();
        var scenario = await RoleAssignmentServiceIntegrationTests.SeedScenarioAsync(factory);
        await RevokeAuditAppendAsync(factory);
        using var telemetry = new AuthorizationTelemetryCollector();

        var result = await RoleAssignmentServiceIntegrationTests.InvokeAssignmentAsync(
            factory,
            scenario.CompanyId,
            scenario.AssignableRoleId,
            scenario.TargetMembershipId,
            Guid.NewGuid());

        Assert.Equal("audit_unavailable", RoleAssignmentServiceIntegrationTests.ErrorCode(result));
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Empty(administrator.RoleAssignments.Where(assignment =>
            assignment.RoleId == scenario.AssignableRoleId && assignment.MembershipId == scenario.TargetMembershipId));
        telemetry.AssertRecordedOnlyApprovedTags("role_assignment", "success_transaction");
    }

    [Fact]
    public async Task Failed_denial_audit_returns_a_stable_audit_error_without_exposing_database_details()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await RevokeAuditAppendAsync(factory);
        using var telemetry = new AuthorizationTelemetryCollector();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/foundation/capabilities");
        var responseBody = await response.Content.ReadAsStringAsync();

        Assert.Equal(503, (int)response.StatusCode);
        Assert.Contains("audit_unavailable", responseBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Postgres", responseBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Npgsql", responseBody, StringComparison.OrdinalIgnoreCase);
        telemetry.AssertRecordedOnlyApprovedTags("capability_map", "denial_audit");
    }

    private static async Task RevokeAuditAppendAsync(FoundationApiFactory factory)
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "REVOKE EXECUTE ON FUNCTION audit.append_event(uuid,uuid,text,text,text,uuid,text,uuid,text,jsonb) FROM vinx_runtime;",
            administrator);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class AuthorizationTelemetryCollector : IDisposable
    {
        private const string MeterName = "Vinx.Erp.Foundation.Authorization";
        private const string CounterName = "foundation.authorization.audit_unavailable";
        private readonly ConcurrentQueue<IReadOnlyDictionary<string, string>> _measurements = new();
        private readonly MeterListener _listener = new();

        public AuthorizationTelemetryCollector()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == MeterName && instrument.Name == CounterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                _measurements.Enqueue(tags.ToArray().ToDictionary(
                    tag => tag.Key,
                    tag => tag.Value?.ToString() ?? string.Empty,
                    StringComparer.Ordinal));
            });
            _listener.Start();
        }

        public void AssertRecordedOnlyApprovedTags(string operation, string phase)
        {
            Assert.Contains(_measurements, measurement =>
                measurement.Count == 2
                && measurement.GetValueOrDefault("operation") == operation
                && measurement.GetValueOrDefault("phase") == phase);
            Assert.All(_measurements, measurement =>
            {
                Assert.Equal(2, measurement.Count);
                Assert.All(measurement.Keys, key => Assert.Contains(key, new[] { "operation", "phase" }));
                Assert.Contains(measurement["operation"], new[] { "capability_map", "role_assignment" });
                Assert.Contains(measurement["phase"], new[]
                {
                    "authorization",
                    "denial_audit",
                    "success_transaction",
                    "idempotency_replay",
                    "concurrent_replay"
                });
            });
        }

        public void Dispose() => _listener.Dispose();
    }
}
