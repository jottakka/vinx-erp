using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations;

/// <summary>
/// Fronteira de proposta viva por recurso: resource_key não vazia delimita a
/// unicidade a (empresa, ator, recurso) — demandas aprovam por demanda; o
/// legado do módulo AI (chave vazia, backfill do DEFAULT) preserva o
/// single-flight global por ator/empresa. Propostas por recurso são de
/// decisão compartilhada na empresa (quem decide é outro ator que o
/// preparador, sempre com a capability da política); o legado permanece
/// visível apenas ao próprio preparador.
/// </summary>
[DbContext(typeof(FoundationDbContext))]
[Migration("20260828091000_AddApprovalResourceKey")]
public sealed class AddApprovalResourceKey : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            ALTER TABLE foundation.approvals
                ADD COLUMN resource_key varchar(200) NOT NULL DEFAULT '';

            DROP INDEX foundation.ix_approvals_company_id_actor_user_id;

            CREATE UNIQUE INDEX ix_approvals_company_id_actor_user_id_resource_key
                ON foundation.approvals (company_id, actor_user_id, resource_key)
                WHERE consumed_at IS NULL AND deleted_at IS NULL;

            DROP POLICY approvals_company_isolation ON foundation.approvals;
            CREATE POLICY approvals_company_isolation ON foundation.approvals
                USING (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid
                    AND (actor_user_id = NULLIF(current_setting('app.user_id', true), '')::uuid
                         OR resource_key <> ''))
                WITH CHECK (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid
                    AND (actor_user_id = NULLIF(current_setting('app.user_id', true), '')::uuid
                         OR resource_key <> ''));
            """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            DROP POLICY approvals_company_isolation ON foundation.approvals;
            CREATE POLICY approvals_company_isolation ON foundation.approvals
                USING (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid
                    AND actor_user_id = NULLIF(current_setting('app.user_id', true), '')::uuid)
                WITH CHECK (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid
                    AND actor_user_id = NULLIF(current_setting('app.user_id', true), '')::uuid);

            DROP INDEX foundation.ix_approvals_company_id_actor_user_id_resource_key;

            CREATE UNIQUE INDEX ix_approvals_company_id_actor_user_id
                ON foundation.approvals (company_id, actor_user_id)
                WHERE consumed_at IS NULL AND deleted_at IS NULL;

            ALTER TABLE foundation.approvals
                DROP COLUMN resource_key;
            """);
}
