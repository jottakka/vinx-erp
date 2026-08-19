using System.Net;
using System.Text;
using System.Text.Json;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Notifications;
using Vinx.Erp.Notifications.Providers.Meta;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

[Collection("notification-worker")]
public sealed class MetaWhatsAppOutboundTests
{
    [Fact]
    public async Task Eligible_recipient_receives_the_approved_template_with_minimal_payload()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(
            factory,
            whatsAppOptedIn: true
        );
        await NotificationIntentSeed.SeedContactAsync(
            factory,
            seed.CompanyId,
            seed.RecipientUserId,
            "+5511999999999"
        );
        await NotificationIntentSeed.SeedTemplateMetaAsync(
            factory,
            seed.CompanyId,
            "alerta_saida",
            "pt_BR"
        );
        var graph = new FakeMetaGraphHandler(
            HttpStatusCode.OK,
            "{\"messages\":[{\"id\":\"wamid.outbound.1\"}]}"
        );
        var enqueued = await EnqueueWhatsAppWithLinkAsync(factory, seed);
        var deliveryId = enqueued.WhatsApp!.DeliveryId!.Value;

        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            metaGraphHandler: graph
        );
        using var client = worker.CreateClient();

        var state = await NotificationIntentSeed.WaitUntilDeliveryAsync(
            factory,
            deliveryId,
            "delivered",
            TimeSpan.FromSeconds(30)
        );
        Assert.Equal("delivered", state.Status);
        Assert.Equal(1, graph.Calls);
        Assert.Equal(1, state.AttemptCount);

        var request = Assert.Single(graph.Requests);
        Assert.NotNull(request.Authorization);
        Assert.StartsWith(
            "Bearer ",
            request.Authorization.ToString(),
            StringComparison.Ordinal
        );
        using var payload = JsonDocument.Parse(request.Body);
        var root = payload.RootElement;
        Assert.Equal("whatsapp", root.GetProperty("messaging_product").GetString());
        Assert.Equal("+5511999999999", root.GetProperty("to").GetString());
        var template = root.GetProperty("template");
        Assert.Equal("alerta_saida", template.GetProperty("name").GetString());
        Assert.Equal("pt_BR", template.GetProperty("language").GetProperty("code").GetString());
        var parameter = Assert.Single(
            Assert.Single(template.GetProperty("components").EnumerateArray())
                .GetProperty("parameters")
                .EnumerateArray()
        );
        Assert.Equal(
            "https://app.example.test/paguai/requests/123",
            parameter.GetProperty("text").GetString()
        );
        var rootProperties = root
            .EnumerateObject()
            .Select(property => property.Name)
            .ToArray();
        Assert.Equal(["messaging_product", "to", "type", "template"], rootProperties);
    }

    [Fact]
    public async Task Recipient_without_contact_fails_permanently_without_external_call()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(
            factory,
            whatsAppOptedIn: true
        );
        await NotificationIntentSeed.SeedTemplateMetaAsync(
            factory,
            seed.CompanyId,
            "alerta_saida",
            "pt_BR"
        );
        var graph = new FakeMetaGraphHandler(HttpStatusCode.OK, "{}");
        var enqueued = await EnqueueWhatsAppWithLinkAsync(factory, seed);
        var deliveryId = enqueued.WhatsApp!.DeliveryId!.Value;

        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            metaGraphHandler: graph
        );
        using var client = worker.CreateClient();

        var state = await NotificationIntentSeed.WaitUntilDeliveryAsync(
            factory,
            deliveryId,
            "provider_failed",
            TimeSpan.FromSeconds(30)
        );
        Assert.Equal("provider_failed", state.Status);
        Assert.Equal(0, graph.Calls);
    }

    [Fact]
    public async Task Graph_server_error_exhausts_retries_without_false_confirmation()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(
            factory,
            whatsAppOptedIn: true
        );
        await NotificationIntentSeed.SeedContactAsync(
            factory,
            seed.CompanyId,
            seed.RecipientUserId,
            "+5511999999999"
        );
        await NotificationIntentSeed.SeedTemplateMetaAsync(
            factory,
            seed.CompanyId,
            "alerta_saida",
            "pt_BR"
        );
        var graph = new FakeMetaGraphHandler(HttpStatusCode.BadGateway, "{}");
        var enqueued = await EnqueueWhatsAppWithLinkAsync(factory, seed);
        var deliveryId = enqueued.WhatsApp!.DeliveryId!.Value;

        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            metaGraphHandler: graph
        );
        using var client = worker.CreateClient();

        var deadline = DateTimeOffset.UtcNow.Add(TimeSpan.FromSeconds(90));
        while (DateTimeOffset.UtcNow < deadline && graph.Calls < 4)
        {
            await Task.Delay(500);
        }

        Assert.Equal(4, graph.Calls);
        var state = await NotificationIntentSeed.ReadDeliveryAsync(factory, deliveryId);
        Assert.Equal("pending_provider", state.Status);
        Assert.False(state.Resolved);
    }

    [Fact]
    public async Task Graph_timeout_maps_to_terminal_ambiguous_with_one_call()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(
            factory,
            whatsAppOptedIn: true
        );
        await NotificationIntentSeed.SeedContactAsync(
            factory,
            seed.CompanyId,
            seed.RecipientUserId,
            "+5511999999999"
        );
        await NotificationIntentSeed.SeedTemplateMetaAsync(
            factory,
            seed.CompanyId,
            "alerta_saida",
            "pt_BR"
        );
        var graph = new FakeMetaGraphHandler(HttpStatusCode.RequestTimeout, "{}");
        var enqueued = await EnqueueWhatsAppWithLinkAsync(factory, seed);
        var deliveryId = enqueued.WhatsApp!.DeliveryId!.Value;

        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            metaGraphHandler: graph
        );
        using var client = worker.CreateClient();

        var state = await NotificationIntentSeed.WaitUntilDeliveryAsync(
            factory,
            deliveryId,
            "provider_ambiguous",
            TimeSpan.FromSeconds(30)
        );
        Assert.Equal("provider_ambiguous", state.Status);
        Assert.Equal(1, graph.Calls);
    }

    [Fact]
    public async Task Template_without_meta_mapping_fails_permanently_without_external_call()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(
            factory,
            whatsAppOptedIn: true
        );
        await NotificationIntentSeed.SeedContactAsync(
            factory,
            seed.CompanyId,
            seed.RecipientUserId,
            "+5511999999999"
        );
        var graph = new FakeMetaGraphHandler(HttpStatusCode.OK, "{}");
        var enqueued = await EnqueueWhatsAppWithLinkAsync(factory, seed);
        var deliveryId = enqueued.WhatsApp!.DeliveryId!.Value;

        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            metaGraphHandler: graph
        );
        using var client = worker.CreateClient();

        var state = await NotificationIntentSeed.WaitUntilDeliveryAsync(
            factory,
            deliveryId,
            "provider_failed",
            TimeSpan.FromSeconds(30)
        );
        Assert.Equal("provider_failed", state.Status);
        Assert.Equal(0, graph.Calls);
    }

    private static async Task<NotificationEnqueueResult> EnqueueWhatsAppWithLinkAsync(
        FoundationApiFactory factory,
        NotificationCompanySeed seed
    )
    {
        var enqueued = await NotificationIntentSeed.EnqueueAsync(
            factory,
            new NotificationIntentCommand(
                seed.CompanyId,
                seed.RecipientUserId,
                NotificationIntentSeed.NewEventKey(),
                seed.InAppTemplateKey,
                WhatsAppTemplateKey: seed.WhatsAppTemplateKey,
                DeepLinkSelector: "/paguai/requests/123"
            )
        );
        Assert.True(enqueued.WhatsApp!.Created);
        return enqueued;
    }

    /// <summary>
    /// Handler falso do Graph: registra corpo e autorização de cada chamada;
    /// nenhum tráfego externo acontece nos testes.
    /// </summary>
    public sealed class FakeMetaGraphHandler(HttpStatusCode status, string responseBody)
        : HttpMessageHandler
    {
        private readonly Lock _gate = new();
        private readonly List<CapturedRequest> _requests = [];

        public int Calls { get; private set; }

        public IReadOnlyList<CapturedRequest> Requests
        {
            get
            {
                lock (_gate)
                {
                    return _requests.ToList();
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Calls++;
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (_gate)
            {
                _requests.Add(new CapturedRequest(body, request.Headers.Authorization));
            }

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }

    public sealed record CapturedRequest(string Body, System.Net.Http.Headers.AuthenticationHeaderValue? Authorization);
}
