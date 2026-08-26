using Npgsql;
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

    [Fact]
    public async Task Prepared_e2e_factory_disposal_never_disposes_the_shared_owner()
    {
        var calls = new List<string>();
        var owner = new ControlledFixture(calls, migrateException: null, disposeException: null);
        var lease = new FoundationPostgresFixtureLease(owner);
        var factory = FoundationApiFactory.CreateForPreparedLifecycleTest(
            lease,
            () => RecordCleanupAsync(calls, "host-dispose"),
            () => RecordCleanupAsync(calls, "jwt-dispose"));

        await factory.DisposeAsync();

        Assert.Equal(["host-dispose", "jwt-dispose"], calls);
        Assert.Equal(0, owner.DisposeCallCount);
        Assert.False(typeof(IAsyncDisposable).IsAssignableFrom(typeof(FoundationPostgresFixtureLease)));
    }

    [Fact]
    public async Task Prepared_e2e_factory_cleanup_failure_never_disposes_the_shared_owner()
    {
        var calls = new List<string>();
        var owner = new ControlledFixture(calls, migrateException: null, disposeException: null);
        var lease = new FoundationPostgresFixtureLease(owner);
        var hostCleanupFailure = new InvalidOperationException("host-cleanup-failure");
        var factory = FoundationApiFactory.CreateForPreparedLifecycleTest(
            lease,
            () => FailCleanupAsync(calls, "host-dispose", hostCleanupFailure),
            () => RecordCleanupAsync(calls, "jwt-dispose"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => factory.DisposeAsync().AsTask());

        Assert.Same(hostCleanupFailure, exception);
        Assert.Equal(["host-dispose", "jwt-dispose"], calls);
        Assert.Equal(0, owner.DisposeCallCount);
    }

    [Fact]
    public async Task Shared_fixture_start_failure_retries_only_pending_cleanup_and_preserves_the_first_failure()
    {
        var startupFailure = new InvalidOperationException("startup-failure");
        var firstCleanupFailure = new IOException("clara-cleanup-failure");
        var calls = new List<string>();
        var clara = new RecordingAsyncDisposable(calls, "clara", firstCleanupFailure);
        var maria = new RecordingAsyncDisposable(calls, "maria");
        var credential = new RecordingAsyncDisposable(calls, "credential");
        var fixture = new RecordingAsyncDisposable(calls, "fixture");
        var sharedHost = SharedE2eFixtureHost.CreateForCleanupTest(
            clara,
            maria,
            credential,
            fixture);

        var exception = await sharedHost.DisposeAfterStartFailureAsync(startupFailure);

        Assert.Equal(["clara", "maria", "credential", "fixture", "clara"], calls);
        Assert.Equal(2, clara.DisposeCalls);
        Assert.Equal(1, maria.DisposeCalls);
        Assert.Equal(1, credential.DisposeCalls);
        Assert.Equal(1, fixture.DisposeCalls);
        var aggregate = Assert.IsType<AggregateException>(exception);
        Assert.Collection(
            aggregate.InnerExceptions,
            actual => Assert.Same(startupFailure, actual),
            actual => Assert.Same(firstCleanupFailure, actual));
    }

    private static ValueTask FailCleanupAsync(List<string> calls, string cleanupName, Exception exception)
    {
        calls.Add(cleanupName);
        return ValueTask.FromException(exception);
    }

    private static ValueTask RecordCleanupAsync(List<string> calls, string cleanupName)
    {
        calls.Add(cleanupName);
        return ValueTask.CompletedTask;
    }

    private sealed class ControlledFixture(
        List<string> calls,
        Exception? migrateException,
        Exception? disposeException,
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

        public FoundationPostgresEndpoint Endpoint => new(
            "127.0.0.1",
            5432,
            "fixture",
            "postgres",
            Pooling: false);

        public Task<NpgsqlConnection> OpenAdministratorConnectionAsync() =>
            Task.FromException<NpgsqlConnection>(new NotSupportedException("The lifecycle test does not open a connection."));

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
            DisposeCallCount += 1;
            calls.Add("fixture-dispose");
            return disposeException is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(disposeException);
        }

        public int DisposeCallCount { get; private set; }
    }

    private sealed class RecordingAsyncDisposable(
        List<string> calls,
        string name,
        Exception? firstFailure = null) : IAsyncDisposable
    {
        public int DisposeCalls { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCalls += 1;
            calls.Add(name);
            return DisposeCalls == 1 && firstFailure is not null
                ? ValueTask.FromException(firstFailure)
                : ValueTask.CompletedTask;
        }
    }
}
