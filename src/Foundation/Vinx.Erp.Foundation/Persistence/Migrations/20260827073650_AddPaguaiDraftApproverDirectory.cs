using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPaguaiDraftApproverDirectory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $block$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM foundation.capabilities
                        WHERE id = 'e3d8be82-1c79-59e7-9dd4-2f8b9c69c55a'::uuid
                          AND name = 'paguai.draft.approve') THEN
                        NULL;
                    ELSIF EXISTS (
                        SELECT 1
                        FROM foundation.capabilities
                        WHERE id = 'e3d8be82-1c79-59e7-9dd4-2f8b9c69c55a'::uuid
                           OR name = 'paguai.draft.approve') THEN
                        RAISE EXCEPTION 'Paguai draft approve capability conflicts with canonical identifier'
                            USING ERRCODE = '23505';
                    ELSE
                        INSERT INTO foundation.capabilities (id, name, created_at)
                        VALUES (
                            'e3d8be82-1c79-59e7-9dd4-2f8b9c69c55a',
                            'paguai.draft.approve',
                            clock_timestamp());
                    END IF;
                END;
                $block$;

                GRANT USAGE ON SCHEMA paguai TO vinx_worker;
                GRANT USAGE, CREATE ON SCHEMA paguai TO vinx_permissions_owner;
                SET ROLE vinx_permissions_owner;
                CREATE FUNCTION paguai.list_draft_approver_user_ids(
                    company_id uuid,
                    exclude_user_id uuid)
                RETURNS TABLE (user_id uuid)
                LANGUAGE plpgsql
                SECURITY DEFINER
                SET search_path TO pg_catalog, foundation, pg_temp
                AS $function$
                BEGIN
                    IF $1 IS NULL
                        OR $1 = '00000000-0000-0000-0000-000000000000'::uuid
                        OR $2 IS NULL
                        OR $2 = '00000000-0000-0000-0000-000000000000'::uuid THEN
                        RAISE EXCEPTION 'Paguai draft approver directory parameters are invalid'
                            USING ERRCODE = '22023';
                    END IF;

                    PERFORM set_config('app.company_id', $1::text, true);

                    RETURN QUERY
                    SELECT DISTINCT membership.user_id
                    FROM foundation.memberships AS membership
                    INNER JOIN foundation.role_assignments AS assignment
                        ON assignment.company_id = membership.company_id
                       AND assignment.membership_id = membership.id
                       AND assignment.deleted_at IS NULL
                    INNER JOIN foundation.roles AS role_record
                        ON role_record.company_id = assignment.company_id
                       AND role_record.id = assignment.role_id
                       AND role_record.deleted_at IS NULL
                    INNER JOIN foundation.role_capabilities AS grant_record
                        ON grant_record.company_id = role_record.company_id
                       AND grant_record.role_id = role_record.id
                       AND grant_record.scope = 'company'
                       AND grant_record.deleted_at IS NULL
                    INNER JOIN foundation.capabilities AS capability
                        ON capability.id = grant_record.capability_id
                       AND capability.name = 'paguai.draft.approve'
                    WHERE membership.company_id = $1
                      AND membership.user_id <> $2
                      AND membership.deleted_at IS NULL
                    ORDER BY membership.user_id;
                END;
                $function$;
                REVOKE ALL ON FUNCTION paguai.list_draft_approver_user_ids(uuid, uuid)
                    FROM PUBLIC, vinx_runtime, vinx_worker, vinx_migrator;
                GRANT EXECUTE ON FUNCTION paguai.list_draft_approver_user_ids(uuid, uuid)
                    TO vinx_worker;
                RESET ROLE;
                REVOKE CREATE ON SCHEMA paguai FROM vinx_permissions_owner;
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                -- O catálogo não registra se a linha canônica já existia. O
                -- DELETE roda em uma subtransação apenas para acionar a FK em
                -- toda referência, inclusive sob FORCE RLS; a exceção sentinela
                -- desfaz o DELETE e conserva a capability em qualquer Down seguro.
                DO $block$
                BEGIN
                    BEGIN
                        DELETE FROM foundation.capabilities
                        WHERE id = 'e3d8be82-1c79-59e7-9dd4-2f8b9c69c55a'::uuid
                          AND name = 'paguai.draft.approve';
                        RAISE EXCEPTION 'retain Paguai draft approve capability'
                            USING ERRCODE = 'P3460';
                    EXCEPTION
                        WHEN foreign_key_violation THEN
                            RAISE EXCEPTION 'cannot downgrade Paguai draft approver directory while capability references remain'
                                USING ERRCODE = '23503';
                        WHEN SQLSTATE 'P3460' THEN
                            NULL;
                    END;
                END;
                $block$;

                SET ROLE vinx_permissions_owner;
                REVOKE ALL ON FUNCTION paguai.list_draft_approver_user_ids(uuid, uuid)
                    FROM PUBLIC, vinx_runtime, vinx_worker, vinx_migrator;
                DROP FUNCTION paguai.list_draft_approver_user_ids(uuid, uuid);
                RESET ROLE;
                REVOKE USAGE ON SCHEMA paguai FROM vinx_permissions_owner;
                REVOKE USAGE ON SCHEMA paguai FROM vinx_worker;
                """
            );
        }
    }
}
