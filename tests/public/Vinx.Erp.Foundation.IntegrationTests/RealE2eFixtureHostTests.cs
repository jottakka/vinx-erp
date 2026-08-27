using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Npgsql;
using Vinx.Erp.AI;
using Vinx.Erp.ApiHost.Agent;
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
            Guid.Parse("a2000000-0000-4000-8000-000000000003"),
            Guid.Parse("f4000000-0000-4000-8000-000000000001"));

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
    public async Task Shared_host_seeds_once_and_keeps_profile_RLS_isolated()
    {
        SharedE2eFixtureHost? sharedHost = null;
        var agentModel = new DeterministicE2eAgentModel();
        var agentRunOptions = new AgentRunOptions
        {
            Provider = AgentRunOptions.OpenAiCompatibleProvider,
            Endpoint = new Uri("https://agent-provider.example/v1"),
            Model = "test-agent-model",
            ApprovedHosts = ["agent-provider.example"],
            Vision = new AgentRunVisionOptions
            {
                Provider = AgentRunOptions.OpenAiCompatibleProvider,
                Endpoint = new Uri("https://agent-provider.example/v1"),
                Model = "test-agent-model",
                ApprovedHosts = ["agent-provider.example"],
                AllowedClassifications = ["agentic_attachment"],
            },
        };
        var mariaApiPort = 0;
        var claraApiPort = 0;
        var databasePort = 0;
        string? databaseHost = null;
        string? credentialFilePath = null;
        try
        {
            sharedHost = await SharedE2eFixtureHost.StartAsync(
                CancellationToken.None,
                agentModel,
                agentRunOptions,
                new E2eFixtureAttachmentStorage());
            mariaApiPort = sharedHost.MariaOrigin.Port;
            claraApiPort = sharedHost.ClaraOrigin.Port;
            databaseHost = sharedHost.DatabaseHost;
            databasePort = sharedHost.DatabasePort;
            credentialFilePath = sharedHost.DatabasePasswordFilePath;

            Assert.NotEqual(sharedHost.MariaOrigin, sharedHost.ClaraOrigin);
            Assert.Equal("127.0.0.1", sharedHost.MariaOrigin.Host);
            Assert.Equal("127.0.0.1", sharedHost.ClaraOrigin.Host);
            Assert.StartsWith("vinx_fixture_", sharedHost.DatabaseName, StringComparison.Ordinal);
            Assert.Equal("postgres", sharedHost.DatabaseUser);
            Assert.True(File.Exists(credentialFilePath));

            using var mariaClient = CreateClient(sharedHost.MariaOrigin);
            using var claraClient = CreateClient(sharedHost.ClaraOrigin);
            using var mariaBootstrap = await GetJsonAsync(mariaClient, "/foundation/bootstrap");
            using var claraBootstrap = await GetJsonAsync(claraClient, "/foundation/bootstrap");
            Assert.Equal("Maria Silva", mariaBootstrap.RootElement.GetProperty("user").GetProperty("displayName").GetString());
            Assert.Equal("Clara Oliveira", claraBootstrap.RootElement.GetProperty("user").GetProperty("displayName").GetString());
            Assert.Equal(
                ["Alfa", "Beta"],
                mariaBootstrap.RootElement.GetProperty("companies").EnumerateArray()
                    .Select(company => company.GetProperty("companyName").GetString()!)
                    .ToArray());
            Assert.Equal(
                ["Gama"],
                claraBootstrap.RootElement.GetProperty("companies").EnumerateArray()
                    .Select(company => company.GetProperty("companyName").GetString()!)
                    .ToArray());
            var alfaCompanyId = sharedHost.Receipt.AlfaCompanyId;
            var betaCompanyId = sharedHost.Receipt.BetaCompanyId;
            var gamaCompanyId = sharedHost.Receipt.GamaCompanyId;

            using (var mariaAlfaAgentResponse = await mariaClient.PostAsJsonAsync(
                "/agent/runs",
                new AgentRunRequest(
                    Guid.NewGuid(),
                    "read_only_summarize_v1",
                    "resuma a empresa Alfa",
                    alfaCompanyId)))
            {
                Assert.Equal(HttpStatusCode.OK, mariaAlfaAgentResponse.StatusCode);
                var result = await ReadAgentRunResponseAsync(mariaAlfaAgentResponse);
                Assert.Equal("result", result.Status);
                Assert.Equal("ok", result.ReasonCode);
                Assert.Equal(DeterministicE2eAgentModel.ResponseText, result.Text);
                Assert.Null(result.Sources);
                Assert.NotEqual(Guid.Empty, result.CorrelationId);
                Assert.Equal(
                    result.CorrelationId.ToString(),
                    Assert.Single(mariaAlfaAgentResponse.Headers.GetValues("X-Correlation-ID")));
            }

            Assert.Equal(1, agentModel.CallCount);
            Assert.Equal(1, agentModel.LastMessageCount);
            Assert.Equal(AgentRunOptions.OpenAiCompatibleProvider, agentModel.LastSelection?.ProviderId);
            Assert.Equal("test-agent-model", agentModel.LastSelection?.ModelId);

            var proposalRunRequestId = Guid.NewGuid();
            Guid proposalId;
            using (var mariaPaguaiAgentResponse = await mariaClient.PostAsJsonAsync(
                "/agent/runs",
                new AgentRunRequest(
                    proposalRunRequestId,
                    AgentTaskProfileCatalog.PaguaiFromDocumentProfileId,
                    E2eFixtureAttachmentStorage.PaguaiPrompt,
                    alfaCompanyId,
                    [sharedHost.Receipt.MariaAlfaAgenticAttachmentId])))
            {
                Assert.Equal(HttpStatusCode.OK, mariaPaguaiAgentResponse.StatusCode);
                var result = await ReadAgentRunResponseAsync(mariaPaguaiAgentResponse);
                Assert.Equal("requires_confirmation", result.Status);
                proposalId = Assert.IsType<Guid>(result.PaguaiProposalId);
                Assert.Null(result.Text);
            }

            Assert.Equal(2, agentModel.CallCount);
            using (var proposal = await GetJsonAsync(
                mariaClient,
                $"/paguai/draft-proposals/{proposalId}?companyId={alfaCompanyId}"))
            {
                Assert.Equal(proposalId, proposal.RootElement.GetProperty("id").GetGuid());
                Assert.Equal(sharedHost.Receipt.MariaAlfaAgenticAttachmentId,
                    proposal.RootElement.GetProperty("invoiceFileId").GetGuid());
                Assert.Equal("pending", proposal.RootElement.GetProperty("state").GetString());
            }

            using (var replay = await mariaClient.PostAsJsonAsync(
                "/agent/runs",
                new AgentRunRequest(
                    proposalRunRequestId,
                    AgentTaskProfileCatalog.PaguaiFromDocumentProfileId,
                    E2eFixtureAttachmentStorage.PaguaiPrompt,
                    alfaCompanyId,
                    [sharedHost.Receipt.MariaAlfaAgenticAttachmentId])))
            {
                Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
                Assert.Equal(proposalId, (await ReadAgentRunResponseAsync(replay)).PaguaiProposalId);
            }

            Assert.Equal(2, agentModel.CallCount);
            var proposalAntiforgery = await GetAntiforgeryTokenAsync(mariaClient);
            using (var confirmation = new HttpRequestMessage(
                HttpMethod.Post,
                $"/paguai/draft-proposals/{proposalId}/confirm")
            {
                Content = JsonContent.Create(new { companyId = alfaCompanyId, requestId = Guid.NewGuid() }),
            })
            {
                confirmation.Headers.Add("X-CSRF-TOKEN", proposalAntiforgery);
                using var confirmed = await mariaClient.SendAsync(confirmation);
                Assert.Equal(HttpStatusCode.Created, confirmed.StatusCode);
                using var draft = JsonDocument.Parse(await confirmed.Content.ReadAsStringAsync());
                Assert.Equal(proposalId, draft.RootElement.GetProperty("sourceProposalId").GetGuid());
            }

            await using (var administrator = await sharedHost.OpenAdministratorConnectionAsync())
            await using (var count = new NpgsqlCommand(
                "SELECT count(*) FROM paguai.requests WHERE source_proposal_id = @proposal_id", administrator))
            {
                count.Parameters.AddWithValue("proposal_id", proposalId);
                Assert.Equal(1L, Convert.ToInt64(await count.ExecuteScalarAsync()));
            }

            using (var mariaBetaAgentResponse = await mariaClient.PostAsJsonAsync(
                "/agent/runs",
                new AgentRunRequest(
                    Guid.NewGuid(),
                    "read_only_summarize_v1",
                    "resuma a empresa Beta",
                    betaCompanyId)))
            {
                Assert.Equal(HttpStatusCode.OK, mariaBetaAgentResponse.StatusCode);
                var result = await ReadAgentRunResponseAsync(mariaBetaAgentResponse);
                Assert.Equal("denied", result.Status);
                Assert.Equal("capability_denied", result.ReasonCode);
                Assert.Null(result.Text);
                Assert.Null(result.Sources);
            }

            Assert.Equal(2, agentModel.CallCount);

            using (var claraGamaAgentResponse = await claraClient.PostAsJsonAsync(
                "/agent/runs",
                new AgentRunRequest(
                    Guid.NewGuid(),
                    "read_only_summarize_v1",
                    "resuma a empresa Gama",
                    gamaCompanyId)))
            {
                Assert.Equal(HttpStatusCode.OK, claraGamaAgentResponse.StatusCode);
                var result = await ReadAgentRunResponseAsync(claraGamaAgentResponse);
                Assert.Equal("denied", result.Status);
                Assert.Equal("capability_denied", result.ReasonCode);
                Assert.Null(result.Text);
                Assert.Null(result.Sources);
            }

            Assert.Equal(2, agentModel.CallCount);

            Assert.Equal(
                ["Beatriz Costa", "Maria Silva"],
                await ReadEmployeeNamesAsync(mariaClient, alfaCompanyId));

            var antiforgeryToken = await GetAntiforgeryTokenAsync(mariaClient);
            using var createRequest = new HttpRequestMessage(
                HttpMethod.Post,
                $"/foundation/companies/{alfaCompanyId}/employees")
            {
                Content = JsonContent.Create(new
                {
                    requestId = Guid.NewGuid(),
                    companyId = alfaCompanyId,
                    displayName = "Pessoa compartilhada A",
                    email = "pessoa.compartilhada.a@example.test",
                }),
            };
            createRequest.Headers.Add("X-CSRF-TOKEN", antiforgeryToken);
            using var created = await mariaClient.SendAsync(createRequest);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);

            Assert.Equal(
                ["Beatriz Costa", "Maria Silva", "Pessoa compartilhada A"],
                await ReadEmployeeNamesAsync(mariaClient, alfaCompanyId));
            await AssertCapabilityDeniedAsync(
                claraClient,
                $"/foundation/companies/{alfaCompanyId}/employees");
            await AssertCapabilityDeniedAsync(
                mariaClient,
                $"/foundation/companies/{gamaCompanyId}/employees");

            await using (var administrator = await sharedHost.OpenAdministratorConnectionAsync())
            await using (var persisted = new NpgsqlCommand(
                "SELECT count(*) FROM foundation.employees WHERE company_id = @company_id AND display_name = @display_name",
                administrator))
            {
                persisted.Parameters.AddWithValue("company_id", alfaCompanyId);
                persisted.Parameters.AddWithValue("display_name", "Pessoa compartilhada A");
                Assert.Equal(1L, Convert.ToInt64(await persisted.ExecuteScalarAsync()));
            }

            using (var endpoint = new TcpClient())
            using (var endpointCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                await endpoint.ConnectAsync(databaseHost, databasePort, endpointCancellation.Token);
            }
        }
        finally
        {
            if (sharedHost is not null)
            {
                await sharedHost.DisposeAsync();
            }
        }

        Assert.NotNull(databaseHost);
        Assert.NotNull(credentialFilePath);
        Assert.False(File.Exists(credentialFilePath));
        await AssertPortClosedAsync("127.0.0.1", mariaApiPort);
        await AssertPortClosedAsync("127.0.0.1", claraApiPort);
        await AssertPortClosedAsync(databaseHost, databasePort);
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

    private static async Task<AgentRunResponse> ReadAgentRunResponseAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<AgentRunResponse>()
            ?? throw new InvalidOperationException("The agent run response body was not readable.");

    private static async Task AssertCapabilityDeniedAsync(HttpClient client, string requestUri)
    {
        using var response = await client.GetAsync(requestUri);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(CapabilityDeniedBody, await response.Content.ReadAsStringAsync());
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
            catch (SocketException)
            {
                // Only a refused connection proves the port is closed.
                return;
            }
            catch (OperationCanceledException)
            {
                // A slow or stalled connect is inconclusive; keep polling until the deadline.
            }

            await Task.Delay(100);
        }

        Assert.Fail($"Port {host}:{port} remained open after fixture disposal.");
    }

    private sealed record AgentRunRequest(
        Guid RequestId,
        string Journey,
        string Message,
        Guid CompanyId,
        IReadOnlyList<Guid>? Attachments = null);

    private sealed record AgentRunResponse(
        string Status,
        string? ReasonCode,
        string? Text,
        IReadOnlyList<AgentRunSourceRef>? Sources,
        Guid CorrelationId,
        Guid? PaguaiProposalId = null);

    private sealed record AgentRunSourceRef(string Reference);
}
