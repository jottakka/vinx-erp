using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations;

public partial class AddPlatformOrganizationProvisioning : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP INDEX foundation.ix_groups_name;
            DROP INDEX foundation.ix_companies_group_id_name;
            DROP INDEX foundation.ix_users_email;
            CREATE UNIQUE INDEX ix_groups_name ON foundation.groups (lower(btrim(name))) WHERE deleted_at IS NULL;
            CREATE UNIQUE INDEX ix_companies_normalized_name ON foundation.companies (lower(btrim(name))) WHERE deleted_at IS NULL;
            CREATE INDEX ix_companies_group_id ON foundation.companies (group_id);
            CREATE UNIQUE INDEX ix_users_email ON foundation.users (lower(btrim(email)));

            GRANT USAGE, CREATE ON SCHEMA foundation TO vinx_provisioning_owner;
            GRANT INSERT ON foundation.groups, foundation.companies, foundation.users,
                foundation.memberships, foundation.roles, foundation.role_capabilities,
                foundation.role_assignments TO vinx_provisioning_owner;
            GRANT REFERENCES (id) ON foundation.groups, foundation.companies, foundation.users, foundation.memberships
                TO vinx_provisioning_owner;
            GRANT SELECT ON foundation.capabilities TO vinx_provisioning_owner;
            GRANT USAGE ON SCHEMA audit TO vinx_provisioning_owner;
            SET ROLE vinx_platform_owner;
            GRANT EXECUTE ON FUNCTION foundation.current_actor_is_platform_operator() TO vinx_provisioning_owner;
            RESET ROLE;

            SET ROLE vinx_audit_owner;
            GRANT EXECUTE ON FUNCTION audit.append_event(
                uuid, text, uuid, text, text, text, uuid, text, uuid, text, jsonb)
                TO vinx_provisioning_owner;
            RESET ROLE;

            CREATE POLICY groups_provisioning_owner ON foundation.groups FOR INSERT TO vinx_provisioning_owner WITH CHECK (true);
            CREATE POLICY companies_provisioning_owner ON foundation.companies FOR INSERT TO vinx_provisioning_owner WITH CHECK (true);
            CREATE POLICY memberships_provisioning_owner ON foundation.memberships FOR INSERT TO vinx_provisioning_owner WITH CHECK (true);
            CREATE POLICY roles_provisioning_owner ON foundation.roles FOR INSERT TO vinx_provisioning_owner WITH CHECK (true);
            CREATE POLICY role_capabilities_provisioning_owner ON foundation.role_capabilities FOR INSERT TO vinx_provisioning_owner WITH CHECK (true);
            CREATE POLICY role_assignments_provisioning_owner ON foundation.role_assignments FOR INSERT TO vinx_provisioning_owner WITH CHECK (true);
            ALTER TABLE foundation.capabilities ENABLE ROW LEVEL SECURITY;
            CREATE POLICY capabilities_runtime_read ON foundation.capabilities FOR SELECT TO vinx_runtime USING (true);
            CREATE POLICY capabilities_provisioning_owner ON foundation.capabilities FOR SELECT TO vinx_provisioning_owner
                USING (name IN ('foundation.roles.assign', 'foundation.memberships.revoke', 'foundation.files.upload', 'foundation.files.confirm'));

            SET ROLE vinx_provisioning_owner;
            CREATE TABLE foundation.pending_owner_invitations (
                id uuid PRIMARY KEY,
                user_id uuid NOT NULL REFERENCES foundation.users(id) ON DELETE RESTRICT,
                company_id uuid NOT NULL REFERENCES foundation.companies(id) ON DELETE RESTRICT,
                status varchar(30) NOT NULL CHECK (status = 'pending_delivery'),
                created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
                CONSTRAINT uq_pending_owner_invitations_user_id UNIQUE (user_id)
            );
            CREATE TABLE foundation.organization_provisioning_receipts (
                id uuid PRIMARY KEY,
                actor_user_id uuid NOT NULL REFERENCES foundation.users(id) ON DELETE RESTRICT,
                request_id uuid NOT NULL,
                payload_hash varchar(64) NOT NULL CHECK (payload_hash ~ '^[0-9a-f]{64}$'),
                group_id uuid NOT NULL REFERENCES foundation.groups(id) ON DELETE RESTRICT,
                company_id uuid NOT NULL REFERENCES foundation.companies(id) ON DELETE RESTRICT,
                owner_user_id uuid NOT NULL REFERENCES foundation.users(id) ON DELETE RESTRICT,
                owner_membership_id uuid NOT NULL REFERENCES foundation.memberships(id) ON DELETE RESTRICT,
                owner_invitation_id uuid NOT NULL REFERENCES foundation.pending_owner_invitations(id) ON DELETE RESTRICT,
                created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
                CONSTRAINT uq_organization_provisioning_receipts_actor_request UNIQUE (actor_user_id, request_id)
            );
            ALTER TABLE foundation.pending_owner_invitations ENABLE ROW LEVEL SECURITY;
            ALTER TABLE foundation.pending_owner_invitations FORCE ROW LEVEL SECURITY;
            ALTER TABLE foundation.organization_provisioning_receipts ENABLE ROW LEVEL SECURITY;
            ALTER TABLE foundation.organization_provisioning_receipts FORCE ROW LEVEL SECURITY;
            CREATE POLICY pending_owner_invitations_provisioning_owner ON foundation.pending_owner_invitations
                FOR INSERT TO vinx_provisioning_owner WITH CHECK (true);
            CREATE POLICY organization_provisioning_receipts_provisioning_owner_select ON foundation.organization_provisioning_receipts
                FOR SELECT TO vinx_provisioning_owner USING (true);
            CREATE POLICY organization_provisioning_receipts_provisioning_owner_insert ON foundation.organization_provisioning_receipts
                FOR INSERT TO vinx_provisioning_owner WITH CHECK (true);

            CREATE FUNCTION foundation.provision_platform_organization(
                p_request_id uuid, p_group_name text, p_company_name text,
                p_owner_display_name text, p_owner_email text, p_payload_hash text)
            RETURNS TABLE (status text, group_id uuid, company_id uuid, owner_user_id uuid, owner_membership_id uuid, owner_invitation_id uuid)
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path TO pg_catalog, foundation, pg_temp
            AS $function$
            DECLARE
                v_actor_user_id uuid := NULLIF(current_setting('app.user_id', true), '')::uuid;
                v_correlation_id uuid := COALESCE(NULLIF(current_setting('app.correlation_id', true), '')::uuid, gen_random_uuid());
                v_group_id uuid := gen_random_uuid();
                v_company_id uuid := gen_random_uuid();
                v_owner_user_id uuid := gen_random_uuid();
                v_membership_id uuid := gen_random_uuid();
                v_role_id uuid := gen_random_uuid();
                v_invitation_id uuid := gen_random_uuid();
                v_existing foundation.organization_provisioning_receipts%ROWTYPE;
                v_capability_count integer;
                v_now timestamp with time zone := clock_timestamp();
            BEGIN
                IF v_actor_user_id IS NULL OR NOT foundation.current_actor_is_platform_operator() THEN
                    RAISE EXCEPTION 'platform operator is required' USING ERRCODE = '42501';
                END IF;
                IF current_setting('app.mfa_assurance', true) IS DISTINCT FROM 'aal2' THEN
                    RAISE EXCEPTION 'mfa assurance is required' USING ERRCODE = '42501';
                END IF;
                IF p_request_id IS NULL OR p_payload_hash !~ '^[0-9a-f]{64}$'
                    OR p_group_name IS NULL OR btrim(p_group_name) = '' OR char_length(btrim(p_group_name)) > 200
                    OR p_company_name IS NULL OR btrim(p_company_name) = '' OR char_length(btrim(p_company_name)) > 200
                    OR p_owner_display_name IS NULL OR btrim(p_owner_display_name) = '' OR char_length(btrim(p_owner_display_name)) > 200
                    OR p_owner_email IS NULL OR btrim(p_owner_email) = '' OR char_length(btrim(p_owner_email)) > 320 THEN
                    RAISE EXCEPTION 'organization provisioning parameters are invalid' USING ERRCODE = '22023';
                END IF;

                PERFORM pg_advisory_xact_lock(hashtextextended(v_actor_user_id::text || '|' || p_request_id::text, 0));
                SELECT * INTO v_existing FROM foundation.organization_provisioning_receipts
                WHERE actor_user_id = v_actor_user_id AND request_id = p_request_id;
                IF FOUND THEN
                    IF v_existing.payload_hash = p_payload_hash THEN
                        RETURN QUERY SELECT 'replay'::text, v_existing.group_id, v_existing.company_id, v_existing.owner_user_id, v_existing.owner_membership_id, v_existing.owner_invitation_id;
                    ELSE
                        RETURN QUERY SELECT 'idempotency_conflict'::text, v_existing.group_id, v_existing.company_id, v_existing.owner_user_id, v_existing.owner_membership_id, v_existing.owner_invitation_id;
                    END IF;
                    RETURN;
                END IF;

                INSERT INTO foundation.groups (id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
                VALUES (v_group_id, btrim(p_group_name), v_actor_user_id, v_actor_user_id, p_request_id, v_now, v_now);
                INSERT INTO foundation.companies (id, group_id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
                VALUES (v_company_id, v_group_id, btrim(p_company_name), v_actor_user_id, v_actor_user_id, p_request_id, v_now, v_now);
                INSERT INTO foundation.users (id, display_name, email, created_at)
                VALUES (v_owner_user_id, btrim(p_owner_display_name), lower(btrim(p_owner_email)), v_now);
                INSERT INTO foundation.memberships (id, company_id, user_id, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
                VALUES (v_membership_id, v_company_id, v_owner_user_id, v_actor_user_id, v_actor_user_id, p_request_id, v_now, v_now);
                INSERT INTO foundation.roles (id, company_id, name, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
                VALUES (v_role_id, v_company_id, 'owner', v_actor_user_id, v_actor_user_id, p_request_id, v_now, v_now);
                INSERT INTO foundation.role_capabilities (id, company_id, role_id, capability_id, scope, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
                SELECT gen_random_uuid(), v_company_id, v_role_id, capability.id, 'company', v_actor_user_id, v_actor_user_id, p_request_id, v_now, v_now
                FROM foundation.capabilities AS capability
                WHERE capability.name IN ('foundation.roles.assign', 'foundation.memberships.revoke', 'foundation.files.upload', 'foundation.files.confirm');
                GET DIAGNOSTICS v_capability_count = ROW_COUNT;
                IF v_capability_count <> 4 THEN
                    RAISE EXCEPTION 'owner capability catalog is incomplete' USING ERRCODE = '23514';
                END IF;
                INSERT INTO foundation.role_assignments (id, company_id, role_id, membership_id, created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
                VALUES (gen_random_uuid(), v_company_id, v_role_id, v_membership_id, v_actor_user_id, v_actor_user_id, p_request_id, v_now, v_now);
                INSERT INTO foundation.pending_owner_invitations (id, user_id, company_id, status)
                VALUES (v_invitation_id, v_owner_user_id, v_company_id, 'pending_delivery');
                INSERT INTO foundation.organization_provisioning_receipts (id, actor_user_id, request_id, payload_hash, group_id, company_id, owner_user_id, owner_membership_id, owner_invitation_id)
                VALUES (gen_random_uuid(), v_actor_user_id, p_request_id, p_payload_hash, v_group_id, v_company_id, v_owner_user_id, v_membership_id, v_invitation_id);
                PERFORM audit.append_event(v_actor_user_id, 'global', NULL, 'web', 'platform.organization.provision', 'organization', v_company_id, 'success', v_correlation_id, NULL, '{}'::jsonb);
                RETURN QUERY SELECT 'created'::text, v_group_id, v_company_id, v_owner_user_id, v_membership_id, v_invitation_id;
            END;
            $function$;
            REVOKE ALL ON FUNCTION foundation.provision_platform_organization(uuid, text, text, text, text, text) FROM PUBLIC, vinx_migrator;
            GRANT EXECUTE ON FUNCTION foundation.provision_platform_organization(uuid, text, text, text, text, text) TO vinx_runtime;
            RESET ROLE;
            REVOKE CREATE ON SCHEMA foundation FROM vinx_provisioning_owner;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""
            SET ROLE vinx_provisioning_owner;
            DO $block$
            BEGIN
                IF EXISTS (SELECT 1 FROM foundation.organization_provisioning_receipts)
                    OR EXISTS (SELECT 1 FROM foundation.pending_owner_invitations) THEN
                    RAISE EXCEPTION 'cannot downgrade platform organization provisioning while state exists' USING ERRCODE = '23514';
                END IF;
            END;
            $block$;
            REVOKE ALL ON FUNCTION foundation.provision_platform_organization(uuid, text, text, text, text, text) FROM PUBLIC, vinx_runtime, vinx_migrator;
            DROP FUNCTION foundation.provision_platform_organization(uuid, text, text, text, text, text);
            DROP TABLE foundation.organization_provisioning_receipts;
            DROP TABLE foundation.pending_owner_invitations;
            RESET ROLE;
            DROP POLICY role_assignments_provisioning_owner ON foundation.role_assignments;
            DROP POLICY role_capabilities_provisioning_owner ON foundation.role_capabilities;
            DROP POLICY roles_provisioning_owner ON foundation.roles;
            DROP POLICY memberships_provisioning_owner ON foundation.memberships;
            DROP POLICY companies_provisioning_owner ON foundation.companies;
            DROP POLICY groups_provisioning_owner ON foundation.groups;
            DROP POLICY capabilities_provisioning_owner ON foundation.capabilities;
            DROP POLICY capabilities_runtime_read ON foundation.capabilities;
            ALTER TABLE foundation.capabilities DISABLE ROW LEVEL SECURITY;
            SET ROLE vinx_audit_owner;
            REVOKE EXECUTE ON FUNCTION audit.append_event(uuid, text, uuid, text, text, text, uuid, text, uuid, text, jsonb) FROM vinx_provisioning_owner;
            RESET ROLE;
            SET ROLE vinx_platform_owner;
            REVOKE EXECUTE ON FUNCTION foundation.current_actor_is_platform_operator() FROM vinx_provisioning_owner;
            RESET ROLE;
            REVOKE INSERT ON foundation.groups, foundation.companies, foundation.users, foundation.memberships, foundation.roles, foundation.role_capabilities, foundation.role_assignments FROM vinx_provisioning_owner;
            REVOKE REFERENCES (id) ON foundation.groups, foundation.companies, foundation.users, foundation.memberships FROM vinx_provisioning_owner;
            REVOKE SELECT ON foundation.capabilities FROM vinx_provisioning_owner;
            REVOKE USAGE ON SCHEMA audit, foundation FROM vinx_provisioning_owner;
            DROP INDEX foundation.ix_groups_name;
            DROP INDEX foundation.ix_companies_normalized_name;
            DROP INDEX foundation.ix_companies_group_id;
            DROP INDEX foundation.ix_users_email;
            CREATE UNIQUE INDEX ix_groups_name ON foundation.groups (name) WHERE deleted_at IS NULL;
            CREATE UNIQUE INDEX ix_companies_group_id_name ON foundation.companies (group_id, name) WHERE deleted_at IS NULL;
            CREATE UNIQUE INDEX ix_users_email ON foundation.users (email);
            """);
}
