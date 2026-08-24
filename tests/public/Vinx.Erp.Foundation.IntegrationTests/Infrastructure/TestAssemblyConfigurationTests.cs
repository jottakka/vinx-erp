using System.Reflection;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests.Infrastructure;

public sealed class TestAssemblyConfigurationTests
{
    [Fact]
    public void Assembly_limits_parallel_test_collections_without_disabling_parallelism()
    {
        var configuration = Assert.Single(
            typeof(TestAssemblyConfigurationTests).Assembly
                .GetCustomAttributes<CollectionBehaviorAttribute>());

        Assert.Equal(2, configuration.MaxParallelThreads);
        Assert.False(configuration.DisableTestParallelization);
    }
}
