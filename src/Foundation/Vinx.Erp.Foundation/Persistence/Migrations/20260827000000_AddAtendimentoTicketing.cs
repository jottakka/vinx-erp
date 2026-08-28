using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Vinx.Erp.Foundation.Persistence;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations;

[DbContext(typeof(FoundationDbContext))]
[Migration("20260827000000_AddAtendimentoTicketing")]
public sealed class AddAtendimentoTicketing : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            INSERT INTO foundation.capabilities (id, name, description, created_at)
            VALUES
                ('0e772f6e-97e9-5e8b-82ba-f254691b5d22', 'service_desk.ticket.fields', 'Atualizar campos de ticket de Atendimento', clock_timestamp()),
                ('3886e350-3784-5f75-9289-8cb5b0a138f6', 'service_desk.ticket.watchers', 'Gerenciar observadores de Atendimento', clock_timestamp()),
                ('84899de4-b2ff-56bd-8720-264e61d11148', 'service_desk.lookup.admin', 'Administrar categorias do Atendimento', clock_timestamp())
            ON CONFLICT (name) DO NOTHING;

            INSERT INTO foundation.capabilities (id, name, description, created_at)
            VALUES
                ('128d103b-1449-5962-98ac-1a7d8edd1063', 'directory.read', 'Ler diretório de pessoas da empresa', clock_timestamp())
            ON CONFLICT (name) DO NOTHING;

            CREATE TABLE service_desk.atendimento_channels (
                id uuid PRIMARY KEY,
                company_id uuid NOT NULL REFERENCES foundation.companies(id) ON DELETE RESTRICT,
                key varchar(20) NOT NULL CHECK (key IN ('whatsapp', 'email', 'web', 'manual')),
                is_active boolean NOT NULL DEFAULT true,
                CONSTRAINT uq_atendimento_channels_company_id_key UNIQUE (company_id, key),
                CONSTRAINT uq_atendimento_channels_company_id_id UNIQUE (company_id, id)
            );

            CREATE TABLE service_desk.atendimento_categories (
                id uuid PRIMARY KEY,
                company_id uuid NOT NULL REFERENCES foundation.companies(id) ON DELETE RESTRICT,
                name varchar(80) NOT NULL CHECK (name = btrim(name) AND btrim(name) <> ''),
                is_active boolean NOT NULL DEFAULT true,
                created_by_user_id uuid NOT NULL REFERENCES foundation.users(id) ON DELETE RESTRICT,
                updated_by_user_id uuid NOT NULL REFERENCES foundation.users(id) ON DELETE RESTRICT,
                request_id uuid NOT NULL,
                created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
                updated_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
                CONSTRAINT uq_atendimento_categories_company_id_name UNIQUE (company_id, name),
                CONSTRAINT uq_atendimento_categories_company_id_id UNIQUE (company_id, id)
            );

            CREATE TABLE service_desk.atendimento_card_watchers (
                id uuid PRIMARY KEY,
                company_id uuid NOT NULL REFERENCES foundation.companies(id) ON DELETE RESTRICT,
                card_id uuid NOT NULL,
                membership_id uuid NOT NULL,
                created_by_user_id uuid NOT NULL REFERENCES foundation.users(id) ON DELETE RESTRICT,
                request_id uuid NOT NULL,
                created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
                CONSTRAINT uq_atendimento_card_watchers UNIQUE (company_id, card_id, membership_id),
                CONSTRAINT fk_atendimento_card_watchers_card
                    FOREIGN KEY (company_id, card_id)
                    REFERENCES service_desk.atendimento_cards(company_id, id) ON DELETE RESTRICT,
                CONSTRAINT fk_atendimento_card_watchers_membership
                    FOREIGN KEY (company_id, membership_id)
                    REFERENCES foundation.memberships(company_id, id) ON DELETE RESTRICT
            );

            CREATE TABLE service_desk.atendimento_card_events (
                id uuid PRIMARY KEY,
                company_id uuid NOT NULL,
                card_id uuid NOT NULL,
                event_seq bigint GENERATED ALWAYS AS IDENTITY,
                action_kind varchar(30) NOT NULL CHECK (action_kind IN
                    ('created', 'assigned', 'mentioned', 'moved', 'fields_updated', 'watcher_added', 'watcher_removed')),
                actor_membership_id uuid NULL REFERENCES foundation.memberships(id) ON DELETE SET NULL,
                payload_digest varchar(64) NOT NULL,
                occurred_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
                CONSTRAINT fk_atendimento_card_events_card
                    FOREIGN KEY (company_id, card_id)
                    REFERENCES service_desk.atendimento_cards(company_id, id) ON DELETE RESTRICT
            );
            CREATE INDEX ix_atendimento_card_events_company_card_seq
                ON service_desk.atendimento_card_events (company_id, card_id, event_seq);

            ALTER TABLE service_desk.atendimento_cards
                ADD COLUMN requester_person_ref varchar(200) NULL,
                ADD COLUMN category_ref_id uuid NULL,
                ADD COLUMN channel_ref_id uuid NULL,
                ADD CONSTRAINT fk_atendimento_cards_category
                    FOREIGN KEY (company_id, category_ref_id)
                    REFERENCES service_desk.atendimento_categories(company_id, id),
                ADD CONSTRAINT fk_atendimento_cards_channel
                    FOREIGN KEY (company_id, channel_ref_id)
                    REFERENCES service_desk.atendimento_channels(company_id, id);

            -- priority/sla_due_at/channel já existem pelo 20260826225942 do main;
            -- a fatia P6 alarga priority para varchar(20) e a torna nullável
            -- (ausência = sem prioridade definida), sem default aplicado.
            ALTER TABLE service_desk.atendimento_cards
                DROP CONSTRAINT ck_atendimento_cards_priority,
                ALTER COLUMN priority DROP NOT NULL,
                ALTER COLUMN priority DROP DEFAULT,
                ALTER COLUMN priority TYPE varchar(20),
                ADD CONSTRAINT ck_atendimento_cards_priority
                    CHECK (priority IN ('critica', 'alta', 'media', 'baixa'));

            -- Semeadura por empresa: existentes no backfill abaixo e novas pelo
            -- trigger. A função roda como dono das tabelas alvo; o runtime segue
            -- limitado pelas policies por empresa.
            CREATE FUNCTION service_desk.seed_company_lookup_defaults_trigger()
            RETURNS trigger
            LANGUAGE plpgsql
            SECURITY DEFINER
            SET search_path TO pg_catalog, service_desk, pg_temp
            AS $function$
            BEGIN
                -- Mesma disciplina das funções de provisionamento: contexto de
                -- empresa antes da escrita para satisfazer a policy com FORCE.
                PERFORM set_config('app.company_id', NEW.id::text, true);
                INSERT INTO service_desk.atendimento_channels (id, company_id, key)
                VALUES
                    (gen_random_uuid(), NEW.id, 'whatsapp'),
                    (gen_random_uuid(), NEW.id, 'email'),
                    (gen_random_uuid(), NEW.id, 'web'),
                    (gen_random_uuid(), NEW.id, 'manual')
                ON CONFLICT (company_id, key) DO NOTHING;
                RETURN NULL;
            END;
            $function$;
            CREATE TRIGGER trg_seed_company_lookup_defaults
                AFTER INSERT ON foundation.companies
                FOR EACH ROW EXECUTE FUNCTION service_desk.seed_company_lookup_defaults_trigger();

            -- Backfill dos canais das empresas vivas criadas antes da RLS.
            INSERT INTO service_desk.atendimento_channels (id, company_id, key)
            SELECT gen_random_uuid(), company.id, candidate.key
            FROM foundation.companies AS company
            CROSS JOIN (VALUES ('whatsapp'), ('email'), ('web'), ('manual')) AS candidate(key)
            WHERE company.deleted_at IS NULL
            ON CONFLICT (company_id, key) DO NOTHING;

            ALTER TABLE service_desk.atendimento_channels ENABLE ROW LEVEL SECURITY;
            ALTER TABLE service_desk.atendimento_channels FORCE ROW LEVEL SECURITY;
            CREATE POLICY atendimento_channels_company_isolation ON service_desk.atendimento_channels
                USING (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid)
                WITH CHECK (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);
            ALTER TABLE service_desk.atendimento_categories ENABLE ROW LEVEL SECURITY;
            ALTER TABLE service_desk.atendimento_categories FORCE ROW LEVEL SECURITY;
            CREATE POLICY atendimento_categories_company_isolation ON service_desk.atendimento_categories
                USING (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid)
                WITH CHECK (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);
            ALTER TABLE service_desk.atendimento_card_watchers ENABLE ROW LEVEL SECURITY;
            ALTER TABLE service_desk.atendimento_card_watchers FORCE ROW LEVEL SECURITY;
            CREATE POLICY atendimento_card_watchers_company_isolation ON service_desk.atendimento_card_watchers
                USING (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid)
                WITH CHECK (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);
            ALTER TABLE service_desk.atendimento_card_events ENABLE ROW LEVEL SECURITY;
            ALTER TABLE service_desk.atendimento_card_events FORCE ROW LEVEL SECURITY;
            CREATE POLICY atendimento_card_events_company_isolation ON service_desk.atendimento_card_events
                USING (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid)
                WITH CHECK (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);

            GRANT SELECT ON service_desk.atendimento_channels TO vinx_runtime;
            GRANT SELECT, INSERT, UPDATE ON service_desk.atendimento_categories TO vinx_runtime;
            GRANT SELECT, INSERT, DELETE ON service_desk.atendimento_card_watchers TO vinx_runtime;
            GRANT SELECT, INSERT ON service_desk.atendimento_card_events TO vinx_runtime;

            -- Timeline resolve o nome do ator com leitura de menor privilégio:
            -- o grant do 20260825110000 cobre somente vinx_permissions_owner,
            -- então o runtime do atendimento precisa da mesma coluna mínima.
            GRANT SELECT (id, display_name) ON foundation.users TO vinx_runtime;
            """
        );

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            DROP TRIGGER IF EXISTS trg_seed_company_lookup_defaults ON foundation.companies;
            REVOKE ALL ON service_desk.atendimento_card_events,
                service_desk.atendimento_card_watchers,
                service_desk.atendimento_categories,
                service_desk.atendimento_channels FROM vinx_runtime;
            REVOKE SELECT (id, display_name) ON foundation.users FROM vinx_runtime;
            DROP TABLE service_desk.atendimento_card_events;
            DROP TABLE service_desk.atendimento_card_watchers;
            ALTER TABLE service_desk.atendimento_cards
                DROP CONSTRAINT fk_atendimento_cards_channel,
                DROP CONSTRAINT fk_atendimento_cards_category,
                DROP COLUMN channel_ref_id,
                DROP COLUMN category_ref_id,
                DROP COLUMN requester_person_ref;
            -- Restaura o shape de priority do 20260826225942 (main): varchar(10)
            -- NOT NULL com default 'media'. Linhas sem prioridade voltam a
            -- 'media'; o NO FORCE segue o padrão de Down do próprio main.
            ALTER TABLE service_desk.atendimento_cards NO FORCE ROW LEVEL SECURITY;
            UPDATE service_desk.atendimento_cards SET priority = 'media' WHERE priority IS NULL;
            ALTER TABLE service_desk.atendimento_cards FORCE ROW LEVEL SECURITY;
            ALTER TABLE service_desk.atendimento_cards
                DROP CONSTRAINT ck_atendimento_cards_priority,
                ALTER COLUMN priority TYPE varchar(10),
                ALTER COLUMN priority SET NOT NULL,
                ALTER COLUMN priority SET DEFAULT 'media',
                ADD CONSTRAINT ck_atendimento_cards_priority
                    CHECK (priority IN ('critica', 'alta', 'media', 'baixa'));
            DROP TABLE service_desk.atendimento_categories;
            DROP TABLE service_desk.atendimento_channels;
            DROP FUNCTION service_desk.seed_company_lookup_defaults_trigger();
            DO $block$
            BEGIN
                DELETE FROM foundation.capabilities
                WHERE (id, name) IN (
                    ('0e772f6e-97e9-5e8b-82ba-f254691b5d22'::uuid, 'service_desk.ticket.fields'),
                    ('3886e350-3784-5f75-9289-8cb5b0a138f6'::uuid, 'service_desk.ticket.watchers'),
                    ('84899de4-b2ff-56bd-8720-264e61d11148'::uuid, 'service_desk.lookup.admin'),
                    ('128d103b-1449-5962-98ac-1a7d8edd1063'::uuid, 'directory.read'));
            EXCEPTION
                WHEN foreign_key_violation THEN
                    RAISE EXCEPTION 'cannot downgrade p6 capabilities while capabilities are referenced'
                        USING ERRCODE = '23503';
            END;
            $block$;
            """
        );
}
