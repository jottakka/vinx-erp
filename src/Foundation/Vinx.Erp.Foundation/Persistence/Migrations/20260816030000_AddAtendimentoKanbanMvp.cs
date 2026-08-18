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
                ('f02fa2b2-aa34-50ca-99bd-b17a55d2989f', 'service_desk.ticket.read_queue', 'Consultar fila de Atendimento', clock_timestamp()),
                ('e7a53081-b6d9-5509-9300-bff9204eaa8f', 'service_desk.ticket.create', 'Criar cartão de Atendimento', clock_timestamp()),
                ('e5f8c74a-d854-57bb-916f-25b63824cab7', 'service_desk.ticket.assign', 'Atribuir responsável de Atendimento', clock_timestamp()),
                ('db9f6a71-6da5-5933-8204-6b16cad2af8a', 'service_desk.ticket.move', 'Mover cartão de Atendimento', clock_timestamp())
            ON CONFLICT (name) DO NOTHING;

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
            DO $block$
            BEGIN
                DELETE FROM foundation.capabilities
                WHERE (id, name) IN (
                    ('f02fa2b2-aa34-50ca-99bd-b17a55d2989f'::uuid, 'service_desk.ticket.read_queue'),
                    ('e7a53081-b6d9-5509-9300-bff9204eaa8f'::uuid, 'service_desk.ticket.create'),
                    ('e5f8c74a-d854-57bb-916f-25b63824cab7'::uuid, 'service_desk.ticket.assign'),
                    ('db9f6a71-6da5-5933-8204-6b16cad2af8a'::uuid, 'service_desk.ticket.move'));
            EXCEPTION
                WHEN foreign_key_violation THEN
                    RAISE EXCEPTION 'cannot downgrade service desk capability catalog while capabilities are referenced'
                        USING ERRCODE = '23503';
            END;
            $block$;
            """
        );
}
