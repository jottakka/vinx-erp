using System.Net;
using System.Text;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.Platform;
using Xunit;

namespace Vinx.Erp.Foundation.UnitTests.Platform;

public sealed class SupabaseAdminOwnerInvitationClientTests
{
    private const string Issuer = "https://project.supabase.co/auth/v1/";
    private const string Audience = "authenticated";
    private const string ServiceRoleKey = "test-service-role-key";
    private const string RedirectUrl = "https://app.example.test/convite";

    [Fact]
    public async Task Invite_async_sends_only_the_provider_invite_contract_and_accepts_an_unconfirmed_exact_user()
    {
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, UserJson()));
        var client = Client(handler);

        var result = await client.InviteAsync("owner@example.test", CancellationToken.None);

        Assert.Equal(SupabaseOwnerInvitationResultKind.Succeeded, result.Kind);
        Assert.Equal(Guid.Parse("2a0399f0-a602-4d1f-8f79-22f4bf75f190"), result.Subject);
        Assert.Equal(
            $"{Issuer.TrimEnd('/')}/invite?redirect_to=https%3A%2F%2Fapp.example.test%2Fconvite",
            handler.RequestUri
        );
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal($"Bearer {ServiceRoleKey}", handler.Authorization);
        Assert.Equal(ServiceRoleKey, handler.ApiKey);
        Assert.Equal("{\"email\":\"owner@example.test\"}", handler.Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task Invite_async_treats_provider_client_rejection_as_definitive(
        HttpStatusCode status
    )
    {
        var client = Client(
            new StubHttpMessageHandler(_ => Json(status, "provider-body-must-not-escape"))
        );

        var result = await client.InviteAsync("owner@example.test", CancellationToken.None);

        Assert.Equal(SupabaseOwnerInvitationResultKind.Rejected, result.Kind);
        Assert.Null(result.Subject);
        Assert.DoesNotContain(
            "provider-body-must-not-escape",
            result.ToString(),
            StringComparison.Ordinal
        );
    }

    [Theory]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task Invite_async_marks_redirect_or_provider_failure_as_ambiguous(
        HttpStatusCode status
    )
    {
        var client = Client(
            new StubHttpMessageHandler(_ => Json(status, "provider-body-must-not-escape"))
        );

        var result = await client.InviteAsync("owner@example.test", CancellationToken.None);

        Assert.Equal(SupabaseOwnerInvitationResultKind.Ambiguous, result.Kind);
        Assert.Null(result.Subject);
        Assert.DoesNotContain(
            "provider-body-must-not-escape",
            result.ToString(),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task Invite_async_marks_a_malformed_success_body_as_ambiguous()
    {
        var client = Client(new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, "{")));

        var result = await client.InviteAsync("owner@example.test", CancellationToken.None);

        Assert.Equal(SupabaseOwnerInvitationResultKind.Ambiguous, result.Kind);
        Assert.Null(result.Subject);
    }

    [Fact]
    public async Task Invite_async_marks_an_oversized_success_body_as_ambiguous_without_parsing_it()
    {
        var client = Client(
            new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, new string('x', 32 * 1024 + 1)))
        );

        var result = await client.InviteAsync("owner@example.test", CancellationToken.None);

        Assert.Equal(SupabaseOwnerInvitationResultKind.Ambiguous, result.Kind);
        Assert.Null(result.Subject);
    }

    [Fact]
    public async Task Invite_async_marks_a_provider_timeout_as_ambiguous()
    {
        var client = Client(
            new StubHttpMessageHandler(_ => throw new TaskCanceledException("provider timeout"))
        );

        var result = await client.InviteAsync("owner@example.test", CancellationToken.None);

        Assert.Equal(SupabaseOwnerInvitationResultKind.Ambiguous, result.Kind);
        Assert.Null(result.Subject);
    }

    private static SupabaseAdminOwnerInvitationClient Client(StubHttpMessageHandler handler) =>
        new(
            new HttpClient(handler),
            new SupabaseAuthOptions { Issuer = Issuer, Audience = Audience },
            new SupabaseAdminOptions
            {
                ServiceRoleKey = ServiceRoleKey,
                InviteRedirectUrl = RedirectUrl,
            }
        );

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string UserJson() =>
        """
            {
              "id": "2a0399f0-a602-4d1f-8f79-22f4bf75f190",
              "aud": "authenticated",
              "email": "owner@example.test",
              "is_anonymous": false,
              "banned_until": null,
              "deleted_at": null
            }
            """;

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory
    ) : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? Authorization { get; private set; }
        public string? ApiKey { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            RequestUri = request.RequestUri?.AbsoluteUri;
            Method = request.Method;
            Authorization = request.Headers.Authorization?.ToString();
            ApiKey = request.Headers.GetValues("apikey").SingleOrDefault();
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return responseFactory(request);
        }
    }
}
