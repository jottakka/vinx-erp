using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Vinx.Erp.Foundation.Persistence;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations;

[DbContext(typeof(FoundationDbContext))]
[Migration("20260816030000_AddAtendimentoKanbanMvp")]
public sealed class AddAtendimentoKanbanMvp : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            INSERT INTO foundation.capabilities (id, name, description, created_at)
            VALUES
                ('611e4143-0a7a-49df-97af-783b28dd8974', 'service_desk.ticket.read_queue', 'Consultar fila de Atendimento', clock_timestamp()),
                ('20d15110-0862-4098-96f6-0a1931d48848', 'service_desk.ticket.create', 'Criar cartão de Atendimento', clock_timestamp()),
                ('7a4911f9-5ff2-4cad-b8e4-f907ff3159b3', 'service_desk.ticket.assign', 'Atribuir responsável de Atendimento', clock_timestamp()),
                ('86622ee5-7117-471a-90a7-1ecd90fabc18', 'service_desk.ticket.move', 'Mover cartão de Atendimento', clock_timestamp())
            ON CONFLICT (id) DO NOTHING;
            DO $block$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM (VALUES
                        ('611e4143-0a7a-49df-97af-783b28dd8974'::uuid, 'service_desk.ticket.read_queue'),
                        ('20d15110-0862-4098-96f6-0a1931d48848'::uuid, 'service_desk.ticket.create'),
                        ('7a4911f9-5ff2-4cad-b8e4-f907ff3159b3'::uuid, 'service_desk.ticket.assign'),
                        ('86622ee5-7117-471a-90a7-1ecd90fabc18'::uuid, 'service_desk.ticket.move')) AS expected(id, name)
                    LEFT JOIN foundation.capabilities AS capability ON capability.id = expected.id
                    WHERE capability.name IS DISTINCT FROM expected.name) THEN
                    RAISE EXCEPTION 'canonical service desk capability UUID is occupied by a divergent name' USING ERRCODE = '23505';
                END IF;
            END;
            $block$;

            CREATE SCHEMA service_desk;

            CREATE TABLE service_desk.atendimento_cards (
                id uuid PRIMARY KEY,
                company_id uuid NOT NULL REFERENCES foundation.companies(id) ON DELETE RESTRICT,
                title varchar(200) NOT NULL CHECK (btrim(title) <> ''),
                description varchar(4000) NOT NULL CHECK (btrim(description) <> ''),
                column_name varchar(30) NOT NULL CHECK (column_name IN ('novo', 'em_andamento', 'concluido')),
                assignee_membership_id uuid NULL,
                created_by_user_id uuid NOT NULL REFERENCES foundation.users(id) ON DELETE RESTRICT,
                updated_by_user_id uuid NOT NULL REFERENCES foundation.users(id) ON DELETE RESTRICT,
                request_id uuid NOT NULL,
                created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
                updated_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
                deleted_at timestamp with time zone NULL,
                deleted_by_user_id uuid NULL REFERENCES foundation.users(id) ON DELETE RESTRICT,
                deleted_reason varchar(500) NULL,
                row_version bigint NOT NULL DEFAULT 1,
                CONSTRAINT uq_atendimento_cards_company_id_id UNIQUE (company_id, id),
                CONSTRAINT fk_atendimento_cards_assignee_membership
                    FOREIGN KEY (company_id, assignee_membership_id)
                    REFERENCES foundation.memberships(company_id, id) ON DELETE RESTRICT,
                CONSTRAINT ck_atendimento_cards_deleted_complete CHECK (
                    (deleted_at IS NULL AND deleted_by_user_id IS NULL AND deleted_reason IS NULL)
                    OR (deleted_at IS NOT NULL AND deleted_by_user_id IS NOT NULL AND btrim(deleted_reason) <> ''))
            );
            CREATE INDEX ix_atendimento_cards_company_id_column
                ON service_desk.atendimento_cards (company_id, column_name) WHERE deleted_at IS NULL;

            CREATE TABLE service_desk.atendimento_card_history (
                id uuid PRIMARY KEY,
                company_id uuid NOT NULL,
                card_id uuid NOT NULL,
                from_column varchar(30) NOT NULL CHECK (from_column IN ('novo', 'em_andamento', 'concluido')),
                to_column varchar(30) NOT NULL CHECK (to_column IN ('novo', 'em_andamento', 'concluido')),
                actor_user_id uuid NOT NULL REFERENCES foundation.users(id) ON DELETE RESTRICT,
                request_id uuid NOT NULL,
                created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
                CONSTRAINT fk_atendimento_card_history_card
                    FOREIGN KEY (company_id, card_id)
                    REFERENCES service_desk.atendimento_cards(company_id, id) ON DELETE RESTRICT
            );
            CREATE INDEX ix_atendimento_card_history_company_id_card_id_created_at
                ON service_desk.atendimento_card_history (company_id, card_id, created_at);

            ALTER TABLE service_desk.atendimento_cards ENABLE ROW LEVEL SECURITY;
            ALTER TABLE service_desk.atendimento_cards FORCE ROW LEVEL SECURITY;
            CREATE POLICY atendimento_cards_company_isolation ON service_desk.atendimento_cards
                USING (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid)
                WITH CHECK (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);
            ALTER TABLE service_desk.atendimento_card_history ENABLE ROW LEVEL SECURITY;
            ALTER TABLE service_desk.atendimento_card_history FORCE ROW LEVEL SECURITY;
            CREATE POLICY atendimento_card_history_company_isolation ON service_desk.atendimento_card_history
                USING (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid)
                WITH CHECK (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);

            GRANT USAGE ON SCHEMA service_desk TO vinx_runtime;
            GRANT SELECT, INSERT, UPDATE ON service_desk.atendimento_cards TO vinx_runtime;
            GRANT SELECT, INSERT ON service_desk.atendimento_card_history TO vinx_runtime;
            """
        );

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            REVOKE ALL ON service_desk.atendimento_card_history, service_desk.atendimento_cards FROM vinx_runtime;
            DROP TABLE service_desk.atendimento_card_history;
            DROP TABLE service_desk.atendimento_cards;
            REVOKE USAGE ON SCHEMA service_desk FROM vinx_runtime;
            DROP SCHEMA service_desk;
            DELETE FROM foundation.capabilities
            WHERE id IN (
                '611e4143-0a7a-49df-97af-783b28dd8974',
                '20d15110-0862-4098-96f6-0a1931d48848',
                '7a4911f9-5ff2-4cad-b8e4-f907ff3159b3',
                '86622ee5-7117-471a-90a7-1ecd90fabc18');
            """
        );
}
