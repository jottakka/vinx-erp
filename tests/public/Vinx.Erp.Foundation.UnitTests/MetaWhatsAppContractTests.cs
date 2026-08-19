using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Vinx.Erp.Notifications;
using Vinx.Erp.Notifications.Providers.Meta;
using Xunit;

namespace Vinx.Erp.Foundation.UnitTests;

public sealed class MetaWhatsAppContractTests
{
    // Valor de teste sem valor real; nenhum segredo verdadeiro vive no repositório.
    private const string UnitTestBearerPlaceholder = "unit-test-placeholder";

    private static MetaWhatsAppOptions Options() =>
        new()
        {
            PhoneNumberId = "111111111111111",
            AccessToken = UnitTestBearerPlaceholder,
            ApiBaseUrl = "https://graph.test",
            WebAppBaseUrl = "https://app.example.test",
        };

    [Fact]
    public void Mapper_carries_only_the_recipient_template_and_optional_deep_link()
    {
        var message = MetaWhatsAppMapper.Map(
            "+5511999999999",
            "alerta_saida",
            "pt_BR",
            null,
            "https://app.example.test"
        );

        Assert.Equal("whatsapp", message.MessagingProduct);
        Assert.Equal("+5511999999999", message.To);
        Assert.Equal("template", message.Type);
        Assert.Equal("alerta_saida", message.Template.Name);
        Assert.Equal("pt_BR", message.Template.Language.Code);
        Assert.Null(message.Template.Components);
        var serialized = MetaWhatsAppMapper.Serialize(message);
        Assert.DoesNotContain("app.example.test", serialized);
        Assert.DoesNotContain("parameters", serialized);
    }

    [Fact]
    public void Mapper_builds_the_deep_link_from_the_web_base_and_selector_only()
    {
        var message = MetaWhatsAppMapper.Map(
            "+5511999999999",
            "alerta_saida",
            "pt_BR",
            "/paguai/requests/123",
            "https://app.example.test/"
        );

        Assert.NotNull(message.Template.Components);
        var component = Assert.Single(message.Template.Components);
        var text = Assert.Single(component.Parameters);
        Assert.Equal("https://app.example.test/paguai/requests/123", text.Text);
        var serialized = MetaWhatsAppMapper.Serialize(message);
        using var document = JsonDocument.Parse(serialized);
        var rootProperties = document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(["messaging_product", "to", "type", "template"], rootProperties);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "{\"messages\":[{\"id\":\"wamid.1\"}]}", NotificationProviderResultKind.Delivered)]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":{\"code\":131047}}", NotificationProviderResultKind.Rejected)]
    [InlineData(HttpStatusCode.RequestTimeout, "{}", NotificationProviderResultKind.Ambiguous)]
    public async Task Client_classifies_stable_outcomes(
        HttpStatusCode statusCode,
        string responseBody,
        NotificationProviderResultKind expected
    )
    {
        var client = BuildClient(statusCode, responseBody);
        var result = await client.SendAsync(
            MetaWhatsAppMapper.Map("+5511999999999", "t", "pt_BR", null, "https://app.example.test"),
            CancellationToken.None
        );

        Assert.Equal(expected, result.Kind);
    }

    [Fact]
    public async Task Client_maps_server_errors_and_rate_limits_to_transient()
    {
        foreach (var status in new[] { HttpStatusCode.TooManyRequests, HttpStatusCode.BadGateway })
        {
            var client = BuildClient(status, "{}");
            await Assert.ThrowsAsync<NotificationProviderTransientException>(() =>
                client.SendAsync(
                    MetaWhatsAppMapper.Map(
                        "+5511999999999",
                        "t",
                        "pt_BR",
                        null,
                        "https://app.example.test"
                    ),
                    CancellationToken.None
                )
            );
        }
    }

    private static MetaWhatsAppClient BuildClient(HttpStatusCode status, string body) =>
        new(
            new HttpClient(
                new StubHandler(
                    new HttpResponseMessage(status) { Content = new StringContent(body) }
                )
            ),
            Options(),
            NullLogger<MetaWhatsAppClient>.Instance
        );

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
