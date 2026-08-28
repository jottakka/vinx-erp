using Vinx.Erp.Foundation.Persistence;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests.Infrastructure;

public sealed class FoundationApiFactoryLifecycleTests
{
    [Fact]
    public async Task PrepareAsync_preserves_primary_failure_when_all_cleanups_also_fail()
    {
        var primaryFailure = new InvalidOperationException("migration-failure");
        var hostCleanupFailure = new InvalidOperationException("host-cleanup-failure");
        var jwtCleanupFailure = new InvalidOperationException("jwt-cleanup-failure");
        var fixtureCleanupFailure = new InvalidOperationException("fixture-cleanup-failure");
        var calls = new List<string>();
        var fixture = new ControlledFixture(calls, primaryFailure, fixtureCleanupFailure);

        var exception = await Assert.ThrowsAsync<AggregateException>(() =>
            FoundationApiFactory.CreateForLifecycleTestAsync(
                fixture,
                () => FailCleanupAsync(calls, "host-dispose", hostCleanupFailure),
                () => FailCleanupAsync(calls, "jwt-dispose", jwtCleanupFailure)));

        Assert.Collection(
            exception.InnerExceptions,
            actual => Assert.Same(primaryFailure, actual),
            actual => Assert.Same(hostCleanupFailure, actual),
            actual => Assert.Same(jwtCleanupFailure, actual),
            actual => Assert.Same(fixtureCleanupFailure, actual));
        Assert.Equal(
            ["start", "migrate", "host-dispose", "jwt-dispose", "fixture-dispose"],
            calls);
    }

    [Fact]
    public async Task DisposeAsync_attempts_all_cleanups_when_each_fails()
    {
        var hostCleanupFailure = new InvalidOperationException("host-cleanup-failure");
        var jwtCleanupFailure = new InvalidOperationException("jwt-cleanup-failure");
        var fixtureCleanupFailure = new InvalidOperationException("fixture-cleanup-failure");
        var calls = new List<string>();
        var fixture = new ControlledFixture(calls, migrateException: null, fixtureCleanupFailure);
        var factory = await FoundationApiFactory.CreateForLifecycleTestAsync(
            fixture,
            () => FailCleanupAsync(calls, "host-dispose", hostCleanupFailure),
            () => FailCleanupAsync(calls, "jwt-dispose", jwtCleanupFailure));

        var exception = await Assert.ThrowsAsync<AggregateException>(() => factory.DisposeAsync().AsTask());

        Assert.Collection(
            exception.InnerExceptions,
            actual => Assert.Same(hostCleanupFailure, actual),
            actual => Assert.Same(jwtCleanupFailure, actual),
            actual => Assert.Same(fixtureCleanupFailure, actual));
        Assert.Equal(
            ["start", "migrate", "host-dispose", "jwt-dispose", "fixture-dispose"],
            calls);
    }

    [Fact]
    public async Task CreateWithAuthenticatedIdentityAsync_preserves_seed_failure_when_all_cleanups_also_fail()
    {
        var primaryFailure = new InvalidOperationException("seed-failure");
        var hostCleanupFailure = new InvalidOperationException("host-cleanup-failure");
        var jwtCleanupFailure = new InvalidOperationException("jwt-cleanup-failure");
        var fixtureCleanupFailure = new InvalidOperationException("fixture-cleanup-failure");
        var calls = new List<string>();
        var fixture = new ControlledFixture(
            calls,
            migrateException: null,
            disposeException: fixtureCleanupFailure,
            createAdministratorDbContextException: primaryFailure);

        var exception = await Assert.ThrowsAsync<AggregateException>(() =>
            FoundationApiFactory.CreateForSeedLifecycleTestAsync(
                fixture,
                () => FailCleanupAsync(calls, "host-dispose", hostCleanupFailure),
                () => FailCleanupAsync(calls, "jwt-dispose", jwtCleanupFailure)));

        Assert.Collection(
            exception.InnerExceptions,
            actual => Assert.Same(primaryFailure, actual),
            actual => Assert.Same(hostCleanupFailure, actual),
            actual => Assert.Same(jwtCleanupFailure, actual),
            actual => Assert.Same(fixtureCleanupFailure, actual));
        Assert.Equal(
            ["start", "migrate", "seed", "host-dispose", "jwt-dispose", "fixture-dispose"],
            calls);
    }

    private static ValueTask FailCleanupAsync(List<string> calls, string cleanupName, Exception exception)
    {
        calls.Add(cleanupName);
        return ValueTask.FromException(exception);
    }

    private sealed class ControlledFixture(
        List<string> calls,
        Exception? migrateException,
        Exception disposeException,
        Exception? createAdministratorDbContextException = null) : IFoundationFixtureLifecycle
    {
        public string RuntimeConnectionString =>
            "Host=127.0.0.1;Port=5432;Database=fixture;Username=postgres;Password=test;Pooling=false";

        public Task StartAsync()
        {
            calls.Add("start");
            return Task.CompletedTask;
        }

        public Task MigrateAsync()
        {
            calls.Add("migrate");
            return migrateException is null ? Task.CompletedTask : Task.FromException(migrateException);
        }

        public FoundationDbContext CreateAdministratorDbContext()
        {
            calls.Add("seed");
            if (createAdministratorDbContextException is not null)
            {
                throw createAdministratorDbContextException;
            }

            throw new NotSupportedException("The lifecycle test does not persist seed data.");
        }

        public ValueTask DisposeAsync()
        {
            calls.Add("fixture-dispose");
            return ValueTask.FromException(disposeException);
        }
    }
}
