using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Vinx.Erp.Foundation.Audit;
using Vinx.Erp.Foundation.Authorization;
using Vinx.Erp.Foundation.Domain;
using Vinx.Erp.Foundation.Employees;
using Vinx.Erp.Foundation.Identity;
using Vinx.Erp.Foundation.IntegrationTests.Infrastructure;
using Xunit;

namespace Vinx.Erp.Foundation.IntegrationTests;

public sealed class EmployeeDirectoryFailureTests
{
    [Fact]
    public async Task Audit_append_failure_returns_safe_unavailable_and_rolls_back_the_employee_graph()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var companyId = await SeedAuthorizedCompanyAsync(factory);
        await RevokeDirectoryAuditPrivilegeAsync(factory);

        using var response = await factory
            .CreateClient()
            .PostAsJsonAsync(
                $"/foundation/companies/{companyId}/employees",
                new
                {
                    requestId = Guid.NewGuid(),
                    companyId,
                    displayName = "Employee",
                    email = "employee@example.test",
                }
            );

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(
            "{\"code\":\"employee_directory_unavailable\"}",
            await response.Content.ReadAsStringAsync()
        );
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Equal(1, await administrator.Users.CountAsync());
        Assert.Equal(1, await administrator.Memberships.CountAsync());
        Assert.Empty(await administrator.Employees.ToListAsync());
        Assert.Empty(
            await administrator
                .IdempotencyRecords.Where(item => item.CommandName == "foundation.employee.create")
                .ToListAsync()
        );
        Assert.Empty(
            await administrator
                .AuditEvents.Where(item => item.Action == "foundation.employee.create")
                .ToListAsync()
        );
    }

    [Fact]
    public async Task Capability_denial_fails_closed_when_its_audit_cannot_be_written()
    {
        await using var factory = await FoundationApiFactory.CreateWithAuthenticatedIdentityAsync();
        var companyId = await SeedAuthorizedCompanyAsync(factory, grantCreate: false);
        await RevokeRuntimeAuditPrivilegeAsync(factory);
        await using var runtime = factory.Fixture.CreateRuntimeDbContext();
        var service = new EmployeeDirectoryService(
            runtime,
            new AuthorizedCompanySetResolver(
                runtime,
                NullLogger<AuthorizedCompanySetResolver>.Instance
            ),
            new AuditAppender(runtime),
            NullLogger<EmployeeDirectoryService>.Instance
        );

        var result = await service.CreateAsync(
            new ActorContext(factory.ActorUserId, Guid.NewGuid()),
            new CreateEmployeeCommand(
                Guid.NewGuid(),
                companyId,
                "Employee",
                "employee@example.test"
            ),
            CancellationToken.None
        );
        Assert.Equal(EmployeeDirectoryWriteState.Unavailable, result.State);
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        Assert.Empty(await administrator.Employees.ToListAsync());
        Assert.Empty(
            await administrator
                .AuditEvents.Where(item => item.Action == "foundation.employee.create")
                .ToListAsync()
        );
    }

    private static async Task<Guid> SeedAuthorizedCompanyAsync(
        FoundationApiFactory factory,
        bool grantCreate = true
    )
    {
        await using var administrator = factory.Fixture.CreateAdministratorDbContext();
        var requestId = Guid.NewGuid();
        var group = new Group(Guid.NewGuid(), "Group", factory.ActorUserId, requestId);
        var company = new Company(
            Guid.NewGuid(),
            group.Id,
            "Company",
            factory.ActorUserId,
            requestId
        );
        var membership = new Membership(
            Guid.NewGuid(),
            company.Id,
            factory.ActorUserId,
            factory.ActorUserId,
            requestId
        );
        var role = new Role(Guid.NewGuid(), company.Id, "Owner", factory.ActorUserId, requestId);
        administrator.AddRange(
            group,
            company,
            membership,
            role,
            new RoleAssignment(
                Guid.NewGuid(),
                company.Id,
                role.Id,
                membership.Id,
                factory.ActorUserId,
                requestId
            )
        );
        if (grantCreate)
        {
            var capability = await administrator.Capabilities.SingleAsync(item =>
                item.Name == "foundation.employees.create"
            );
            administrator.Add(
                new RoleCapability(
                    Guid.NewGuid(),
                    company.Id,
                    role.Id,
                    capability.Id,
                    CapabilityScope.Company,
                    factory.ActorUserId,
                    requestId
                )
            );
        }
        await administrator.SaveChangesAsync();
        return company.Id;
    }

    private static async Task RevokeDirectoryAuditPrivilegeAsync(FoundationApiFactory factory)
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "REVOKE EXECUTE ON FUNCTION audit.append_event(uuid, text, uuid, text, text, text, uuid, text, uuid, text, jsonb) FROM vinx_employee_directory_owner;",
            administrator
        );
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RevokeRuntimeAuditPrivilegeAsync(FoundationApiFactory factory)
    {
        await using var administrator = await factory.Fixture.OpenAdministratorConnectionAsync();
        await using var command = new NpgsqlCommand(
            "REVOKE EXECUTE ON FUNCTION audit.append_event(uuid, text, uuid, text, text, text, uuid, text, uuid, text, jsonb) FROM vinx_runtime;",
            administrator
        );
        await command.ExecuteNonQueryAsync();
    }
}
