using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Notifications;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class NotificationInboxStateTests
{
    private const string AntiforgeryHeader = "X-CSRF-TOKEN";

    [Fact]
    public async Task Delivered_whatsapp_intent_never_appears_in_the_in_app_inbox()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var companyId = await NotificationInboxSeed.CreateCompanyForActorAsync(
            factory,
            whatsAppOptedIn: true
        );
        var result = await NotificationIntentSeed.EnqueueAsync(
            factory,
            new NotificationIntentCommand(
                companyId,
                factory.ActorUserId,
                NotificationIntentSeed.NewEventKey(),
                NotificationIntentSeed.InAppTemplateKey,
                WhatsAppTemplateKey: NotificationIntentSeed.WhatsAppTemplateKey
            )
        );
        var whatsAppDeliveryId = result.WhatsApp!.DeliveryId!.Value;
        await NotificationInboxSeed.DriveToStateAsync(factory, whatsAppDeliveryId, "delivered");

        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/notifications/inbox?limit=10");

        response.EnsureSuccessStatusCode();
        var envelope = await response.Content.ReadFromJsonAsync<InboxPayload>();
        Assert.Empty(envelope!.Items);
    }

    [Theory]
    [InlineData("pending_delivery")]
    [InlineData("pending_provider")]
    [InlineData("provider_in_flight")]
    [InlineData("provider_failed")]
    [InlineData("provider_ambiguous")]
    public async Task Non_delivered_intents_are_not_listed_and_not_markable(string state)
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var companyId = await NotificationInboxSeed.CreateCompanyForActorAsync(factory);
        var deliveryId = await NotificationInboxSeed.CreateIntentInStateAsync(
            factory,
            companyId,
            state
        );

        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<NotificationInboxService>();
        var actor = new ActorContext(factory.ActorUserId, factory.CorrelationId);
        var list = await service.ListAsync(actor, null, 10, CancellationToken.None);
        var mark = await service.MarkReadAsync(actor, deliveryId, CancellationToken.None);

        Assert.Equal(NotificationInboxState.Listed, list.State);
        Assert.Empty(list.Page!.Items);
        Assert.Equal(NotificationMarkReadState.NotFound, mark.State);
        var readState = await NotificationInboxSeed.ReadInboxStateAsync(factory, deliveryId);
        Assert.Null(readState.ReadAt);

        using var client = factory.CreateClient();
        using var response = await client.PostAsync(
            $"/notifications/inbox/{deliveryId}/read",
            content: null
        );
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Audit_failure_rolls_back_the_marking_without_partial_state()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var companyId = await NotificationInboxSeed.CreateCompanyForActorAsync(factory);
        var deliveryId = await NotificationInboxSeed.CreateDeliveredInAppIntentAsync(factory, companyId);
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var revoke = new NpgsqlCommand(
            """
            SET ROLE vinx_audit_owner;
            REVOKE EXECUTE ON FUNCTION audit.append_event(
                uuid, text, uuid, text, text, text, uuid, text, uuid, text, jsonb)
                FROM vinx_notifications_owner;
            RESET ROLE;
            """,
            administrator
        );
        await revoke.ExecuteNonQueryAsync();

        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<NotificationInboxService>();
        var result = await service.MarkReadAsync(
            new ActorContext(factory.ActorUserId, factory.CorrelationId),
            deliveryId,
            CancellationToken.None
        );

        Assert.Equal(NotificationMarkReadState.Unavailable, result.State);
        var readState = await NotificationInboxSeed.ReadInboxStateAsync(factory, deliveryId);
        Assert.Null(readState.ReadAt);
        Assert.Equal(0, await NotificationInboxSeed.CountReadAuditsAsync(factory, deliveryId));
    }

    [Fact]
    public async Task Inbox_operations_do_not_touch_worker_lifecycle_columns()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var companyId = await NotificationInboxSeed.CreateCompanyForActorAsync(factory);
        var deliveryId = await NotificationInboxSeed.CreateDeliveredInAppIntentAsync(factory, companyId);
        var before = await NotificationIntentSeed.ReadDeliveryAsync(factory, deliveryId);

        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<NotificationInboxService>();
        var actor = new ActorContext(factory.ActorUserId, factory.CorrelationId);
        await service.ListAsync(actor, null, 10, CancellationToken.None);
        await service.CountUnreadAsync(actor, CancellationToken.None);
        await service.MarkReadAsync(actor, deliveryId, CancellationToken.None);

        var after = await NotificationIntentSeed.ReadDeliveryAsync(factory, deliveryId);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.AttemptCount, after.AttemptCount);
        Assert.Equal(before.FailureReason, after.FailureReason);
        Assert.Equal(before.Resolved, after.Resolved);
    }

    [Fact]
    public async Task Cookie_session_requires_csrf_for_read_and_accepts_a_fresh_token()
    {
        var provider = new ProviderHandler();
        await using var factory =
            await FoundationApiFactory.CreateWithJwtBearerAndBrowserSessionProviderAsync(provider);
        provider.ResponseFactory = _ =>
            ProviderResponse(factory.CreateJwtToken(JwtTokenVariation.Valid));
        var companyId = await NotificationInboxSeed.CreateCompanyForActorAsync(factory);
        var deliveryId = await NotificationInboxSeed.CreateDeliveredInAppIntentAsync(factory, companyId);
        using var cookieClient = factory.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = true }
        );
        using var signIn = await SignInAsync(cookieClient);
        signIn.EnsureSuccessStatusCode();

        using var missingToken = await cookieClient.PostAsync(
            $"/notifications/inbox/{deliveryId}/read",
            content: null
        );
        Assert.Equal(HttpStatusCode.BadRequest, missingToken.StatusCode);

        var token = await GetAntiforgeryTokenAsync(cookieClient);
        using var withToken = new HttpRequestMessage(
            HttpMethod.Post,
            $"/notifications/inbox/{deliveryId}/read"
        );
        withToken.Headers.Add(AntiforgeryHeader, token);
        using var cookieSuccess = await cookieClient.SendAsync(withToken);
        Assert.Equal(HttpStatusCode.OK, cookieSuccess.StatusCode);

        using var bearerClient = factory.CreateClient();
        bearerClient.DefaultRequestHeaders.Authorization = new(
            "Bearer",
            factory.CreateJwtToken(JwtTokenVariation.Valid)
        );
        using var bearerResponse = await bearerClient.PostAsync(
            $"/notifications/inbox/{deliveryId}/read",
            content: null
        );
        Assert.Equal(HttpStatusCode.OK, bearerResponse.StatusCode);
        Assert.Equal(1, await NotificationInboxSeed.CountReadAuditsAsync(factory, deliveryId));
    }

    private static async Task<HttpResponseMessage> SignInAsync(HttpClient client)
    {
        var token = await GetAntiforgeryTokenAsync(client);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/foundation/identity/browser-session"
        )
        {
            Content = new StringContent(
                JsonSerializer.Serialize(
                    new
                    {
                        email = $"inbox-{Guid.NewGuid():N}@example.test",
                        password = Guid.NewGuid().ToString("N"),
                    }
                ),
                Encoding.UTF8,
                "application/json"
            ),
        };
        request.Headers.Add(AntiforgeryHeader, token);
        return await client.SendAsync(request);
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        using var response = await client.GetAsync(
            "/foundation/identity/browser-session/antiforgery"
        );
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("requestToken").GetString()
            ?? throw new InvalidOperationException("The test antiforgery token was empty.");
    }

    private static HttpResponseMessage ProviderResponse(string accessToken) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { access_token = accessToken }),
                Encoding.UTF8,
                "application/json"
            ),
        };

    private sealed class ProviderHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? ResponseFactory { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                ResponseFactory?.Invoke(request)
                    ?? new HttpResponseMessage(HttpStatusCode.OK)
            );
    }

    private sealed record InboxPayload(IReadOnlyList<InboxItem> Items);

    private sealed record InboxItem(Guid Id);
}
