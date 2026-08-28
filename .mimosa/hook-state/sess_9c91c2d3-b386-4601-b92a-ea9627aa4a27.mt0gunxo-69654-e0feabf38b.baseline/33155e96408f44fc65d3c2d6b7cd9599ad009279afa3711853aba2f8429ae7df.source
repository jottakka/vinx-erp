using System.Net;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests.Storage;

[Trait("Category", "SupabaseStorage")]
public sealed class SupabaseFileStorageTests
{
    [Fact]
    public async Task Ensure_private_container_is_idempotent_and_denies_anonymous_object_reads()
    {
        await using var fixture = SupabaseStorageFixture.FromEnvironment();
        var storage = fixture.CreateStorage();
        var objectName = "private-provisioning-fixture.txt";

        await storage.EnsurePrivateContainerAsync(CancellationToken.None);
        await storage.EnsurePrivateContainerAsync(CancellationToken.None);

        var bucket = await fixture.GetBucketAsync(CancellationToken.None);
        Assert.Equal(SupabaseStorageFixture.ContainerId, bucket.Id);
        Assert.False(bucket.Public);

        await fixture.UploadFixtureObjectAsync(objectName, CancellationToken.None);
        try
        {
            using var response = await fixture.GetAnonymousObjectAsync(objectName, CancellationToken.None);
            Assert.False(response.IsSuccessStatusCode);
            Assert.Contains(response.StatusCode, new[]
            {
                HttpStatusCode.BadRequest,
                HttpStatusCode.Unauthorized,
                HttpStatusCode.Forbidden,
                HttpStatusCode.NotFound
            });
        }
        finally
        {
            await fixture.DeleteFixtureObjectAsync(objectName, CancellationToken.None);
        }
    }
}
