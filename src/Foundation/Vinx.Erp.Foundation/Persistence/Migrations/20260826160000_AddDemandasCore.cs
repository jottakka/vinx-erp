using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Vinx.Erp.Foundation.Persistence;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations;

[DbContext(typeof(FoundationDbContext))]
[Migration("20260826160000_AddDemandasCore")]
public sealed class AddDemandasCore : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            INSERT INTO foundation.capabilities (id, name, description, created_at)
            VALUES
                ('6ff8b25a-1c37-58cc-894b-97221467c3d4', 'demandas.demand.read', 'Consultar o quadro e detalhes de Demandas', clock_timestamp()),
                ('d3c10860-a26c-5eac-b684-8e2faac9ad99', 'demandas.demand.create', 'Criar demanda', clock_timestamp()),
                ('74403a29-bdae-55a9-b43b-774eca3f03b2', 'demandas.demand.advance', 'Avançar etapa e concluir itens de checklist', clock_timestamp()),
                ('00b09e31-8693-5b62-b59a-cad5994f3378', 'demandas.demand.assign', 'Trocar responsável, pausar automação e retomar demanda', clock_timestamp()),
                ('d207f7a5-7fdb-5a7d-a87c-2b4210f36712', 'demandas.demand.participants.manage', 'Gerenciar participantes e supervisor da demanda', clock_timestamp()),
                ('ec28a347-1ade-5586-bf33-e6f1cbd59620', 'demandas.routine.manage', 'Gerenciar rotinas e gatilhos do fluxo', clock_timestamp())
            ON CONFLICT (name) DO NOTHING;

            CREATE SCHEMA demandas;

            CREATE TABLE demandas.demand_routines (
                id uuid PRIMARY KEY,
                company_id uuid NOT NULL REFERENCES foundation.companies(id) ON DELETE RESTRICT,
                name varchar(120) NOT NULL CHECK (btrim(name) <> ''),
                description varchar(500) NULL,
                tipo varchar(100) NULL,
                departamento varchar(100) NULL,
                auto_assignee_membership_id uuid NULL,
                routing_order integer NOT NULL DEFAULT 100,
                default_checklist_items jsonb NOT NULL DEFAULT '[]'::jsonb,
                is_active boolean NOT NULL DEFAULT TRUE,
                created_by_user_id uuid NOT NULL REFERENCES foundation.users(id) ON DELETE RESTRICT,
                updated_by_user_id uuid NOT NULL REFERENCES foundation.users(id) ON DELETE RESTRICT,
                request_id uuid NOT NULL,
                created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
                updated_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
                deleted_at timestamp with time zone NULL,
                deleted_by_user_id uuid NULL REFERENCES foundation.users(id) ON DELETE RESTRICT,
                deleted_reason varchar(500) NULL,
                row_version bigint NOT NULL DEFAULT 1,
                CONSTRAINT uq_demand_routines_company_id_id UNIQUE (company_id, id),
                CONSTRAINT fk_demand_routines_auto_assignee
                    FOREIGN KEY (company_id, auto_assignee_membership_id)
                    REFERENCES foundation.memberships(company_id, id) ON DELETE RESTRICT,
                CONSTRAINT ck_demand_routines_deleted_complete CHECK (
                    (deleted_at IS NULL AND deleted_by_user_id IS NULL AND deleted_reason IS NULL)
                    OR (deleted_at IS NOT NULL AND deleted_by_user_id IS NOT NULL AND btrim(deleted_reason) <> '')),
                CONSTRAINT ck_demand_routines_routing_order_positive CHECK (routing_order >= 0)
            );
            CREATE INDEX ix_demand_routines_company_active_routing
                ON demandas.demand_routines (company_id, is_active, departamento, tipo, routing_order)
                WHERE deleted_at IS NULL;

            CREATE TABLE demandas.demand_routine_stages (
                id uuid PRIMARY KEY,
                company_id uuid NOT NULL,
                routine_id uuid NOT NULL,
                order_index integer NOT NULL CHECK (order_index >= 0),
                stage varchar(30) NOT NULL CHECK (stage IN ('initiation', 'execution', 'awaiting_approval')),
                trigger_kind integer NOT NULL CHECK (trigger_kind IN (1, 2, 3)),
                trigger_hours numeric(10,2) NULL,
                deadline_hours numeric(10,2) NULL,
                CONSTRAINT uq_demand_routine_stages_company_routine_id UNIQUE (company_id, id),
                CONSTRAINT uq_demand_routine_stages_company_routine_order UNIQUE (company_id, routine_id, order_index),
                CONSTRAINT uq_demand_routine_stages_company_routine_stage UNIQUE (company_id, routine_id, stage),
                CONSTRAINT fk_demand_routine_stages_routine
                    FOREIGN KEY (company_id, routine_id)
                    REFERENCES demandas.demand_routines(company_id, id) ON DELETE RESTRICT,
                CONSTRAINT ck_demand_routine_stages_time_trigger_hours CHECK (
                    (trigger_kind = 3 AND trigger_hours IS NOT NULL AND trigger_hours > 0)
                    OR (trigger_kind <> 3 AND trigger_hours IS NULL)),
                CONSTRAINT ck_demand_routine_stages_deadline_hours CHECK (
                    deadline_hours IS NULL OR deadline_hours > 0)
            );

            CREATE TABLE demandas.demands (
                id uuid PRIMARY KEY,
                company_id uuid NOT NULL REFERENCES foundation.companies(id) ON DELETE RESTRICT,
                demand_number bigint GENERATED ALWAYS AS IDENTITY,
                title varchar(200) NOT NULL CHECK (btrim(title) <> ''),
                description varchar(4000) NOT NULL,
                priority varchar(12) NOT NULL DEFAULT 'medium' CHECK (priority IN ('critical', 'high', 'medium', 'low')),
                stage varchar(30) NOT NULL DEFAULT 'initiation' CHECK (stage IN ('initiation', 'execution', 'awaiting_approval', 'closed')),
                routine_id uuid NULL,
                responsible_membership_id uuid NOT NULL,
                requester_user_id uuid NOT NULL REFERENCES foundation.users(id) ON DELETE RESTRICT,
                tipo varchar(100) NULL,
                departamento varchar(100) NULL,
                manual_due_at timestamp with time zone NULL,
                sla_due_at timestamp with time zone NULL,
                stage_due_at timestamp with time zone NULL,
                stage_started_at timestamp with time zone NULL DEFAULT clock_timestamp(),
                automation_paused boolean NOT NULL DEFAULT FALSE,
                overdue_since timestamp with time zone NULL,
                stage_cycle_count integer NOT NULL DEFAULT 1 CHECK (stage_cycle_count >= 1),
                closed_at timestamp with time zone NULL,
                created_by_user_id uuid NOT NULL REFERENCES foundation.users(id) ON DELETE RESTRICT,
                updated_by_user_id uuid NOT NULL REFERENCES foundation.users(id) ON DELETE RESTRICT,
                request_id uuid NOT NULL,
                created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
                updated_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
                deleted_at timestamp with time zone NULL,
                deleted_by_user_id uuid NULL REFERENCES foundation.users(id) ON DELETE RESTRICT,
                deleted_reason varchar(500) NULL,
                row_version bigint NOT NULL DEFAULT 1,
                CONSTRAINT uq_demands_company_id_id UNIQUE (company_id, id),
                CONSTRAINT fk_demands_routine FOREIGN KEY (company_id, routine_id)
                    REFERENCES demandas.demand_routines(company_id, id) ON DELETE RESTRICT,
                CONSTRAINT fk_demands_responsible_membership
                    FOREIGN KEY (company_id, responsible_membership_id)
                    REFERENCES foundation.memberships(company_id, id) ON DELETE RESTRICT,
                CONSTRAINT ck_demands_deleted_complete CHECK (
                    (deleted_at IS NULL AND deleted_by_user_id IS NULL AND deleted_reason IS NULL)
                    OR (deleted_at IS NOT NULL AND deleted_by_user_id IS NOT NULL AND btrim(deleted_reason) <> '')),
                CONSTRAINT ck_demands_closed_complete CHECK (
                    (closed_at IS NULL) = (stage <> 'closed'))
            );
            CREATE UNIQUE INDEX uq_demands_company_number
                ON demandas.demands (company_id, demand_number);
            CREATE INDEX ix_demands_company_stage_board
                ON demandas.demands (company_id, stage, closed_at)
                WHERE deleted_at IS NULL;
            CREATE INDEX ix_demands_company_automation_scan
                ON demandas.demands (company_id, stage_due_at, manual_due_at, sla_due_at)
                WHERE deleted_at IS NULL AND closed_at IS NULL;

            CREATE TABLE demandas.demand_participants (
                id uuid PRIMARY KEY,
                company_id uuid NOT NULL REFERENCES foundation.companies(id) ON DELETE RESTRICT,
                demand_id uuid NOT NULL,
                user_id uuid NOT NULL REFERENCES foundation.users(id) ON DELETE RESTRICT,
                role varchar(20) NOT NULL DEFAULT 'watcher' CHECK (role IN ('supervisor', 'watcher')),
                created_by_user_id uuid NOT NULL REFERENCES foundation.users(id) ON DELETE RESTRICT,
                created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
                deleted_at timestamp with time zone NULL,
                deleted_by_user_id uuid NULL REFERENCES foundation.users(id) ON DELETE RESTRICT,
                deleted_reason varchar(500) NULL,
                CONSTRAINT uq_demand_participants_company_demand_user UNIQUE (company_id, demand_id, user_id),
                CONSTRAINT fk_demand_participants_demand
                    FOREIGN KEY (company_id, demand_id)
                    REFERENCES demandas.demands(company_id, id) ON DELETE RESTRICT,
                CONSTRAINT ck_demand_participants_deleted_complete CHECK (
                    (deleted_at IS NULL AND deleted_by_user_id IS NULL AND deleted_reason IS NULL)
                    OR (deleted_at IS NOT NULL AND deleted_by_user_id IS NOT NULL AND btrim(deleted_reason) <> ''))
            );
            CREATE UNIQUE INDEX ux_demand_participants_single_supervisor
                ON demandas.demand_participants (company_id, demand_id)
                WHERE role = 'supervisor' AND deleted_at IS NULL;
            CREATE INDEX ix_demand_participants_company_user
                ON demandas.demand_participants (company_id, user_id)
                WHERE deleted_at IS NULL;

            CREATE TABLE demandas.demand_checklist_items (
                id uuid PRIMARY KEY,
                company_id uuid NOT NULL REFERENCES foundation.companies(id) ON DELETE RESTRICT,
                demand_id uuid NOT NULL,
                text varchar(200) NOT NULL CHECK (btrim(text) <> ''),
                position integer NOT NULL CHECK (position >= 0),
                is_done boolean NOT NULL DEFAULT FALSE,
                done_at timestamp with time zone NULL,
                done_by_user_id uuid NULL REFERENCES foundation.users(id) ON DELETE RESTRICT,
                CONSTRAINT uq_demand_checklist_items_company_id_id UNIQUE (company_id, id),
                CONSTRAINT fk_demand_checklist_items_demand
                    FOREIGN KEY (company_id, demand_id)
                    REFERENCES demandas.demands(company_id, id) ON DELETE RESTRICT,
                CONSTRAINT ck_demand_checklist_items_done_complete CHECK (
                    (NOT is_done AND done_at IS NULL AND done_by_user_id IS NULL)
                    OR (is_done AND done_at IS NOT NULL AND done_by_user_id IS NOT NULL))
            );
            CREATE INDEX ix_demand_checklist_items_company_demand_position
                ON demandas.demand_checklist_items (company_id, demand_id, position);

            CREATE TABLE demandas.demand_events (
                id uuid PRIMARY KEY,
                company_id uuid NOT NULL REFERENCES foundation.companies(id) ON DELETE RESTRICT,
                demand_id uuid NOT NULL,
                kind varchar(40) NOT NULL,
                from_stage varchar(30) NULL,
                to_stage varchar(30) NULL,
                cycle_count integer NOT NULL,
                occurrence_token varchar(64) NOT NULL,
                actor_user_id uuid NULL REFERENCES foundation.users(id) ON DELETE RESTRICT,
                metadata_json jsonb NOT NULL DEFAULT '{}'::jsonb,
                occurred_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
                CONSTRAINT uq_demand_events_company_id_id UNIQUE (company_id, id),
                CONSTRAINT fk_demand_events_demand
                    FOREIGN KEY (company_id, demand_id)
                    REFERENCES demandas.demands(company_id, id) ON DELETE RESTRICT,
                CONSTRAINT ck_demand_events_token_not_blank CHECK (btrim(occurrence_token) <> '')
            );
            CREATE UNIQUE INDEX uq_demand_events_trigger_key
                ON demandas.demand_events (demand_id, kind, occurrence_token);
            CREATE INDEX ix_demand_events_company_demand_occurred
                ON demandas.demand_events (company_id, demand_id, occurred_at);

            -- Isolamento por empresa com falha fechada: sem app.company_id na
            -- transação a comparação é nula e nenhuma linha fica visível ou mutável.
            ALTER TABLE demandas.demand_routines ENABLE ROW LEVEL SECURITY;
            ALTER TABLE demandas.demand_routines FORCE ROW LEVEL SECURITY;
            CREATE POLICY demand_routines_company_isolation ON demandas.demand_routines
                USING (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid)
                WITH CHECK (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);
            ALTER TABLE demandas.demand_routine_stages ENABLE ROW LEVEL SECURITY;
            ALTER TABLE demandas.demand_routine_stages FORCE ROW LEVEL SECURITY;
            CREATE POLICY demand_routine_stages_company_isolation ON demandas.demand_routine_stages
                USING (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid)
                WITH CHECK (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);
            ALTER TABLE demandas.demands ENABLE ROW LEVEL SECURITY;
            ALTER TABLE demandas.demands FORCE ROW LEVEL SECURITY;
            CREATE POLICY demands_company_isolation ON demandas.demands
                USING (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid)
                WITH CHECK (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);
            ALTER TABLE demandas.demand_participants ENABLE ROW LEVEL SECURITY;
            ALTER TABLE demandas.demand_participants FORCE ROW LEVEL SECURITY;
            CREATE POLICY demand_participants_company_isolation ON demandas.demand_participants
                USING (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid)
                WITH CHECK (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);
            ALTER TABLE demandas.demand_checklist_items ENABLE ROW LEVEL SECURITY;
            ALTER TABLE demandas.demand_checklist_items FORCE ROW LEVEL SECURITY;
            CREATE POLICY demand_checklist_items_company_isolation ON demandas.demand_checklist_items
                USING (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid)
                WITH CHECK (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);
            ALTER TABLE demandas.demand_events ENABLE ROW LEVEL SECURITY;
            ALTER TABLE demandas.demand_events FORCE ROW LEVEL SECURITY;
            CREATE POLICY demand_events_company_isolation ON demandas.demand_events
                USING (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid)
                WITH CHECK (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);

            GRANT USAGE ON SCHEMA demandas TO vinx_runtime;
            -- Catálogo legível para o dono de provisionamento assim que existir
            -- grant vivo (padrão capabilities_permissions_owner_granted_select).
            CREATE POLICY role_capabilities_demandas_owner_read
                ON foundation.role_capabilities FOR SELECT TO vinx_provisioning_owner USING (true);
            CREATE POLICY capabilities_demandas_owner_read
                ON foundation.capabilities FOR SELECT TO vinx_provisioning_owner
                USING (
                    EXISTS (
                        SELECT 1 FROM foundation.role_capabilities AS grant_record
                        WHERE grant_record.capability_id = capabilities.id
                          AND grant_record.deleted_at IS NULL
                          AND grant_record.capability_id IN (
                              '6ff8b25a-1c37-58cc-894b-97221467c3d4'::uuid,
                              'd3c10860-a26c-5eac-b684-8e2faac9ad99'::uuid,
                              '74403a29-bdae-55a9-b43b-774eca3f03b2'::uuid,
                              '00b09e31-8693-5b62-b59a-cad5994f3378'::uuid,
                              'd207f7a5-7fdb-5a7d-a87c-2b4210f36712'::uuid,
                              'ec28a347-1ade-5586-bf33-e6f1cbd59620'::uuid)));

            CREATE FUNCTION demandas.enumerate_automation_companies()
            RETURNS TABLE (company_id uuid)
            LANGUAGE sql
            STABLE
            SECURITY DEFINER
            SET search_path TO demandas, pg_catalog, foundation, pg_temp
            AS $function$
                SELECT DISTINCT candidate.company_id
                FROM demandas.demands AS candidate
                WHERE candidate.deleted_at IS NULL
                  AND candidate.closed_at IS NULL
                ORDER BY candidate.company_id;
            $function$;
            ALTER FUNCTION demandas.enumerate_automation_companies()
                OWNER TO vinx_demandas_owner;
            REVOKE ALL ON FUNCTION demandas.enumerate_automation_companies()
                FROM PUBLIC, vinx_migrator;
            GRANT EXECUTE ON FUNCTION demandas.enumerate_automation_companies()
                TO vinx_worker;

            CREATE FUNCTION demandas.demand_responsible_user(p_demand_id uuid)
            RETURNS uuid
            LANGUAGE sql
            STABLE
            SECURITY DEFINER
            SET search_path TO demandas, pg_catalog, foundation, pg_temp
            AS $function$
                SELECT responsible.user_id
                FROM demandas.demands AS demand
                INNER JOIN foundation.memberships AS responsible
                    ON responsible.company_id = demand.company_id
                   AND responsible.id = demand.responsible_membership_id
                WHERE demand.id = p_demand_id
                  AND demand.deleted_at IS NULL;
            $function$;
            ALTER FUNCTION demandas.demand_responsible_user(uuid)
                OWNER TO vinx_demandas_owner;
            REVOKE ALL ON FUNCTION demandas.demand_responsible_user(uuid)
                FROM PUBLIC, vinx_migrator;
            GRANT EXECUTE ON FUNCTION demandas.demand_responsible_user(uuid)
                TO vinx_worker;

            GRANT USAGE ON SCHEMA demandas TO vinx_worker;
            GRANT SELECT ON demandas.demand_routine_stages,
                demandas.demand_participants TO vinx_worker;
            GRANT SELECT, UPDATE ON demandas.demands TO vinx_worker;
            GRANT SELECT, INSERT ON demandas.demand_events TO vinx_worker;
            -- Geração do número sequencial exige USAGE na identidade.
            GRANT USAGE, SELECT ON SEQUENCE demandas.demands_demand_number_seq
                TO vinx_runtime;

            GRANT SELECT, INSERT, UPDATE ON demandas.demands TO vinx_runtime;
            GRANT SELECT, INSERT, UPDATE ON demandas.demand_routines TO vinx_runtime;
            GRANT SELECT, INSERT, UPDATE ON demandas.demand_routine_stages TO vinx_runtime;
            GRANT SELECT, INSERT, UPDATE ON demandas.demand_participants TO vinx_runtime;
            GRANT SELECT, INSERT, UPDATE ON demandas.demand_checklist_items TO vinx_runtime;
            -- Projeção de histórico é append-only para o runtime.
            GRANT SELECT, INSERT ON demandas.demand_events TO vinx_runtime;
            """
        );

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            DROP FUNCTION demandas.enumerate_automation_companies();
            DROP FUNCTION demandas.demand_responsible_user(uuid);
            DROP POLICY capabilities_demandas_owner_read ON foundation.capabilities;
            DROP POLICY role_capabilities_demandas_owner_read ON foundation.role_capabilities;
                        REVOKE ALL ON demandas.demand_events, demandas.demand_checklist_items,
                demandas.demand_participants, demandas.demands, demandas.demand_routine_stages,
                demandas.demand_routines FROM vinx_runtime;
            DROP TABLE demandas.demand_events;
            DROP TABLE demandas.demand_checklist_items;
            DROP TABLE demandas.demand_participants;
            DROP TABLE demandas.demands;
            DROP TABLE demandas.demand_routine_stages;
            DROP TABLE demandas.demand_routines;
            REVOKE USAGE, SELECT ON SEQUENCE demandas.demands_demand_number_seq FROM vinx_runtime;
            REVOKE ALL ON SCHEMA demandas FROM vinx_worker;
            REVOKE USAGE ON SCHEMA demandas FROM vinx_runtime;
            DROP SCHEMA demandas;
            DO $block$
            BEGIN
                DELETE FROM foundation.capabilities
                WHERE (id, name) IN (
                    ('6ff8b25a-1c37-58cc-894b-97221467c3d4'::uuid, 'demandas.demand.read'),
                    ('d3c10860-a26c-5eac-b684-8e2faac9ad99'::uuid, 'demandas.demand.create'),
                    ('74403a29-bdae-55a9-b43b-774eca3f03b2'::uuid, 'demandas.demand.advance'),
                    ('00b09e31-8693-5b62-b59a-cad5994f3378'::uuid, 'demandas.demand.assign'),
                    ('d207f7a5-7fdb-5a7d-a87c-2b4210f36712'::uuid, 'demandas.demand.participants.manage'),
                    ('ec28a347-1ade-5586-bf33-e6f1cbd59620'::uuid, 'demandas.routine.manage'));
            EXCEPTION
                WHEN foreign_key_violation THEN
                    RAISE EXCEPTION 'cannot downgrade demandas capability catalog while capabilities are referenced'
                        USING ERRCODE = '23503';
            END;
            $block$;
            """
        );
}
