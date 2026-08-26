using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAtendimentoCancellationParticipantsAndAttachment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(
                """
                ALTER TABLE service_desk.atendimento_cards
                    ADD COLUMN attachment_file_id uuid NULL,
                    ADD COLUMN cancelled_at timestamp with time zone NULL,
                    ADD COLUMN cancelled_by_user_id uuid NULL,
                    ADD COLUMN cancellation_reason varchar(500) NULL;

                ALTER TABLE service_desk.atendimento_cards
                    DROP CONSTRAINT atendimento_cards_column_name_check,
                    ADD CONSTRAINT atendimento_cards_column_name_check
                        CHECK (column_name IN ('novo', 'em_andamento', 'concluido', 'cancelado')),
                    ADD CONSTRAINT ck_atendimento_cards_cancellation_complete CHECK (
                        (column_name = 'cancelado'
                            AND cancelled_at IS NOT NULL
                            AND cancelled_by_user_id IS NOT NULL
                            AND cancellation_reason IS NOT NULL
                            AND btrim(cancellation_reason) <> '')
                        OR (column_name <> 'cancelado'
                            AND cancelled_at IS NULL
                            AND cancelled_by_user_id IS NULL
                            AND cancellation_reason IS NULL));

                ALTER TABLE service_desk.atendimento_card_history
                    DROP CONSTRAINT atendimento_card_history_from_column_check,
                    DROP CONSTRAINT atendimento_card_history_to_column_check,
                    ADD CONSTRAINT atendimento_card_history_from_column_check
                        CHECK (from_column IN ('novo', 'em_andamento', 'concluido', 'cancelado')),
                    ADD CONSTRAINT atendimento_card_history_to_column_check
                        CHECK (to_column IN ('novo', 'em_andamento', 'concluido', 'cancelado'));

                ALTER TABLE service_desk.atendimento_cards
                    ADD CONSTRAINT fk_atendimento_cards_files_company_id_attachment_file_id
                        FOREIGN KEY (company_id, attachment_file_id)
                        REFERENCES foundation.files(company_id, id) ON DELETE RESTRICT,
                    ADD CONSTRAINT fk_atendimento_cards_users_cancelled_by_user_id
                        FOREIGN KEY (cancelled_by_user_id)
                        REFERENCES foundation.users(id) ON DELETE RESTRICT;
                CREATE INDEX ix_atendimento_cards_company_id_attachment_file_id
                    ON service_desk.atendimento_cards(company_id, attachment_file_id);
                CREATE INDEX ix_atendimento_cards_cancelled_by_user_id
                    ON service_desk.atendimento_cards(cancelled_by_user_id);

                CREATE TABLE service_desk.atendimento_card_participants (
                    company_id uuid NOT NULL,
                    card_id uuid NOT NULL,
                    membership_id uuid NOT NULL,
                    CONSTRAINT pk_atendimento_card_participants
                        PRIMARY KEY (company_id, card_id, membership_id),
                    CONSTRAINT fk_atendimento_card_participants_atendimento_cards_company_id_
                        FOREIGN KEY (company_id, card_id)
                        REFERENCES service_desk.atendimento_cards(company_id, id) ON DELETE RESTRICT,
                    CONSTRAINT fk_atendimento_card_participants_memberships_company_id_member
                        FOREIGN KEY (company_id, membership_id)
                        REFERENCES foundation.memberships(company_id, id) ON DELETE RESTRICT
                );
                CREATE INDEX ix_atendimento_card_participants_company_id_membership_id
                    ON service_desk.atendimento_card_participants(company_id, membership_id);
                ALTER TABLE service_desk.atendimento_card_participants ENABLE ROW LEVEL SECURITY;
                ALTER TABLE service_desk.atendimento_card_participants FORCE ROW LEVEL SECURITY;
                CREATE POLICY atendimento_card_participants_company_isolation
                    ON service_desk.atendimento_card_participants
                    USING (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid)
                    WITH CHECK (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);
                REVOKE ALL ON service_desk.atendimento_card_participants FROM PUBLIC, vinx_runtime;
                GRANT SELECT, INSERT ON service_desk.atendimento_card_participants TO vinx_runtime;

                GRANT USAGE, CREATE ON SCHEMA foundation TO vinx_authorization_owner;
                SET ROLE vinx_authorization_owner;
                CREATE FUNCTION foundation.current_company_membership_options()
                RETURNS TABLE (membership_id uuid, display_name text)
                LANGUAGE sql
                STABLE
                SECURITY DEFINER
                SET search_path TO foundation, pg_catalog
                AS $function$
                    SELECT membership.id, user_record.display_name::text
                    FROM foundation.memberships AS membership
                    INNER JOIN foundation.users AS user_record ON user_record.id = membership.user_id
                    WHERE membership.company_id = NULLIF(current_setting('app.company_id', true), '')::uuid
                      AND membership.deleted_at IS NULL
                      AND EXISTS (
                          SELECT 1
                          FROM foundation.current_actor_memberships() AS actor_membership
                          WHERE actor_membership.company_id = membership.company_id)
                    ORDER BY user_record.display_name, membership.id;
                $function$;
                REVOKE ALL ON FUNCTION foundation.current_company_membership_options()
                    FROM PUBLIC, vinx_migrator, vinx_runtime;
                GRANT EXECUTE ON FUNCTION foundation.current_company_membership_options()
                    TO vinx_runtime;
                RESET ROLE;
                REVOKE CREATE ON SCHEMA foundation FROM vinx_authorization_owner;
                """
            );

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(
                """
                DO $block$
                BEGIN
                    IF EXISTS (SELECT 1 FROM service_desk.atendimento_card_participants)
                        OR EXISTS (
                            SELECT 1 FROM service_desk.atendimento_cards
                            WHERE attachment_file_id IS NOT NULL
                               OR column_name = 'cancelado'
                               OR cancelled_at IS NOT NULL
                               OR cancelled_by_user_id IS NOT NULL
                               OR cancellation_reason IS NOT NULL)
                        OR EXISTS (
                            SELECT 1 FROM service_desk.atendimento_card_history
                            WHERE from_column = 'cancelado' OR to_column = 'cancelado') THEN
                        RAISE EXCEPTION 'cannot downgrade Atendimento cancellation while new references remain'
                            USING ERRCODE = '23514';
                    END IF;
                END;
                $block$;

                SET ROLE vinx_authorization_owner;
                REVOKE ALL ON FUNCTION foundation.current_company_membership_options()
                    FROM PUBLIC, vinx_migrator, vinx_runtime;
                DROP FUNCTION foundation.current_company_membership_options();
                RESET ROLE;

                REVOKE ALL ON service_desk.atendimento_card_participants FROM PUBLIC, vinx_runtime;
                DROP POLICY atendimento_card_participants_company_isolation
                    ON service_desk.atendimento_card_participants;
                DROP TABLE service_desk.atendimento_card_participants;

                DROP INDEX service_desk.ix_atendimento_cards_company_id_attachment_file_id;
                DROP INDEX service_desk.ix_atendimento_cards_cancelled_by_user_id;

                ALTER TABLE service_desk.atendimento_cards
                    DROP CONSTRAINT fk_atendimento_cards_files_company_id_attachment_file_id,
                    DROP CONSTRAINT fk_atendimento_cards_users_cancelled_by_user_id,
                    DROP CONSTRAINT ck_atendimento_cards_cancellation_complete,
                    DROP CONSTRAINT atendimento_cards_column_name_check,
                    ADD CONSTRAINT atendimento_cards_column_name_check
                        CHECK (column_name IN ('novo', 'em_andamento', 'concluido')),
                    DROP COLUMN attachment_file_id,
                    DROP COLUMN cancelled_at,
                    DROP COLUMN cancelled_by_user_id,
                    DROP COLUMN cancellation_reason;

                ALTER TABLE service_desk.atendimento_card_history
                    DROP CONSTRAINT atendimento_card_history_from_column_check,
                    DROP CONSTRAINT atendimento_card_history_to_column_check,
                    ADD CONSTRAINT atendimento_card_history_from_column_check
                        CHECK (from_column IN ('novo', 'em_andamento', 'concluido')),
                    ADD CONSTRAINT atendimento_card_history_to_column_check
                        CHECK (to_column IN ('novo', 'em_andamento', 'concluido'));
                """
            );
    }
}
