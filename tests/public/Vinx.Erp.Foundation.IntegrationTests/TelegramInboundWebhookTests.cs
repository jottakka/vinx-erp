using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Vinx.Erp.ApiHost.Notifications;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Persistence;
using Wolverine.EntityFrameworkCore;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class TelegramInboundWebhookTests
{
    [Fact]
    public async Task Admitted_private_text_is_persisted_with_the_resolved_actor()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await TelegramNotificationSeed.CreateCompanyWithRecipientAsync(
            factory,
            telegramAddress: "810001",
            telegramContactValidated: true
        );
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<FoundationDbContext>();
        var service = new InboundTelegramIngressService(
            database,
            scope.ServiceProvider.GetRequiredService<IDbContextOutbox>(),
            new SqlInboundTelegramActorResolver(database),
            NullLogger<InboundTelegramIngressService>.Instance
        );

        var created = await service.RecordAsync(
            new InboundTelegramText(1001, 810001, 2001, "approved Telegram text"),
            CancellationToken.None
        );

        Assert.True(created);
        var inbound = Assert.Single(await TelegramNotificationSeed.ReadInboundMessagesAsync(factory));
        Assert.Equal("admitted", inbound.ResolutionState);
        Assert.Equal(seed.RecipientUserId, inbound.ResolvedUserId);
        Assert.Equal(seed.CompanyId, inbound.ResolvedCompanyId);
    }
}
