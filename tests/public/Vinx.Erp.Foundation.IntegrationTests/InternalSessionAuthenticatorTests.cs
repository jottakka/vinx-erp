using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class InternalSessionAuthenticatorTests
{
    [Fact]
    public async Task Authenticated_principal_is_admitted_with_the_resolved_internal_actor()
    {
        await using var fixture = new FoundationPostgresFixture();
        await fixture.StartAsync();
        await fixture.MigrateAsync();
        var userId = Guid.NewGuid();
        var externalIdentityId = Guid.NewGuid();
        var proof = new ExternalIdentityProof(
            new ExternalIdentityKey("https://project.supabase.co/auth/v1", "browser-subject"),
            Guid.NewGuid(),
            DateTimeOffset.Parse("2026-08-16T12:00:00+00:00"),
            DateTimeOffset.Parse("2026-08-16T13:00:00+00:00"),
            AuthenticatorAssuranceLevel.Aal2);
        var correlationId = Guid.NewGuid();

        await using (var administrator = fixture.CreateAdministratorDbContext())
        {
            administrator.AddRange(
                new User(userId, "Browser", "browser-authenticator@example.test"),
                new ExternalIdentity(externalIdentityId, userId, proof.Key.Issuer, proof.Key.Subject));
            await administrator.SaveChangesAsync();
        }

        await using var runtime = fixture.CreateRuntimeDbContext();
        var authenticator = new InternalSessionAuthenticator(
            new ProofIdentityProvider(proof),
            new InternalIdentityResolver(runtime),
            new InternalSessionAdmissionService(
                runtime,
                new AuditAppender(runtime),
                NullLogger<InternalSessionAdmissionService>.Instance),
            NullLogger<InternalSessionAuthenticator>.Instance);

        var result = await authenticator.AuthenticateAsync(
            new ClaimsPrincipal(new ClaimsIdentity("browser")),
            correlationId,
            CancellationToken.None);

        Assert.Equal(InternalSessionAuthenticationState.Authenticated, result.State);
        var actor = Assert.IsType<ActorContext>(result.Actor);
        Assert.Equal(userId, actor.UserId);
        Assert.Equal(correlationId, actor.CorrelationId);
        Assert.Equal(externalIdentityId, actor.ExternalIdentityId);
        Assert.Equal(AuthenticatorAssuranceLevel.Aal2, actor.AssuranceLevel);

        await using var verification = fixture.CreateAdministratorDbContext();
        Assert.Single(await verification.UserSessions.ToListAsync());
        var audit = Assert.Single(await verification.AuditEvents.ToListAsync());
        Assert.Equal("identity_session_admitted", audit.Action);
    }

    private sealed class ProofIdentityProvider(ExternalIdentityProof proof) : IIdentityProvider
    {
        public bool TryGetExternalIdentityProof(ClaimsPrincipal principal, out ExternalIdentityProof result)
        {
            result = proof;
            return true;
        }
    }
}
