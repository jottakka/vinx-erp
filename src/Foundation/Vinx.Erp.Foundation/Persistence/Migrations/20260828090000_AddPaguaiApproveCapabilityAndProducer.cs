using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations;

/// <inheritdoc />
public partial class AddPaguaiApproveCapabilityAndProducer : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            -- Capability canônica (UUIDv5 estável, espelhada em KnownCapabilities);
            -- conflito de nome é idempotente, colisão de id falha a migração.
            INSERT INTO foundation.capabilities (id, name, created_at)
            VALUES ('e3d8be82-1c79-59e7-9dd4-2f8b9c69c55a', 'paguai.draft.approve', clock_timestamp())
            ON CONFLICT (name) DO NOTHING;

            -- O catálogo visível ao dono do provisionamento passa a incluir a nova
            -- capability; sem isso, ensure_company_default_roles rodando como
            -- vinx_permissions_owner NÃO a vê num banco vazio (item 4 da revisão).
            DROP POLICY capabilities_permissions_owner_select ON foundation.capabilities;
            CREATE POLICY capabilities_permissions_owner_select
                ON foundation.capabilities FOR SELECT TO vinx_permissions_owner
                USING (name IN ('foundation.roles.assign', 'foundation.employees.create',
                    'foundation.files.upload', 'foundation.files.confirm',
                    'paguai.draft.approve'));

            -- Papel admin padrão passa a carregar a nova capability: o padrão
            -- estabelecido é conceder ações privilegiadas de módulo ao admin
            -- (como foundation.files.confirm), nunca ao member. A função já é
            -- de vinx_permissions_owner; a reescrita acontece DENTRO do papel
            -- (transient CREATE + SET ROLE), no padrão das migrações irmãs.
            GRANT CREATE ON SCHEMA foundation TO vinx_permissions_owner;
            SET ROLE vinx_permissions_owner;
            CREATE OR REPLACE FUNCTION foundation.ensure_company_default_roles(p_company_id uuid)
            RETURNS void
            LANGUAGE plpgsql
            SECURITY DEFINER
            SET search_path TO pg_catalog, foundation, pg_temp
            AS $function$
            DECLARE
                v_seed_actor uuid;
                v_role_id uuid;
                v_grant_count integer;
            BEGIN
                IF p_company_id IS NULL THEN
                    RAISE EXCEPTION 'company default roles parameters are invalid'
                        USING ERRCODE = '22023';
                END IF;

                PERFORM set_config('app.company_id', p_company_id::text, true);

                SELECT membership.user_id
                INTO v_seed_actor
                FROM foundation.memberships AS membership
                WHERE membership.company_id = p_company_id
                  AND membership.deleted_at IS NULL
                ORDER BY membership.created_at, membership.id
                LIMIT 1;
                IF v_seed_actor IS NULL THEN
                    RAISE EXCEPTION 'company has no active membership'
                        USING ERRCODE = '23514';
                END IF;

                IF NOT EXISTS (
                    SELECT 1
                    FROM foundation.roles AS role_record
                    WHERE role_record.company_id = p_company_id
                      AND role_record.name = 'admin'
                      AND role_record.deleted_at IS NULL) THEN
                    v_role_id := gen_random_uuid();
                    INSERT INTO foundation.roles (
                        id, company_id, name, created_by_user_id, updated_by_user_id,
                        request_id, created_at, updated_at)
                    VALUES (
                        v_role_id, p_company_id, 'admin', v_seed_actor, v_seed_actor,
                        gen_random_uuid(), clock_timestamp(), clock_timestamp());
                    INSERT INTO foundation.role_capabilities (
                        id, company_id, role_id, capability_id, scope,
                        created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
                    SELECT
                        gen_random_uuid(),
                        p_company_id,
                        v_role_id,
                        capability.id,
                        'company',
                        v_seed_actor,
                        v_seed_actor,
                        gen_random_uuid(),
                        clock_timestamp(),
                        clock_timestamp()
                    FROM foundation.capabilities AS capability
                    WHERE capability.name IN (
                        'foundation.roles.assign',
                        'foundation.employees.create',
                        'foundation.files.upload',
                        'foundation.files.confirm',
                        'paguai.draft.approve')
                    ON CONFLICT (company_id, role_id, capability_id, scope) WHERE deleted_at IS NULL
                        DO NOTHING;
                    GET DIAGNOSTICS v_grant_count = ROW_COUNT;
                    IF v_grant_count <> 5 THEN
                        RAISE EXCEPTION 'admin default role capability catalog is incomplete'
                            USING ERRCODE = '23514';
                    END IF;
                END IF;

                IF NOT EXISTS (
                    SELECT 1
                    FROM foundation.roles AS role_record
                    WHERE role_record.company_id = p_company_id
                      AND role_record.name = 'member'
                      AND role_record.deleted_at IS NULL) THEN
                    v_role_id := gen_random_uuid();
                    INSERT INTO foundation.roles (
                        id, company_id, name, created_by_user_id, updated_by_user_id,
                        request_id, created_at, updated_at)
                    VALUES (
                        v_role_id, p_company_id, 'member', v_seed_actor, v_seed_actor,
                        gen_random_uuid(), clock_timestamp(), clock_timestamp());
                    INSERT INTO foundation.role_capabilities (
                        id, company_id, role_id, capability_id, scope,
                        created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
                    SELECT
                        gen_random_uuid(),
                        p_company_id,
                        v_role_id,
                        capability.id,
                        'company',
                        v_seed_actor,
                        v_seed_actor,
                        gen_random_uuid(),
                        clock_timestamp(),
                        clock_timestamp()
                    FROM foundation.capabilities AS capability
                    WHERE capability.name IN ('foundation.files.upload')
                    ON CONFLICT (company_id, role_id, capability_id, scope) WHERE deleted_at IS NULL
                        DO NOTHING;
                    GET DIAGNOSTICS v_grant_count = ROW_COUNT;
                    IF v_grant_count <> 1 THEN
                        RAISE EXCEPTION 'member default role capability catalog is incomplete'
                            USING ERRCODE = '23514';
                    END IF;
                END IF;
            END;
            $function$;
            RESET ROLE;
            REVOKE CREATE ON SCHEMA foundation FROM vinx_permissions_owner;
            REVOKE ALL ON FUNCTION foundation.ensure_company_default_roles(uuid)
                FROM PUBLIC, vinx_migrator;
            GRANT EXECUTE ON FUNCTION foundation.ensure_company_default_roles(uuid)
                TO vinx_runtime;

            -- Backfill: empresas com admin existente recebem o novo grant
            -- (executado como migrator, bypass de RLS, como na migração original).
            DO $block$
            DECLARE
                company_record record;
            BEGIN
                FOR company_record IN
                    SELECT company.id
                    FROM foundation.companies AS company
                    INNER JOIN foundation.memberships AS membership
                        ON membership.company_id = company.id
                       AND membership.deleted_at IS NULL
                    WHERE company.deleted_at IS NULL
                    GROUP BY company.id
                LOOP
                    INSERT INTO foundation.role_capabilities (
                        id, company_id, role_id, capability_id, scope,
                        created_by_user_id, updated_by_user_id, request_id,
                        created_at, updated_at)
                    SELECT
                        gen_random_uuid(),
                        company_record.id,
                        role_record.id,
                        capability.id,
                        'company',
                        role_record.created_by_user_id,
                        role_record.created_by_user_id,
                        gen_random_uuid(),
                        clock_timestamp(),
                        clock_timestamp()
                    FROM foundation.roles AS role_record
                    CROSS JOIN foundation.capabilities AS capability
                    WHERE role_record.company_id = company_record.id
                      AND role_record.name = 'admin'
                      AND role_record.deleted_at IS NULL
                      AND capability.name = 'paguai.draft.approve'
                      AND NOT EXISTS (
                          SELECT 1
                          FROM foundation.role_capabilities AS existing
                          WHERE existing.company_id = role_record.company_id
                            AND existing.role_id = role_record.id
                            AND existing.capability_id = capability.id
                            AND existing.scope = 'company'
                            AND existing.deleted_at IS NULL)
                    ON CONFLICT (company_id, role_id, capability_id, scope) WHERE deleted_at IS NULL
                        DO NOTHING;
                END LOOP;
            END;
            $block$;

            -- Diretório de destinatários na fila: resolução só na entrega. O
            -- DEFINER é vinx_permissions_owner (dá vazão às policies _owner_select
            -- existentes + role_assignments acima) e o contexto de empresa é fixado
            -- INTERNAMENTE pela própria função — sem isso o FORCE RLS devolve zero
            -- linhas quando o chamador veio de outra empresa ou sem contexto.
            GRANT CREATE ON SCHEMA paguai TO vinx_permissions_owner;
            SET ROLE vinx_permissions_owner;
            CREATE FUNCTION paguai.list_draft_approver_user_ids(
                p_company_id uuid,
                p_exclude_user_id uuid)
            RETURNS TABLE (user_id uuid)
            LANGUAGE plpgsql STABLE SECURITY DEFINER
            SET search_path TO pg_catalog, foundation, pg_temp
            AS $function$
            BEGIN
                IF p_company_id IS NULL OR p_exclude_user_id IS NULL THEN
                    RAISE EXCEPTION 'paguai approver parameters are invalid' USING ERRCODE = '22023';
                END IF;

                PERFORM set_config('app.company_id', p_company_id::text, true);

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
                WHERE membership.company_id = p_company_id
                  AND membership.deleted_at IS NULL
                  AND membership.user_id <> p_exclude_user_id;
            END;
            $function$;
            RESET ROLE;
            REVOKE CREATE ON SCHEMA paguai FROM vinx_permissions_owner;
            REVOKE ALL ON FUNCTION paguai.list_draft_approver_user_ids(uuid, uuid)
                FROM PUBLIC, vinx_migrator;
            GRANT EXECUTE ON FUNCTION paguai.list_draft_approver_user_ids(uuid, uuid)
                TO vinx_worker;

            -- O handler do worker enfileira intents na mesma conexão do worker.
            GRANT EXECUTE ON FUNCTION notifications.enqueue_notification_intent(
                uuid, uuid, text, text, text, text)
                TO vinx_worker;
            """
        );

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            SET LOCAL lock_timeout = '3s';

            DO $block$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM foundation.role_capabilities AS grant_record
                    INNER JOIN foundation.capabilities AS capability
                        ON capability.id = grant_record.capability_id
                    WHERE capability.name = 'paguai.draft.approve')
                THEN
                    RAISE EXCEPTION 'cannot downgrade paguai.draft.approve while any grant reference exists'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $block$;

            -- Revoke escopado aos objetos deste produtor.
            REVOKE EXECUTE ON FUNCTION notifications.enqueue_notification_intent(
                uuid, uuid, text, text, text, text)
                FROM vinx_worker;
            DROP FUNCTION paguai.list_draft_approver_user_ids(uuid, uuid);
            DELETE FROM foundation.capabilities
            WHERE (id, name) IN (
                    ('e3d8be82-1c79-59e7-9dd4-2f8b9c69c55a'::uuid, 'paguai.draft.approve'));

            -- Catálogo do provisionador volta ao corte anterior.
            CREATE POLICY capabilities_permissions_owner_select
                ON foundation.capabilities FOR SELECT TO vinx_permissions_owner
                USING (name IN ('foundation.roles.assign', 'foundation.employees.create',
                    'foundation.files.upload', 'foundation.files.confirm'));

            -- Corpo do provisionador restaura o estado PRÉ-migração (catálogo de
            -- 4 capabilities), no mesmo padrão transiente de troca de papel —
            -- provisionamento é reversível até o último estado de provisioning.
            GRANT CREATE ON SCHEMA foundation TO vinx_permissions_owner;
            SET ROLE vinx_permissions_owner;
            CREATE OR REPLACE FUNCTION foundation.ensure_company_default_roles(p_company_id uuid)
            RETURNS void
            LANGUAGE plpgsql
            SECURITY DEFINER
            SET search_path TO pg_catalog, foundation, pg_temp
            AS $function$
            DECLARE
                v_seed_actor uuid;
                v_role_id uuid;
                v_grant_count integer;
            BEGIN
                IF p_company_id IS NULL THEN
                    RAISE EXCEPTION 'company default roles parameters are invalid'
                        USING ERRCODE = '22023';
                END IF;

                PERFORM set_config('app.company_id', p_company_id::text, true);

                SELECT membership.user_id
                INTO v_seed_actor
                FROM foundation.memberships AS membership
                WHERE membership.company_id = p_company_id
                  AND membership.deleted_at IS NULL
                ORDER BY membership.created_at, membership.id
                LIMIT 1;
                IF v_seed_actor IS NULL THEN
                    RAISE EXCEPTION 'company has no active membership'
                        USING ERRCODE = '23514';
                END IF;

                IF NOT EXISTS (
                    SELECT 1
                    FROM foundation.roles AS role_record
                    WHERE role_record.company_id = p_company_id
                      AND role_record.name = 'admin'
                      AND role_record.deleted_at IS NULL) THEN
                    v_role_id := gen_random_uuid();
                    INSERT INTO foundation.roles (
                        id, company_id, name, created_by_user_id, updated_by_user_id,
                        request_id, created_at, updated_at)
                    VALUES (
                        v_role_id, p_company_id, 'admin', v_seed_actor, v_seed_actor,
                        gen_random_uuid(), clock_timestamp(), clock_timestamp());
                    INSERT INTO foundation.role_capabilities (
                        id, company_id, role_id, capability_id, scope,
                        created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
                    SELECT
                        gen_random_uuid(),
                        p_company_id,
                        v_role_id,
                        capability.id,
                        'company',
                        v_seed_actor,
                        v_seed_actor,
                        gen_random_uuid(),
                        clock_timestamp(),
                        clock_timestamp()
                    FROM foundation.capabilities AS capability
                    WHERE capability.name IN (
                        'foundation.roles.assign',
                        'foundation.employees.create',
                        'foundation.files.upload',
                        'foundation.files.confirm')
                    ON CONFLICT (company_id, role_id, capability_id, scope) WHERE deleted_at IS NULL
                        DO NOTHING;
                    GET DIAGNOSTICS v_grant_count = ROW_COUNT;
                    IF v_grant_count <> 4 THEN
                        RAISE EXCEPTION 'admin default role capability catalog is incomplete'
                            USING ERRCODE = '23514';
                    END IF;
                END IF;

                IF NOT EXISTS (
                    SELECT 1
                    FROM foundation.roles AS role_record
                    WHERE role_record.company_id = p_company_id
                      AND role_record.name = 'member'
                      AND role_record.deleted_at IS NULL) THEN
                    v_role_id := gen_random_uuid();
                    INSERT INTO foundation.roles (
                        id, company_id, name, created_by_user_id, updated_by_user_id,
                        request_id, created_at, updated_at)
                    VALUES (
                        v_role_id, p_company_id, 'member', v_seed_actor, v_seed_actor,
                        gen_random_uuid(), clock_timestamp(), clock_timestamp());
                    INSERT INTO foundation.role_capabilities (
                        id, company_id, role_id, capability_id, scope,
                        created_by_user_id, updated_by_user_id, request_id, created_at, updated_at)
                    SELECT
                        gen_random_uuid(),
                        p_company_id,
                        v_role_id,
                        capability.id,
                        'company',
                        v_seed_actor,
                        v_seed_actor,
                        gen_random_uuid(),
                        clock_timestamp(),
                        clock_timestamp()
                    FROM foundation.capabilities AS capability
                    WHERE capability.name IN ('foundation.files.upload')
                    ON CONFLICT (company_id, role_id, capability_id, scope) WHERE deleted_at IS NULL
                        DO NOTHING;
                    GET DIAGNOSTICS v_grant_count = ROW_COUNT;
                    IF v_grant_count <> 1 THEN
                        RAISE EXCEPTION 'member default role capability catalog is incomplete'
                            USING ERRCODE = '23514';
                    END IF;
                END IF;
            END;
            $function$;
            RESET ROLE;
            REVOKE CREATE ON SCHEMA foundation FROM vinx_permissions_owner;
            REVOKE ALL ON FUNCTION foundation.ensure_company_default_roles(uuid)
                FROM PUBLIC, vinx_migrator;
            GRANT EXECUTE ON FUNCTION foundation.ensure_company_default_roles(uuid)
                TO vinx_runtime;
            """
        );
}
