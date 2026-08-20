using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Notifications;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class NotificationInboxTests
{
    [Fact]
    public async Task Authorized_actor_lists_delivered_in_app_intents_with_company_of_origin()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var alfaCompanyId = await NotificationInboxSeed.CreateCompanyForActorAsync(factory);
        var betaCompanyId = await NotificationInboxSeed.CreateCompanyForActorAsync(factory);
        await NotificationInboxSeed.CreateDeliveredInAppIntentAsync(factory, alfaCompanyId);
        await NotificationInboxSeed.CreateDeliveredInAppIntentAsync(factory, betaCompanyId);

        var result = await ListAsync(factory, cursor: null, limit: null);

        Assert.Equal(NotificationInboxState.Listed, result.State);
        Assert.NotNull(result.Page);
        Assert.Equal(2, result.Page!.Items.Count);
        Assert.Equal(
            new[] { betaCompanyId, alfaCompanyId },
            result.Page.Items.Select(item => item.CompanyId).ToArray()
        );
        foreach (var item in result.Page.Items)
        {
            Assert.Equal("Sistema", item.Source);
            Assert.Null(item.Event);
            Assert.Null(item.Action);
            Assert.Null(item.ReadAt);
        }
    }

    [Fact]
    public async Task Pagination_is_stable_without_duplicates_or_losses()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var companyId = await NotificationInboxSeed.CreateCompanyForActorAsync(factory);
        var deliveryIds = new List<Guid>();
        for (var index = 0; index < 3; index++)
        {
            deliveryIds.Add(
                await NotificationInboxSeed.CreateDeliveredInAppIntentAsync(factory, companyId)
            );
        }

        var firstPage = await ListAsync(factory, cursor: null, limit: 2);

        Assert.Equal(NotificationInboxState.Listed, firstPage.State);
        Assert.Equal(2, firstPage.Page!.Items.Count);
        Assert.NotNull(firstPage.Page.NextCursor);

        var secondPage = await ListAsync(factory, firstPage.Page.NextCursor, 2);

        Assert.Equal(NotificationInboxState.Listed, secondPage.State);
        Assert.Single(secondPage.Page!.Items);
        Assert.Null(secondPage.Page.NextCursor);

        var listedIds = firstPage.Page.Items.Concat(secondPage.Page.Items).Select(i => i.Id).ToArray();
        Assert.Equal(3, listedIds.Distinct().Count());
        Assert.Empty(listedIds.Except(deliveryIds));
        Assert.Empty(deliveryIds.Except(listedIds));
    }

    [Fact]
    public async Task Equal_timestamps_are_tie_broken_by_id_desc()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var companyId = await NotificationInboxSeed.CreateCompanyForActorAsync(factory);
        var deliveryIds = new List<Guid>();
        for (var index = 0; index < 3; index++)
        {
            deliveryIds.Add(
                await NotificationInboxSeed.CreateDeliveredInAppIntentAsync(factory, companyId)
            );
        }
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var pin = administrator.CreateCommand();
        pin.CommandText =
            """
            UPDATE notifications.notification_intents
            SET created_at = '2026-08-19T12:00:00.000000+00:00'::timestamptz
            WHERE id = ANY(@ids)
            """;
        pin.Parameters.AddWithValue("ids", deliveryIds.ToArray());
        await pin.ExecuteNonQueryAsync();

        var firstPage = await ListAsync(factory, cursor: null, limit: 2);
        var secondPage = await ListAsync(factory, firstPage.Page!.NextCursor, 2);

        var ordered = deliveryIds.OrderDescending().ToList();
        var listedIds = firstPage.Page!.Items.Concat(secondPage.Page!.Items).Select(i => i.Id).ToList();
        Assert.Equal(ordered, listedIds);
        Assert.Equal(3, listedIds.Distinct().Count());
    }

    [Fact]
    public async Task Insert_ahead_of_the_cursor_is_visible_only_after_refresh()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var companyId = await NotificationInboxSeed.CreateCompanyForActorAsync(factory);
        for (var index = 0; index < 3; index++)
        {
            await NotificationInboxSeed.CreateDeliveredInAppIntentAsync(factory, companyId);
        }

        var firstPage = await ListAsync(factory, cursor: null, limit: 2);
        var newestId = await NotificationInboxSeed.CreateDeliveredInAppIntentAsync(
            factory,
            companyId
        );

        var secondPage = await ListAsync(factory, firstPage.Page!.NextCursor, 2);

        Assert.Single(secondPage.Page!.Items);
        var listedIds = firstPage.Page.Items.Concat(secondPage.Page.Items).Select(i => i.Id).ToHashSet();
        Assert.DoesNotContain(newestId, listedIds);

        var refreshed = await ListAsync(factory, cursor: null, limit: 10);
        Assert.Contains(refreshed.Page!.Items, item => item.Id == newestId);
    }

    [Fact]
    public async Task Unread_count_follows_the_listed_items_and_marking()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var companyId = await NotificationInboxSeed.CreateCompanyForActorAsync(factory);
        var deliveryId = await NotificationInboxSeed.CreateDeliveredInAppIntentAsync(factory, companyId);
        await NotificationInboxSeed.CreateDeliveredInAppIntentAsync(factory, companyId);

        var before = await CountUnreadAsync(factory);
        var marked = await MarkReadAsync(factory, deliveryId);
        var after = await CountUnreadAsync(factory);

        Assert.Equal(NotificationMarkReadState.Read, marked.State);
        Assert.Equal(2, before.UnreadCount);
        Assert.Equal(1, after.UnreadCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-base64!")]
    [InlineData("djEuOTMyMy4wMDAwMDAwLTAwMDAtMDAwMC0wMDAwLTAwMDAwMDAwMDAwMDA=")] // non-canonical uuid
    [InlineData("djIuMTIzLjA=")] // wrong version
    [InlineData("djEuYWJjLjA=")] // non-numeric micros
    [InlineData("QUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFB")] // 136 > 128 bytes recebidos
    public async Task Invalid_cursors_are_rejected_without_side_effects(string cursor)
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var companyId = await NotificationInboxSeed.CreateCompanyForActorAsync(factory);
        await NotificationInboxSeed.CreateDeliveredInAppIntentAsync(factory, companyId);

        var result = await ListAsync(factory, cursor, limit: null);

        Assert.Equal(NotificationInboxState.Invalid, result.State);
        Assert.Null(result.Page);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(51)]
    [InlineData(500)]
    public async Task Out_of_range_limits_are_rejected(int limit)
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var companyId = await NotificationInboxSeed.CreateCompanyForActorAsync(factory);
        await NotificationInboxSeed.CreateDeliveredInAppIntentAsync(factory, companyId);

        var result = await ListAsync(factory, cursor: null, limit);

        Assert.Equal(NotificationInboxState.Invalid, result.State);
    }

    [Fact]
    public async Task Repeated_read_is_idempotent_and_audits_once()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var companyId = await NotificationInboxSeed.CreateCompanyForActorAsync(factory);
        var deliveryId = await NotificationInboxSeed.CreateDeliveredInAppIntentAsync(factory, companyId);

        var first = await MarkReadAsync(factory, deliveryId);
        var second = await MarkReadAsync(factory, deliveryId);

        Assert.Equal(NotificationMarkReadState.Read, first.State);
        Assert.Equal(NotificationMarkReadState.Read, second.State);
        Assert.Equal(1, await NotificationInboxSeed.CountReadAuditsAsync(factory, deliveryId));
        var state = await NotificationInboxSeed.ReadInboxStateAsync(factory, deliveryId);
        Assert.NotNull(state.ReadAt);
        Assert.Equal(factory.ActorUserId, state.ReadByUserId);
    }

    [Fact]
    public async Task Allowlisted_deep_links_become_safe_actions_and_unknown_keys_stay_generic()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var companyId = await NotificationInboxSeed.CreateCompanyForActorAsync(factory);
        var allowedId = await NotificationInboxSeed.CreateDeliveredInAppIntentAsync(
            factory,
            companyId,
            "/paguai"
        );
        var unknownLinkId = await NotificationInboxSeed.CreateDeliveredInAppIntentAsync(
            factory,
            companyId,
            "/rota-desconhecida"
        );

        var result = await ListAsync(factory, cursor: null, limit: 10);

        var allowed = result.Page!.Items.Single(item => item.Id == allowedId);
        var unknownLink = result.Page.Items.Single(item => item.Id == unknownLinkId);
        Assert.NotNull(allowed.Action);
        Assert.Equal("paguai", allowed.Action!.Kind);
        Assert.Equal("/paguai", allowed.Action.Href);
        Assert.Null(unknownLink.Action);
        // A resposta nunca ecoa o seletor bruto.
        Assert.DoesNotContain(result.Page.Items, item =>
            item.Action is { Href: "/rota-desconhecida" }
        );
    }

    [Fact]
    public async Task Inbox_endpoints_return_the_frozen_envelope_with_no_store()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var companyId = await NotificationInboxSeed.CreateCompanyForActorAsync(factory);
        var deliveryId = await NotificationInboxSeed.CreateDeliveredInAppIntentAsync(factory, companyId);
        using var client = factory.CreateClient();

        using var listResponse = await client.GetAsync("/notifications/inbox?limit=10");

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.True(listResponse.Headers.CacheControl?.Private);
        Assert.True(listResponse.Headers.CacheControl?.NoStore);
        await AssertEnvelopeKeysAsync(listResponse);

        using var countResponse = await client.GetAsync("/notifications/inbox/unread-count");

        Assert.Equal(HttpStatusCode.OK, countResponse.StatusCode);
        Assert.True(countResponse.Headers.CacheControl?.Private);
        Assert.True(countResponse.Headers.CacheControl?.NoStore);
        using var countDocument = JsonDocument.Parse(
            await countResponse.Content.ReadAsStringAsync()
        );
        Assert.Equal(1, countDocument.RootElement.GetProperty("unreadCount").GetInt64());

        using var readResponse = await client.PostAsync(
            $"/notifications/inbox/{deliveryId}/read",
            content: null
        );

        Assert.Equal(HttpStatusCode.OK, readResponse.StatusCode);
        using var readDocument = JsonDocument.Parse(await readResponse.Content.ReadAsStringAsync());
        Assert.Equal("read", readDocument.RootElement.GetProperty("status").GetString());

        using var replayResponse = await client.PostAsync(
            $"/notifications/inbox/{deliveryId}/read",
            content: null
        );
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        using var replayDocument = JsonDocument.Parse(
            await replayResponse.Content.ReadAsStringAsync()
        );
        Assert.Equal("read", replayDocument.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, await NotificationInboxSeed.CountReadAuditsAsync(factory, deliveryId));
    }

    [Fact]
    public async Task Inbox_item_payload_exposes_only_allowlisted_fields()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var companyId = await NotificationInboxSeed.CreateCompanyForActorAsync(factory);
        await NotificationInboxSeed.CreateDeliveredInAppIntentAsync(
            factory,
            companyId,
            "/rota-desconhecida"
        );
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/notifications/inbox?limit=10");

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(payload);
        var item = document.RootElement.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(
            new[]
            {
                "action",
                "companyId",
                "createdAt",
                "event",
                "id",
                "readAt",
                "source",
            },
            item.EnumerateObject().Select(property => property.Name).OrderBy(name => name).ToArray()
        );
        Assert.DoesNotContain("template", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rota-desconhecida", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("event-", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inbox_http_validation_maps_bad_queries_and_ids_to_safe_errors()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        using var client = factory.CreateClient();

        using var badCursor = await client.GetAsync("/notifications/inbox?cursor=%21%21%21");
        Assert.Equal(HttpStatusCode.BadRequest, badCursor.StatusCode);
        await AssertErrorCodeAsync(badCursor, "invalid_request");

        using var badLimit = await client.GetAsync("/notifications/inbox?limit=0");
        Assert.Equal(HttpStatusCode.BadRequest, badLimit.StatusCode);

        using var nonNumericLimit = await client.GetAsync("/notifications/inbox?limit=abc");
        Assert.Equal(HttpStatusCode.BadRequest, nonNumericLimit.StatusCode);

        using var malformedId = await client.PostAsync(
            "/notifications/inbox/not-a-guid/read",
            content: null
        );
        Assert.Equal(HttpStatusCode.NotFound, malformedId.StatusCode);

        using var emptyId = await client.PostAsync(
            "/notifications/inbox/00000000-0000-0000-0000-000000000000/read",
            content: null
        );
        Assert.Equal(HttpStatusCode.BadRequest, emptyId.StatusCode);
        await AssertErrorCodeAsync(emptyId, "invalid_request");
    }

    [Fact]
    public async Task Unauthenticated_and_rate_limited_responses_are_empty_bodies_with_no_store()
    {
        await using var factory = await FoundationApiFactory.CreateWithClaimsAsync(null, null);
        using var client = factory.CreateClient();

        string limitedBody = "unset";
        CacheControlHeaderValue? limitedCacheControl = null;
        HttpStatusCode? limitedStatus = null;
        for (var attempt = 0; attempt < 80 && limitedStatus is null; attempt++)
        {
            using var response = await client.GetAsync("/notifications/inbox");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                limitedStatus = response.StatusCode;
                limitedCacheControl = response.Headers.CacheControl;
                limitedBody = await response.Content.ReadAsStringAsync();
                break;
            }

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.Private);
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.Empty(await response.Content.ReadAsStringAsync());
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, limitedStatus);
        Assert.True(limitedCacheControl?.Private);
        Assert.True(limitedCacheControl?.NoStore);
        Assert.Empty(limitedBody);
    }

    private static async Task<NotificationInboxResult> ListAsync(
        FoundationApiFactory factory,
        string? cursor,
        int? limit
    )
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<NotificationInboxService>();
        return await service.ListAsync(
            new ActorContext(factory.ActorUserId, factory.CorrelationId),
            cursor,
            limit,
            CancellationToken.None
        );
    }

    private static async Task<NotificationUnreadCountResult> CountUnreadAsync(
        FoundationApiFactory factory
    )
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<NotificationInboxService>();
        return await service.CountUnreadAsync(
            new ActorContext(factory.ActorUserId, factory.CorrelationId),
            CancellationToken.None
        );
    }

    private static async Task<NotificationMarkReadResult> MarkReadAsync(
        FoundationApiFactory factory,
        Guid deliveryId
    )
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<NotificationInboxService>();
        return await service.MarkReadAsync(
            new ActorContext(factory.ActorUserId, factory.CorrelationId),
            deliveryId,
            CancellationToken.None
        );
    }

    private static async Task AssertEnvelopeKeysAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rootKeys = document
            .RootElement.EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name)
            .ToArray();
        Assert.Equal(new[] { "items", "nextCursor" }, rootKeys);
    }

    private static async Task AssertErrorCodeAsync(HttpResponseMessage response, string code)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
    }
}
