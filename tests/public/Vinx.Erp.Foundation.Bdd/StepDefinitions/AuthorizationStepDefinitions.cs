using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Reqnroll;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.Bdd.StepDefinitions;

[Binding]
public sealed class AuthorizationStepDefinitions
{
    private FoundationApiFactory? _factory;
    private HttpResponseMessage? _response;
    private AuthorizationResult<AssignRoleToMembershipResult>? _assignmentResult;
    private Guid _companyId;
    private Guid _roleId;
    private Guid _targetMembershipId;
    private IReadOnlyList<MembershipResponse>? _membershipMap;

    [Given("Ana has memberships in Alfa, Beta and Gama")]
    public async Task GivenAnaHasMembershipsInAlfaBetaAndGama()
    {
        _factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await using var administrator = Factory.Fixture.CreateAdministratorDbContext();
        var requestId = Guid.NewGuid();
        var group = new Group(Guid.NewGuid(), "Vinx Group", Factory.ActorUserId, requestId);
        var alfa = new Company(Guid.NewGuid(), group.Id, "Alfa", Factory.ActorUserId, requestId);
        var beta = new Company(Guid.NewGuid(), group.Id, "Beta", Factory.ActorUserId, requestId);
        var gama = new Company(Guid.NewGuid(), group.Id, "Gama", Factory.ActorUserId, requestId);
        var alfaMembership = new Membership(
            Guid.NewGuid(),
            alfa.Id,
            Factory.ActorUserId,
            Factory.ActorUserId,
            requestId
        );
        var betaMembership = new Membership(
            Guid.NewGuid(),
            beta.Id,
            Factory.ActorUserId,
            Factory.ActorUserId,
            requestId
        );
        var gamaMembership = new Membership(
            Guid.NewGuid(),
            gama.Id,
            Factory.ActorUserId,
            Factory.ActorUserId,
            requestId
        );
        var alfaRole = new Role(
            Guid.NewGuid(),
            alfa.Id,
            "Alfa approver",
            Factory.ActorUserId,
            requestId
        );
        var betaRole = new Role(
            Guid.NewGuid(),
            beta.Id,
            "Beta uploader",
            Factory.ActorUserId,
            requestId
        );
        var paymentApprove = new Capability(Guid.NewGuid(), "payment.approve");
        var filesUpload = await administrator.Capabilities.SingleAsync(capability =>
            capability.Name == KnownCapabilities.FilesUpload
        );

        administrator.AddRange(
            paymentApprove,
            group,
            alfa,
            beta,
            gama,
            alfaMembership,
            betaMembership,
            gamaMembership,
            alfaRole,
            betaRole,
            new RoleAssignment(
                Guid.NewGuid(),
                alfa.Id,
                alfaRole.Id,
                alfaMembership.Id,
                Factory.ActorUserId,
                requestId
            ),
            new RoleAssignment(
                Guid.NewGuid(),
                beta.Id,
                betaRole.Id,
                betaMembership.Id,
                Factory.ActorUserId,
                requestId
            ),
            new RoleCapability(
                Guid.NewGuid(),
                alfa.Id,
                alfaRole.Id,
                paymentApprove.Id,
                CapabilityScope.Company,
                Factory.ActorUserId,
                requestId
            ),
            new RoleCapability(
                Guid.NewGuid(),
                beta.Id,
                betaRole.Id,
                filesUpload.Id,
                CapabilityScope.Company,
                Factory.ActorUserId,
                requestId
            )
        );
        await administrator.SaveChangesAsync();
    }

    [When("she requests her membership capability map")]
    public async Task WhenSheRequestsHerMembershipCapabilityMap()
    {
        _response = await Factory.CreateClient().GetAsync("/foundation/memberships");
        _membershipMap = await _response.Content.ReadFromJsonAsync<List<MembershipResponse>>();
    }

    [Then("the membership map lists Alfa, Beta and Gama in that order")]
    public Task ThenTheMembershipMapListsAlfaBetaAndGamaInThatOrder()
    {
        Assert.Equal(
            ["Alfa", "Beta", "Gama"],
            MembershipMap.Select(membership => membership.CompanyName)
        );
        return Task.CompletedTask;
    }

    [Then("Alfa has only {string} and Beta has only {string}")]
    public Task ThenAlfaAndBetaHaveTheirCapabilities(string alfaCapability, string betaCapability)
    {
        Assert.Equal(alfaCapability, Assert.Single(MembershipMap[0].Capabilities).Name);
        Assert.Equal(betaCapability, Assert.Single(MembershipMap[1].Capabilities).Name);
        return Task.CompletedTask;
    }

    [Then("Gama has no effective capabilities")]
    public Task ThenGamaHasNoEffectiveCapabilities()
    {
        Assert.Empty(MembershipMap[2].Capabilities);
        return Task.CompletedTask;
    }

    [Given("Ana is authenticated")]
    public async Task GivenAnaIsAuthenticated()
    {
        _factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
    }

    [When("she requests her capability map without a company identifier")]
    public async Task WhenSheRequestsHerCapabilityMapWithoutACompanyIdentifier()
    {
        _response = await Factory.CreateClient().GetAsync("/foundation/capabilities");
    }

    [Then("the request is denied with code {string}")]
    public async Task ThenTheRequestIsDeniedWithCode(string expectedCode)
    {
        var response =
            _response
            ?? throw new InvalidOperationException("The capability request was not sent.");
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal(expectedCode, body?.Code);
    }

    [Then("the authorization denial is auditable with Ana as the actor")]
    public async Task ThenTheAuthorizationDenialIsAuditableWithAnaAsTheActor()
    {
        await using var administrator = Factory.Fixture.CreateAdministratorDbContext();
        var denial = await administrator.AuditEvents.SingleAsync(auditEvent =>
            auditEvent.Outcome == AuditOutcome.Denied
        );
        Assert.Equal(Factory.ActorUserId, denial.ActorUserId);
        Assert.NotEqual(Guid.Empty, denial.CorrelationId);
        Assert.NotEqual(Factory.CorrelationId, denial.CorrelationId);
        Assert.Null(denial.RequestedCompanyId);
    }

    [Given("Ana may assign roles in company {string}")]
    public async Task GivenAnaMayAssignRolesInCompany(string companyName)
    {
        _factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        await using var administrator = Factory.Fixture.CreateAdministratorDbContext();
        var requestId = Guid.NewGuid();
        var group = new Group(Guid.NewGuid(), "Vinx Group", Factory.ActorUserId, requestId);
        var company = new Company(
            Guid.NewGuid(),
            group.Id,
            companyName,
            Factory.ActorUserId,
            requestId
        );
        var otherCompany = new Company(
            Guid.NewGuid(),
            group.Id,
            "Vinx Serviços",
            Factory.ActorUserId,
            requestId
        );
        var target = new User(Guid.NewGuid(), "Bea", "bea@example.test");
        var actorMembership = new Membership(
            Guid.NewGuid(),
            company.Id,
            Factory.ActorUserId,
            Factory.ActorUserId,
            requestId
        );
        var targetMembership = new Membership(
            Guid.NewGuid(),
            otherCompany.Id,
            target.Id,
            Factory.ActorUserId,
            requestId
        );
        var administratorRole = new Role(
            Guid.NewGuid(),
            company.Id,
            "Foundation administrator",
            Factory.ActorUserId,
            requestId
        );
        var targetRole = new Role(
            Guid.NewGuid(),
            company.Id,
            "Operator",
            Factory.ActorUserId,
            requestId
        );
        var capability = await administrator.Capabilities.SingleAsync(candidate =>
            candidate.Name == KnownCapabilities.RolesAssign
        );
        var capabilityGrant = new RoleCapability(
            Guid.NewGuid(),
            company.Id,
            administratorRole.Id,
            capability.Id,
            CapabilityScope.Company,
            Factory.ActorUserId,
            requestId
        );
        var roleAssignment = new RoleAssignment(
            Guid.NewGuid(),
            company.Id,
            administratorRole.Id,
            actorMembership.Id,
            Factory.ActorUserId,
            requestId
        );

        administrator.AddRange(
            group,
            company,
            otherCompany,
            target,
            actorMembership,
            targetMembership,
            administratorRole,
            targetRole,
            capabilityGrant,
            roleAssignment
        );
        await administrator.SaveChangesAsync();
        _companyId = company.Id;
        _roleId = targetRole.Id;
        _targetMembershipId = targetMembership.Id;
    }

    [Given("a role belongs to company {string}")]
    public Task GivenARoleBelongsToCompany(string companyName)
    {
        Assert.Equal("Vinx Obras", companyName);
        Assert.NotEqual(Guid.Empty, _roleId);
        return Task.CompletedTask;
    }

    [Given("a membership belongs to company {string}")]
    public Task GivenAMembershipBelongsToCompany(string companyName)
    {
        Assert.Equal("Vinx Serviços", companyName);
        Assert.NotEqual(Guid.Empty, _targetMembershipId);
        return Task.CompletedTask;
    }

    [When("Ana assigns the role to the membership using company {string}")]
    public async Task WhenAnaAssignsTheRoleToTheMembershipUsingCompany(string companyName)
    {
        Assert.Equal("Vinx Obras", companyName);
        await using var scope = Factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<RoleAssignmentService>();
        _assignmentResult = await service.AssignRoleToMembershipAsync(
            new ActorContext(Factory.ActorUserId, Factory.CorrelationId),
            new AssignRoleToMembershipCommand(
                _companyId,
                _roleId,
                _targetMembershipId,
                Guid.NewGuid()
            ),
            CancellationToken.None
        );
    }

    [Then("the command is denied with code {string}")]
    public Task ThenTheCommandIsDeniedWithCode(string expectedCode)
    {
        var result =
            _assignmentResult
            ?? throw new InvalidOperationException("The role assignment command was not executed.");
        Assert.Equal(expectedCode, result.ErrorCode?.ToStableCode());
        return Task.CompletedTask;
    }

    [Then("the authorization denial contains the requested company and correlation identifier")]
    public async Task ThenTheAuthorizationDenialContainsTheRequestedCompanyAndCorrelationIdentifier()
    {
        await using var administrator = Factory.Fixture.CreateAdministratorDbContext();
        var denial = await administrator.AuditEvents.SingleAsync(auditEvent =>
            auditEvent.Outcome == AuditOutcome.Denied
        );
        Assert.Equal(_companyId, denial.RequestedCompanyId);
        Assert.Equal(Factory.CorrelationId, denial.CorrelationId);
    }

    [AfterScenario]
    public async Task DisposeFactoryAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    private FoundationApiFactory Factory =>
        _factory ?? throw new InvalidOperationException("The test host was not initialized.");

    private IReadOnlyList<MembershipResponse> MembershipMap =>
        _membershipMap
        ?? throw new InvalidOperationException("The membership map was not requested.");

    private sealed record ErrorResponse(string Code);

    private sealed record MembershipResponse(
        Guid MembershipId,
        Guid CompanyId,
        string CompanyName,
        IReadOnlyList<CapabilityResponse> Capabilities
    );

    private sealed record CapabilityResponse(string Name, IReadOnlyList<string> Scopes);
}
