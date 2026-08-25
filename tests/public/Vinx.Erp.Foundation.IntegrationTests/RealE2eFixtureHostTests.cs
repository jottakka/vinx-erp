using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealE2eFixtureHostCollection
{
    public const string Name = "Real E2E fixture host";
}

[Collection(RealE2eFixtureHostCollection.Name)]
public sealed class RealE2eFixtureHostTests
{
    private const string CapabilityDeniedBody = "{\"code\":\"capability_denied\"}";

    [Fact]
    public async Task Unknown_profile_is_rejected_before_fixture_start()
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => E2eFixtureHost.StartAsync("unknown", CancellationToken.None));

        Assert.Equal("profileName", exception.ParamName);
    }

    [Theory]
    [InlineData("maria", "Alfa,Beta", "a2000000-0000-4000-8000-000000000001,a2000000-0000-4000-8000-000000000002")]
    [InlineData("beatriz", "Alfa", "a2000000-0000-4000-8000-000000000001")]
    [InlineData("joao", "Beta", "a2000000-0000-4000-8000-000000000002")]
    [InlineData("clara", "Gama", "a2000000-0000-4000-8000-000000000003")]
    public void Profile_readiness_maps_exact_own_memberships(
        string profileName,
        string companyNames,
        string companyIds)
    {
        var receipt = new E2eDatasetReceipt(
            Guid.Parse("a1000000-0000-4000-8000-000000000001"),
            Guid.Parse("a2000000-0000-4000-8000-000000000001"),
            Guid.Parse("a2000000-0000-4000-8000-000000000002"),
            Guid.Parse("a2000000-0000-4000-8000-000000000003"));

        var expected = E2eFixtureHost.ExpectedCompaniesForProfile(profileName, receipt);

        Assert.Equal(
            companyNames.Split(','),
            expected.Select(company => company.CompanyName).ToArray());
        Assert.Equal(
            companyIds.Split(',').Select(Guid.Parse).ToArray(),
            expected.Select(company => company.CompanyId).ToArray());
    }

    [Fact]
    public async Task Maria_host_exposes_real_authorized_readback_on_dynamic_loopback()
    {
        await using var host = await E2eFixtureHost.StartAsync("maria", CancellationToken.None);

        Assert.Equal("maria", host.ProfileName);
        Assert.Equal("http", host.Origin.Scheme);
        Assert.Equal("127.0.0.1", host.Origin.Host);
        Assert.True(host.Origin.Port > 0);
        Assert.Equal("/", host.Origin.AbsolutePath);
        Assert.Empty(host.Origin.Query);
        Assert.Empty(host.Origin.Fragment);
        Assert.False(await host.WolverineStorageExistsAsync());

        using var client = CreateClient(host.Origin);
        using var health = await client.GetAsync("/health");
        health.EnsureSuccessStatusCode();

        using var bootstrap = await GetJsonAsync(client, "/foundation/bootstrap");
        var companies = bootstrap.RootElement.GetProperty("companies").EnumerateArray().ToArray();
        Assert.Equal(
            ["Alfa", "Beta"],
            companies
                .Select(company => company.GetProperty("companyName").GetString()!)
                .ToArray());
        Assert.Equal(
            [host.Receipt.AlfaCompanyId, host.Receipt.BetaCompanyId],
            companies.Select(company => company.GetProperty("companyId").GetGuid()).ToArray());
        Assert.DoesNotContain(
            companies,
            company => company.GetProperty("companyId").GetGuid() == host.Receipt.GamaCompanyId);

        using var employees = await GetJsonAsync(
            client,
            $"/foundation/companies/{host.Receipt.AlfaCompanyId}/employees");
        Assert.Equal(
            ["Beatriz Costa", "Maria Silva"],
            employees.RootElement
                .EnumerateArray()
                .Select(employee => employee.GetProperty("displayName").GetString()!)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray());

        using var paguai = await GetJsonAsync(
            client,
            $"/paguai/requests?companyId={host.Receipt.BetaCompanyId}");
        var mariaBetaDraft = Assert.Single(paguai.RootElement.EnumerateArray());
        Assert.Equal("Serviços contábeis", mariaBetaDraft.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.String, mariaBetaDraft.GetProperty("amount").ValueKind);
        Assert.Equal("8500.00", mariaBetaDraft.GetProperty("amount").GetString());

        using var cards = await GetJsonAsync(
            client,
            $"/atendimento/cards?companyId={host.Receipt.AlfaCompanyId}");
        Assert.Equal(3, cards.RootElement.GetArrayLength());
        Assert.Equal(
            ["concluido", "em_andamento", "novo"],
            cards.RootElement
                .EnumerateArray()
                .Select(card => card.GetProperty("column").GetString()!)
                .OrderBy(column => column, StringComparer.Ordinal)
                .ToArray());

        using var inbox = await GetJsonAsync(client, "/notifications/inbox?limit=10");
        var notifications = inbox.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(3, notifications.Length);
        Assert.Single(
            notifications,
            notification => notification.GetProperty("readAt").ValueKind == JsonValueKind.String);
        Assert.All(
            notifications,
            notification =>
            {
                var action = notification.GetProperty("action");
                Assert.Equal(JsonValueKind.Object, action.ValueKind);
                Assert.Contains(
                    action.GetProperty("href").GetString(),
                    new[] { "/paguai", "/atendimento" });
            });

        await AssertCapabilityDeniedAsync(
            client,
            $"/foundation/companies/{host.Receipt.GamaCompanyId}/employees");
        await AssertCapabilityDeniedAsync(
            client,
            $"/paguai/requests?companyId={host.Receipt.GamaCompanyId}");

        var firstDisposal = host.DisposeAsync().AsTask();
        var repeatedDisposal = host.DisposeAsync().AsTask();
        Assert.Same(firstDisposal, repeatedDisposal);
        await firstDisposal;
    }

    [Fact]
    public async Task Other_profiles_expose_their_exact_authorized_readbacks_serially()
    {
        foreach (var profileName in new[] { "beatriz", "joao", "clara" })
        {
            await using var host = await E2eFixtureHost.StartAsync(
                profileName,
                CancellationToken.None);
            using var client = CreateClient(host.Origin);

            switch (profileName)
            {
                case "beatriz":
                    Assert.Equal(
                        ["Beatriz Costa", "Maria Silva"],
                        await ReadEmployeeNamesAsync(client, host.Receipt.AlfaCompanyId));
                    using (var cards = await GetJsonAsync(
                        client,
                        $"/atendimento/cards?companyId={host.Receipt.AlfaCompanyId}"))
                    {
                        Assert.Equal(3, cards.RootElement.GetArrayLength());
                        Assert.Equal(
                            ["concluido", "em_andamento", "novo"],
                            cards.RootElement
                                .EnumerateArray()
                                .Select(card => card.GetProperty("column").GetString()!)
                                .OrderBy(column => column, StringComparer.Ordinal)
                                .ToArray());
                    }
                    break;

                case "joao":
                    using (var paguai = await GetJsonAsync(
                        client,
                        $"/paguai/requests?companyId={host.Receipt.BetaCompanyId}"))
                    {
                        var draft = Assert.Single(paguai.RootElement.EnumerateArray());
                        Assert.Equal("Hospedagem em nuvem", draft.GetProperty("title").GetString());
                        Assert.Equal(JsonValueKind.String, draft.GetProperty("amount").ValueKind);
                        Assert.Equal("2340.75", draft.GetProperty("amount").GetString());
                    }
                    break;

                case "clara":
                    using (var cards = await GetJsonAsync(
                        client,
                        $"/atendimento/cards?companyId={host.Receipt.GamaCompanyId}"))
                    {
                        var card = Assert.Single(cards.RootElement.EnumerateArray());
                        Assert.Equal("Caso restrito", card.GetProperty("title").GetString());
                        Assert.Equal("em_andamento", card.GetProperty("column").GetString());
                    }
                    using (var inbox = await GetJsonAsync(client, "/notifications/inbox?limit=10"))
                    {
                        var notification = Assert.Single(
                            inbox.RootElement.GetProperty("items").EnumerateArray());
                        Assert.Equal(
                            host.Receipt.GamaCompanyId,
                            notification.GetProperty("companyId").GetGuid());
                        Assert.Equal(JsonValueKind.Null, notification.GetProperty("readAt").ValueKind);
                        Assert.Equal(
                            "/atendimento",
                            notification.GetProperty("action").GetProperty("href").GetString());
                    }
                    break;
            }
        }
    }

    [Fact]
    public async Task Concurrent_hosts_keep_database_and_api_mutations_isolated()
    {
        var firstStart = E2eFixtureHost.StartAsync("maria", CancellationToken.None);
        var secondStart = E2eFixtureHost.StartAsync("maria", CancellationToken.None);
        E2eFixtureHost[] hosts;
        try
        {
            hosts = await Task.WhenAll(firstStart, secondStart);
        }
        catch (Exception primaryFailure)
        {
            await FixtureCleanup.CompleteAsync(
                primaryFailure,
                () => DisposeSuccessfulStartAsync(firstStart),
                () => DisposeSuccessfulStartAsync(secondStart));
            throw;
        }

        var first = hosts[0];
        var second = hosts[1];
        var firstApiPort = first.Origin.Port;
        var secondApiPort = second.Origin.Port;
        var firstDatabaseEndpoint = (first.DatabaseHost, first.DatabasePort);
        var secondDatabaseEndpoint = (second.DatabaseHost, second.DatabasePort);

        try
        {
            Assert.NotEqual(first.Origin, second.Origin);
            Assert.NotEqual(first.DatabaseName, second.DatabaseName);
            Assert.NotEqual(firstDatabaseEndpoint, secondDatabaseEndpoint);

            using var firstClient = CreateClient(first.Origin);
            using var secondClient = CreateClient(second.Origin);
            Assert.Equal(
                ["Beatriz Costa", "Maria Silva"],
                await ReadEmployeeNamesAsync(firstClient, first.Receipt.AlfaCompanyId));
            Assert.Equal(
                ["Beatriz Costa", "Maria Silva"],
                await ReadEmployeeNamesAsync(secondClient, second.Receipt.AlfaCompanyId));

            var antiforgeryToken = await GetAntiforgeryTokenAsync(firstClient);
            using var createRequest = new HttpRequestMessage(
                HttpMethod.Post,
                $"/foundation/companies/{first.Receipt.AlfaCompanyId}/employees")
            {
                Content = JsonContent.Create(new
                {
                    requestId = Guid.NewGuid(),
                    companyId = first.Receipt.AlfaCompanyId,
                    displayName = "Pessoa isolada A",
                    email = "pessoa.isolada.a@example.test",
                }),
            };
            createRequest.Headers.Add("X-CSRF-TOKEN", antiforgeryToken);
            using var created = await firstClient.SendAsync(createRequest);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);

            Assert.Equal(
                ["Beatriz Costa", "Maria Silva", "Pessoa isolada A"],
                await ReadEmployeeNamesAsync(firstClient, first.Receipt.AlfaCompanyId));
            Assert.Equal(
                ["Beatriz Costa", "Maria Silva"],
                await ReadEmployeeNamesAsync(secondClient, second.Receipt.AlfaCompanyId));
        }
        finally
        {
            await Task.WhenAll(first.DisposeAsync().AsTask(), second.DisposeAsync().AsTask());
        }

        await AssertPortClosedAsync("127.0.0.1", firstApiPort);
        await AssertPortClosedAsync("127.0.0.1", secondApiPort);
        await AssertPortClosedAsync(firstDatabaseEndpoint.DatabaseHost, firstDatabaseEndpoint.DatabasePort);
        await AssertPortClosedAsync(secondDatabaseEndpoint.DatabaseHost, secondDatabaseEndpoint.DatabasePort);
    }

    private static HttpClient CreateClient(Uri origin) => new(new HttpClientHandler
    {
        CookieContainer = new CookieContainer(),
        UseCookies = true,
    })
    {
        BaseAddress = origin,
        Timeout = TimeSpan.FromSeconds(15),
    };

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string requestUri)
    {
        using var response = await client.GetAsync(requestUri);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<string[]> ReadEmployeeNamesAsync(HttpClient client, Guid companyId)
    {
        using var document = await GetJsonAsync(
            client,
            $"/foundation/companies/{companyId}/employees");
        return document.RootElement
            .EnumerateArray()
            .Select(employee => employee.GetProperty("displayName").GetString()!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        using var document = await GetJsonAsync(
            client,
            "/foundation/identity/browser-session/antiforgery");
        return document.RootElement.GetProperty("requestToken").GetString()
            ?? throw new InvalidOperationException("The antiforgery request token was empty.");
    }

    private static async Task AssertCapabilityDeniedAsync(HttpClient client, string requestUri)
    {
        using var response = await client.GetAsync(requestUri);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(CapabilityDeniedBody, await response.Content.ReadAsStringAsync());
    }

    private static async ValueTask DisposeSuccessfulStartAsync(Task<E2eFixtureHost> startTask)
    {
        E2eFixtureHost host;
        try
        {
            host = await startTask;
        }
        catch
        {
            // StartAsync owns cleanup when it fails.
            return;
        }

        await host.DisposeAsync();
    }

    private static async Task AssertPortClosedAsync(string host, int port)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < timeout)
        {
            using var client = new TcpClient();
            try
            {
                using var attempt = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                await client.ConnectAsync(host, port, attempt.Token);
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException)
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"Port {host}:{port} remained open after fixture disposal.");
    }
}
