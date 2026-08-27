using Npgsql;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Vinx.Erp.Foundation.Notifications;
using Vinx.Erp.Notifications.Providers.Email;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

[Collection("notification-worker")]
public sealed class EmailNotificationTests
{
    private const string TestSubject = "Alerta de pagamento";
    private const string TestBodyHtml = "<p>Um novo rascunho está disponível.</p>";

    private static EmailProviderOptions SmtpOptions(int port, string user, string password) =>
        new()
        {
            Host = "127.0.0.1",
            Port = port,
            UseTls = false,
            Username = user,
            Password = password,
            FromAddress = "noreply@example.test",
            FromName = "Vinx ERP",
            TimeoutSeconds = 5,
        };

    [Fact]
    public async Task Eligible_recipient_receives_the_approved_email_template()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(factory);
        await NotificationIntentSeed.SeedEmailPreferenceAsync(
            factory,
            seed.CompanyId,
            seed.RecipientUserId
        );
        await NotificationIntentSeed.SeedEmailTemplateAsync(
            factory,
            seed.CompanyId,
            TestSubject,
            TestBodyHtml
        );
        await using var smtp = new FakeSmtpServer("smtp-user", "smtp-pass");
        var enqueued = await EnqueueEmailAsync(factory, seed);
        var deliveryId = enqueued.Email!.DeliveryId!.Value;

        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            smtpOptions: SmtpOptions(smtp.Port, "smtp-user", "smtp-pass")
        );
        using var client = worker.CreateClient();

        var state = await NotificationIntentSeed.WaitUntilDeliveryAsync(
            factory,
            deliveryId,
            "delivered",
            TimeSpan.FromSeconds(30)
        );
        Assert.Equal("delivered", state.Status);
        Assert.Equal(1, state.AttemptCount);
        Assert.True(state.Resolved);
        Assert.Null(state.FailureReason);

        var mail = Assert.Single(smtp.Mails);
        Assert.NotNull(mail.Recipient);
        Assert.EndsWith("@example.test", mail.Recipient);
        Assert.Contains(TestSubject, mail.Data);
        Assert.Equal(1L, await CountFinalAuditsAsync(factory, deliveryId));
    }

    [Fact]
    public async Task Opted_out_recipient_suppresses_email_but_keeps_in_app()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(factory);
        await NotificationIntentSeed.SeedEmailPreferenceAsync(
            factory,
            seed.CompanyId,
            seed.RecipientUserId,
            optedIn: false
        );
        await NotificationIntentSeed.SeedEmailTemplateAsync(
            factory,
            seed.CompanyId,
            TestSubject,
            TestBodyHtml
        );

        var result = await EnqueueEmailAsync(factory, seed);

        Assert.NotNull(result.Email);
        Assert.False(result.Email!.Created);
        Assert.Null(result.Email.DeliveryId);
        Assert.Equal("suppressed_channel_opt_out", result.Email.Status);
        Assert.NotNull(result.InApp);
        Assert.True(result.InApp!.Created);
    }

    [Fact]
    public async Task Unapproved_email_template_keeps_enqueue_invalid()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(factory);
        await NotificationIntentSeed.SeedEmailPreferenceAsync(
            factory,
            seed.CompanyId,
            seed.RecipientUserId
        );
        // Template de e-mail reprovado: o enfileiramento rejeita como inválido,
        // sem criar intenção e sem publicar mensagem de entrega.
        await NotificationIntentSeed.SeedEmailTemplateAsync(
            factory,
            seed.CompanyId,
            TestSubject,
            TestBodyHtml,
            approved: false
        );
        var enqueued = await EnqueueEmailAsync(factory, seed);

        Assert.Equal(NotificationIntentEnqueueState.Invalid, enqueued.State);
    }

    [Fact]
    public async Task Rejected_authentication_fails_permanently_without_retry()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var seed = await NotificationIntentSeed.CreateCompanyWithRecipientAsync(factory);
        await NotificationIntentSeed.SeedEmailPreferenceAsync(
            factory,
            seed.CompanyId,
            seed.RecipientUserId
        );
        await NotificationIntentSeed.SeedEmailTemplateAsync(
            factory,
            seed.CompanyId,
            TestSubject,
            TestBodyHtml
        );
        await using var smtp = new FakeSmtpServer("smtp-user", "smtp-pass")
        {
            RejectAuthentication = true,
        };
        var enqueued = await EnqueueEmailAsync(factory, seed);
        var deliveryId = enqueued.Email!.DeliveryId!.Value;

        await using var worker = new NotificationWorkerHostFactory(
            factory.Fixture.WorkerConnectionString,
            smtpOptions: SmtpOptions(smtp.Port, "smtp-user", "smtp-pass")
        );
        using var client = worker.CreateClient();

        var state = await NotificationIntentSeed.WaitUntilDeliveryAsync(
            factory,
            deliveryId,
            "provider_failed",
            TimeSpan.FromSeconds(30)
        );
        Assert.Equal("provider_failed", state.Status);
        Assert.Equal("provider_rejected", state.FailureReason);
        Assert.Equal(1, state.AttemptCount);
        Assert.True(state.Resolved);
        Assert.Empty(smtp.Mails);
        Assert.Equal(1L, await CountFinalAuditsAsync(factory, deliveryId));
    }

    private static async Task<NotificationEnqueueResult> EnqueueEmailAsync(
        FoundationApiFactory factory,
        NotificationCompanySeed seed
    )
    {
        return await NotificationIntentSeed.EnqueueAsync(
            factory,
            new NotificationIntentCommand(
                seed.CompanyId,
                seed.RecipientUserId,
                NotificationIntentSeed.NewEventKey(),
                seed.InAppTemplateKey,
                EmailTemplateKey: NotificationIntentSeed.EmailTemplateKey
            )
        );
    }

    private static async Task<long> CountFinalAuditsAsync(
        FoundationApiFactory factory,
        Guid deliveryId
    )
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*)
            FROM audit.audit_events
            WHERE action = 'notification.delivery.provider'
              AND resource_id = @delivery_id
              AND outcome = 'success'
            """,
            administrator
        );
        command.Parameters.AddWithValue("delivery_id", deliveryId);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
