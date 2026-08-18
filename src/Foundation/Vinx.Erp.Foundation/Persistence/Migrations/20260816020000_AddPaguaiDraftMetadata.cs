using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations;

/// <inheritdoc />
[DbContext(typeof(FoundationDbContext))]
[Migration("20260816020000_AddPaguaiDraftMetadata")]
public partial class AddPaguaiDraftMetadata : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            DO $block$
            DECLARE
                capability_record record;
            BEGIN
                FOR capability_record IN
                    SELECT *
                    FROM (VALUES
                        ('766bdb56-80c0-5dee-982f-be7f0d3eaea8'::uuid, 'paguai.draft.read'),
                        ('aecaa296-6a6f-5815-93ac-bd1ccdff3f44'::uuid, 'paguai.draft.create'))
                        AS expected(id, name)
                LOOP
                    IF EXISTS (
                        SELECT 1 FROM foundation.capabilities
                        WHERE id = capability_record.id AND name = capability_record.name) THEN
                        CONTINUE;
                    END IF;
                    IF EXISTS (
                        SELECT 1 FROM foundation.capabilities
                        WHERE id = capability_record.id OR name = capability_record.name) THEN
                        RAISE EXCEPTION 'Paguai capability catalog conflicts with canonical identifier'
                            USING ERRCODE = '23505';
                    END IF;
                    INSERT INTO foundation.capabilities (id, name, created_at)
                    VALUES (capability_record.id, capability_record.name, clock_timestamp());
                END LOOP;
            END;
            $block$;

            CREATE SCHEMA paguai;
            REVOKE ALL ON SCHEMA paguai FROM PUBLIC;
            GRANT USAGE ON SCHEMA paguai TO vinx_runtime;

            CREATE TABLE paguai.requests (
                id uuid NOT NULL,
                company_id uuid NOT NULL,
                requester_user_id uuid NOT NULL,
                request_id uuid NOT NULL,
                title text NOT NULL,
                description text NOT NULL,
                state text NOT NULL DEFAULT 'rascunho',
                version integer NOT NULL DEFAULT 1,
                created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
                CONSTRAINT pk_paguai_requests PRIMARY KEY (id),
                CONSTRAINT fk_paguai_requests_company FOREIGN KEY (company_id)
                    REFERENCES foundation.companies(id) ON DELETE RESTRICT,
                CONSTRAINT fk_paguai_requests_requester FOREIGN KEY (requester_user_id)
                    REFERENCES foundation.users(id) ON DELETE RESTRICT,
                CONSTRAINT ck_paguai_requests_title CHECK (
                    char_length(title) BETWEEN 1 AND 200 AND title = btrim(title)),
                CONSTRAINT ck_paguai_requests_description CHECK (
                    char_length(description) BETWEEN 1 AND 4000 AND description = btrim(description)),
                CONSTRAINT ck_paguai_requests_state CHECK (state = 'rascunho'),
                CONSTRAINT ck_paguai_requests_version CHECK (version = 1)
            );
            CREATE INDEX ix_paguai_requests_requester_list
                ON paguai.requests (company_id, requester_user_id, created_at DESC, id DESC);

            ALTER TABLE paguai.requests ENABLE ROW LEVEL SECURITY;
            ALTER TABLE paguai.requests FORCE ROW LEVEL SECURITY;
            CREATE POLICY paguai_requests_requester_isolation ON paguai.requests
                FOR ALL TO vinx_runtime
                USING (
                    company_id = NULLIF(current_setting('app.company_id', true), '')::uuid
                    AND requester_user_id = NULLIF(current_setting('app.user_id', true), '')::uuid)
                WITH CHECK (
                    company_id = NULLIF(current_setting('app.company_id', true), '')::uuid
                    AND requester_user_id = NULLIF(current_setting('app.user_id', true), '')::uuid);

            REVOKE ALL ON TABLE paguai.requests FROM PUBLIC, vinx_runtime;
            GRANT SELECT, INSERT ON TABLE paguai.requests TO vinx_runtime;
            """
        );

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            DO $block$
            BEGIN
                IF EXISTS (SELECT 1 FROM paguai.requests) THEN
                    RAISE EXCEPTION 'cannot downgrade Paguai draft metadata while requests exist'
                        USING ERRCODE = '23514';
                END IF;
            END;
            $block$;
            REVOKE ALL ON TABLE paguai.requests FROM PUBLIC, vinx_runtime;
            DROP TABLE paguai.requests;
            REVOKE USAGE ON SCHEMA paguai FROM vinx_runtime;
            DROP SCHEMA paguai;
            DELETE FROM foundation.capabilities
            WHERE (id, name) IN (
                ('766bdb56-80c0-5dee-982f-be7f0d3eaea8'::uuid, 'paguai.draft.read'),
                ('aecaa296-6a6f-5815-93ac-bd1ccdff3f44'::uuid, 'paguai.draft.create'));
            """
        );
}
