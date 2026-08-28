using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vinx.Erp.Foundation.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialFoundationBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE SCHEMA IF NOT EXISTS foundation;");
            migrationBuilder.Sql("CREATE SCHEMA IF NOT EXISTS audit;");

            migrationBuilder.CreateTable(
                name: "audit_events",
                schema: "audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_company_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source = table.Column<string>(
                        type: "character varying(20)",
                        maxLength: 20,
                        nullable: false
                    ),
                    action = table.Column<string>(
                        type: "character varying(200)",
                        maxLength: 200,
                        nullable: false
                    ),
                    resource_type = table.Column<string>(
                        type: "character varying(200)",
                        maxLength: 200,
                        nullable: false
                    ),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: true),
                    outcome = table.Column<string>(
                        type: "character varying(20)",
                        maxLength: 20,
                        nullable: false
                    ),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(
                        type: "character varying(1000)",
                        maxLength: 1000,
                        nullable: true
                    ),
                    metadata_json = table.Column<string>(type: "jsonb", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_events", x => x.id);
                    table.CheckConstraint(
                        "ck_audit_events_requested_company",
                        "requested_company_id IS NOT NULL OR (action = 'company_context_required' AND outcome = 'denied')"
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "capabilities",
                schema: "foundation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(
                        type: "character varying(200)",
                        maxLength: 200,
                        nullable: false
                    ),
                    description = table.Column<string>(
                        type: "character varying(1000)",
                        maxLength: 1000,
                        nullable: true
                    ),
                    created_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_capabilities", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "groups",
                schema: "foundation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(
                        type: "character varying(200)",
                        maxLength: 200,
                        nullable: false
                    ),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    updated_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    deleted_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    deleted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_reason = table.Column<string>(
                        type: "character varying(500)",
                        maxLength: 500,
                        nullable: true
                    ),
                    row_version = table.Column<long>(
                        type: "bigint",
                        nullable: false,
                        defaultValue: 1L
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_groups", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "users",
                schema: "foundation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(
                        type: "character varying(200)",
                        maxLength: 200,
                        nullable: false
                    ),
                    email = table.Column<string>(
                        type: "character varying(320)",
                        maxLength: 320,
                        nullable: false
                    ),
                    created_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                }
            );

            migrationBuilder.CreateTable(
                name: "companies",
                schema: "foundation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(
                        type: "character varying(200)",
                        maxLength: 200,
                        nullable: false
                    ),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    updated_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    deleted_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    deleted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_reason = table.Column<string>(
                        type: "character varying(500)",
                        maxLength: 500,
                        nullable: true
                    ),
                    row_version = table.Column<long>(
                        type: "bigint",
                        nullable: false,
                        defaultValue: 1L
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_companies", x => x.id);
                    table.ForeignKey(
                        name: "fk_companies_groups_group_id",
                        column: x => x.group_id,
                        principalSchema: "foundation",
                        principalTable: "groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "external_identities",
                schema: "foundation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(
                        type: "character varying(100)",
                        maxLength: 100,
                        nullable: false
                    ),
                    subject = table.Column<string>(
                        type: "character varying(400)",
                        maxLength: 400,
                        nullable: false
                    ),
                    created_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_external_identities", x => x.id);
                    table.ForeignKey(
                        name: "fk_external_identities_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "foundation",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "idempotency_records",
                schema: "foundation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    command_name = table.Column<string>(
                        type: "character varying(200)",
                        maxLength: 200,
                        nullable: false
                    ),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payload_hash = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    result_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_records", x => x.id);
                    table.UniqueConstraint(
                        "ak_idempotency_records_company_id_id",
                        x => new { x.company_id, x.id }
                    );
                    table.ForeignKey(
                        name: "fk_idempotency_records_companies_company_id",
                        column: x => x.company_id,
                        principalSchema: "foundation",
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "memberships",
                schema: "foundation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    updated_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    deleted_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    deleted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_reason = table.Column<string>(
                        type: "character varying(500)",
                        maxLength: 500,
                        nullable: true
                    ),
                    row_version = table.Column<long>(
                        type: "bigint",
                        nullable: false,
                        defaultValue: 1L
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_memberships", x => x.id);
                    table.UniqueConstraint(
                        "ak_memberships_company_id_id",
                        x => new { x.company_id, x.id }
                    );
                    table.ForeignKey(
                        name: "fk_memberships_companies_company_id",
                        column: x => x.company_id,
                        principalSchema: "foundation",
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "fk_memberships_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "foundation",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "roles",
                schema: "foundation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(
                        type: "character varying(200)",
                        maxLength: 200,
                        nullable: false
                    ),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    updated_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    deleted_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    deleted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_reason = table.Column<string>(
                        type: "character varying(500)",
                        maxLength: 500,
                        nullable: true
                    ),
                    row_version = table.Column<long>(
                        type: "bigint",
                        nullable: false,
                        defaultValue: 1L
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_roles", x => x.id);
                    table.UniqueConstraint(
                        "ak_roles_company_id_id",
                        x => new { x.company_id, x.id }
                    );
                    table.ForeignKey(
                        name: "fk_roles_companies_company_id",
                        column: x => x.company_id,
                        principalSchema: "foundation",
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "units",
                schema: "foundation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(
                        type: "character varying(200)",
                        maxLength: 200,
                        nullable: false
                    ),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    updated_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    deleted_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    deleted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_reason = table.Column<string>(
                        type: "character varying(500)",
                        maxLength: 500,
                        nullable: true
                    ),
                    row_version = table.Column<long>(
                        type: "bigint",
                        nullable: false,
                        defaultValue: 1L
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_units", x => x.id);
                    table.UniqueConstraint(
                        "ak_units_company_id_id",
                        x => new { x.company_id, x.id }
                    );
                    table.ForeignKey(
                        name: "fk_units_companies_company_id",
                        column: x => x.company_id,
                        principalSchema: "foundation",
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "employees",
                schema: "foundation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(
                        type: "character varying(200)",
                        maxLength: 200,
                        nullable: false
                    ),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    updated_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    deleted_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    deleted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_reason = table.Column<string>(
                        type: "character varying(500)",
                        maxLength: 500,
                        nullable: true
                    ),
                    row_version = table.Column<long>(
                        type: "bigint",
                        nullable: false,
                        defaultValue: 1L
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_employees", x => x.id);
                    table.UniqueConstraint(
                        "ak_employees_company_id_id",
                        x => new { x.company_id, x.id }
                    );
                    table.ForeignKey(
                        name: "fk_employees_companies_company_id",
                        column: x => x.company_id,
                        principalSchema: "foundation",
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "fk_employees_memberships_company_id_membership_id",
                        columns: x => new { x.company_id, x.membership_id },
                        principalSchema: "foundation",
                        principalTable: "memberships",
                        principalColumns: new[] { "company_id", "id" },
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "files",
                schema: "foundation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    uploader_membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    classification = table.Column<string>(
                        type: "character varying(100)",
                        maxLength: 100,
                        nullable: false
                    ),
                    checksum = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    provider_key = table.Column<string>(
                        type: "character varying(1000)",
                        maxLength: 1000,
                        nullable: false
                    ),
                    is_quarantined = table.Column<bool>(type: "boolean", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    updated_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    deleted_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    deleted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_reason = table.Column<string>(
                        type: "character varying(500)",
                        maxLength: 500,
                        nullable: true
                    ),
                    row_version = table.Column<long>(
                        type: "bigint",
                        nullable: false,
                        defaultValue: 1L
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_files", x => x.id);
                    table.UniqueConstraint(
                        "ak_files_company_id_id",
                        x => new { x.company_id, x.id }
                    );
                    table.ForeignKey(
                        name: "fk_files_companies_company_id",
                        column: x => x.company_id,
                        principalSchema: "foundation",
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "fk_files_memberships_company_id_uploader_membership_id",
                        columns: x => new { x.company_id, x.uploader_membership_id },
                        principalSchema: "foundation",
                        principalTable: "memberships",
                        principalColumns: new[] { "company_id", "id" },
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "role_assignments",
                schema: "foundation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    updated_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    deleted_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    deleted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_reason = table.Column<string>(
                        type: "character varying(500)",
                        maxLength: 500,
                        nullable: true
                    ),
                    row_version = table.Column<long>(
                        type: "bigint",
                        nullable: false,
                        defaultValue: 1L
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_role_assignments", x => x.id);
                    table.UniqueConstraint(
                        "ak_role_assignments_company_id_id",
                        x => new { x.company_id, x.id }
                    );
                    table.ForeignKey(
                        name: "fk_role_assignments_companies_company_id",
                        column: x => x.company_id,
                        principalSchema: "foundation",
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "fk_role_assignments_memberships_company_id_membership_id",
                        columns: x => new { x.company_id, x.membership_id },
                        principalSchema: "foundation",
                        principalTable: "memberships",
                        principalColumns: new[] { "company_id", "id" },
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "fk_role_assignments_roles_company_id_role_id",
                        columns: x => new { x.company_id, x.role_id },
                        principalSchema: "foundation",
                        principalTable: "roles",
                        principalColumns: new[] { "company_id", "id" },
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "role_capabilities",
                schema: "foundation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    capability_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(
                        type: "character varying(20)",
                        maxLength: 20,
                        nullable: false
                    ),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    updated_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    deleted_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    deleted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_reason = table.Column<string>(
                        type: "character varying(500)",
                        maxLength: 500,
                        nullable: true
                    ),
                    row_version = table.Column<long>(
                        type: "bigint",
                        nullable: false,
                        defaultValue: 1L
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_role_capabilities", x => x.id);
                    table.UniqueConstraint(
                        "ak_role_capabilities_company_id_id",
                        x => new { x.company_id, x.id }
                    );
                    table.CheckConstraint(
                        "ck_role_capabilities_scope",
                        "scope IN ('self', 'team', 'unit', 'company', 'group')"
                    );
                    table.ForeignKey(
                        name: "fk_role_capabilities_capabilities_capability_id",
                        column: x => x.capability_id,
                        principalSchema: "foundation",
                        principalTable: "capabilities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "fk_role_capabilities_roles_company_id_role_id",
                        columns: x => new { x.company_id, x.role_id },
                        principalSchema: "foundation",
                        principalTable: "roles",
                        principalColumns: new[] { "company_id", "id" },
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "teams",
                schema: "foundation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    company_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(
                        type: "character varying(200)",
                        maxLength: 200,
                        nullable: false
                    ),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    updated_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    deleted_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    deleted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_reason = table.Column<string>(
                        type: "character varying(500)",
                        maxLength: 500,
                        nullable: true
                    ),
                    row_version = table.Column<long>(
                        type: "bigint",
                        nullable: false,
                        defaultValue: 1L
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_teams", x => x.id);
                    table.UniqueConstraint(
                        "ak_teams_company_id_id",
                        x => new { x.company_id, x.id }
                    );
                    table.ForeignKey(
                        name: "fk_teams_companies_company_id",
                        column: x => x.company_id,
                        principalSchema: "foundation",
                        principalTable: "companies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "fk_teams_units_company_id_unit_id",
                        columns: x => new { x.company_id, x.unit_id },
                        principalSchema: "foundation",
                        principalTable: "units",
                        principalColumns: new[] { "company_id", "id" },
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_actor_user_id_occurred_at",
                schema: "audit",
                table: "audit_events",
                columns: new[] { "actor_user_id", "occurred_at" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_requested_company_id_occurred_at",
                schema: "audit",
                table: "audit_events",
                columns: new[] { "requested_company_id", "occurred_at" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_capabilities_name",
                schema: "foundation",
                table: "capabilities",
                column: "name",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_companies_group_id_name",
                schema: "foundation",
                table: "companies",
                columns: new[] { "group_id", "name" },
                unique: true,
                filter: "deleted_at IS NULL"
            );

            migrationBuilder.CreateIndex(
                name: "ix_employees_company_id_membership_id",
                schema: "foundation",
                table: "employees",
                columns: new[] { "company_id", "membership_id" },
                unique: true,
                filter: "deleted_at IS NULL"
            );

            migrationBuilder.CreateIndex(
                name: "ix_external_identities_provider_subject",
                schema: "foundation",
                table: "external_identities",
                columns: new[] { "provider", "subject" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_external_identities_user_id",
                schema: "foundation",
                table: "external_identities",
                column: "user_id"
            );

            migrationBuilder.CreateIndex(
                name: "ix_files_company_id_provider_key",
                schema: "foundation",
                table: "files",
                columns: new[] { "company_id", "provider_key" },
                unique: true,
                filter: "deleted_at IS NULL"
            );

            migrationBuilder.CreateIndex(
                name: "ix_files_company_id_uploader_membership_id",
                schema: "foundation",
                table: "files",
                columns: new[] { "company_id", "uploader_membership_id" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_groups_name",
                schema: "foundation",
                table: "groups",
                column: "name",
                unique: true,
                filter: "deleted_at IS NULL"
            );

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_records_company_id_actor_user_id_command_name_r",
                schema: "foundation",
                table: "idempotency_records",
                columns: new[] { "company_id", "actor_user_id", "command_name", "request_id" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_memberships_company_id_user_id",
                schema: "foundation",
                table: "memberships",
                columns: new[] { "company_id", "user_id" },
                unique: true,
                filter: "deleted_at IS NULL"
            );

            migrationBuilder.CreateIndex(
                name: "ix_memberships_user_id",
                schema: "foundation",
                table: "memberships",
                column: "user_id"
            );

            migrationBuilder.CreateIndex(
                name: "ix_role_assignments_company_id_membership_id",
                schema: "foundation",
                table: "role_assignments",
                columns: new[] { "company_id", "membership_id" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_role_assignments_company_id_role_id_membership_id",
                schema: "foundation",
                table: "role_assignments",
                columns: new[] { "company_id", "role_id", "membership_id" },
                unique: true,
                filter: "deleted_at IS NULL"
            );

            migrationBuilder.CreateIndex(
                name: "ix_role_capabilities_capability_id",
                schema: "foundation",
                table: "role_capabilities",
                column: "capability_id"
            );

            migrationBuilder.CreateIndex(
                name: "ix_role_capabilities_company_id_role_id_capability_id_scope",
                schema: "foundation",
                table: "role_capabilities",
                columns: new[] { "company_id", "role_id", "capability_id", "scope" },
                unique: true,
                filter: "deleted_at IS NULL"
            );

            migrationBuilder.CreateIndex(
                name: "ix_roles_company_id_name",
                schema: "foundation",
                table: "roles",
                columns: new[] { "company_id", "name" },
                unique: true,
                filter: "deleted_at IS NULL"
            );

            migrationBuilder.CreateIndex(
                name: "ix_teams_company_id_name",
                schema: "foundation",
                table: "teams",
                columns: new[] { "company_id", "name" },
                unique: true,
                filter: "deleted_at IS NULL"
            );

            migrationBuilder.CreateIndex(
                name: "ix_teams_company_id_unit_id",
                schema: "foundation",
                table: "teams",
                columns: new[] { "company_id", "unit_id" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_units_company_id_name",
                schema: "foundation",
                table: "units",
                columns: new[] { "company_id", "name" },
                unique: true,
                filter: "deleted_at IS NULL"
            );

            migrationBuilder.CreateIndex(
                name: "ix_users_email",
                schema: "foundation",
                table: "users",
                column: "email",
                unique: true
            );

            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    EXECUTE format('REVOKE ALL ON DATABASE %I FROM PUBLIC', current_database());
                    EXECUTE format('GRANT CONNECT ON DATABASE %I TO vinx_runtime', current_database());
                END
                $$;

                REVOKE ALL ON SCHEMA foundation FROM PUBLIC;
                REVOKE ALL ON SCHEMA audit FROM PUBLIC;
                REVOKE ALL ON ALL TABLES IN SCHEMA foundation FROM PUBLIC;
                REVOKE ALL ON ALL TABLES IN SCHEMA audit FROM PUBLIC;
                REVOKE ALL ON ALL FUNCTIONS IN SCHEMA audit FROM PUBLIC;

                GRANT USAGE ON SCHEMA foundation, audit TO vinx_runtime;
                GRANT SELECT ON TABLE foundation.companies TO vinx_runtime;
                GRANT SELECT ON TABLE foundation.groups TO vinx_runtime;
                GRANT SELECT ON TABLE foundation.memberships, foundation.roles,
                    foundation.role_capabilities, foundation.role_assignments,
                    foundation.idempotency_records TO vinx_runtime;
                GRANT INSERT, UPDATE ON TABLE foundation.role_assignments,
                    foundation.idempotency_records TO vinx_runtime;
                GRANT SELECT ON TABLE foundation.files TO vinx_runtime;
                GRANT SELECT ON TABLE foundation.capabilities TO vinx_runtime;

                REVOKE ALL ON TABLE foundation.files, foundation.users,
                    foundation.external_identities, foundation.capabilities,
                    audit.audit_events FROM vinx_runtime;
                GRANT SELECT ON TABLE foundation.files, foundation.capabilities TO vinx_runtime;

                ALTER TABLE foundation.companies ENABLE ROW LEVEL SECURITY;
                ALTER TABLE foundation.companies FORCE ROW LEVEL SECURITY;
                CREATE POLICY companies_company_isolation ON foundation.companies
                    USING (id = NULLIF(current_setting('app.company_id', true), '')::uuid)
                    WITH CHECK (id = NULLIF(current_setting('app.company_id', true), '')::uuid);

                ALTER TABLE foundation.groups ENABLE ROW LEVEL SECURITY;
                ALTER TABLE foundation.groups FORCE ROW LEVEL SECURITY;
                CREATE POLICY groups_company_isolation ON foundation.groups
                    USING (id = (
                        SELECT company.group_id
                        FROM foundation.companies AS company
                        WHERE company.id = NULLIF(current_setting('app.company_id', true), '')::uuid))
                    WITH CHECK (id = (
                        SELECT company.group_id
                        FROM foundation.companies AS company
                        WHERE company.id = NULLIF(current_setting('app.company_id', true), '')::uuid));

                ALTER TABLE foundation.units ENABLE ROW LEVEL SECURITY;
                ALTER TABLE foundation.units FORCE ROW LEVEL SECURITY;
                CREATE POLICY units_company_isolation ON foundation.units
                    USING (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid)
                    WITH CHECK (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);

                ALTER TABLE foundation.teams ENABLE ROW LEVEL SECURITY;
                ALTER TABLE foundation.teams FORCE ROW LEVEL SECURITY;
                CREATE POLICY teams_company_isolation ON foundation.teams
                    USING (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid)
                    WITH CHECK (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);

                ALTER TABLE foundation.memberships ENABLE ROW LEVEL SECURITY;
                ALTER TABLE foundation.memberships FORCE ROW LEVEL SECURITY;
                CREATE POLICY memberships_company_isolation ON foundation.memberships
                    USING (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid)
                    WITH CHECK (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);

                ALTER TABLE foundation.employees ENABLE ROW LEVEL SECURITY;
                ALTER TABLE foundation.employees FORCE ROW LEVEL SECURITY;
                CREATE POLICY employees_company_isolation ON foundation.employees
                    USING (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid)
                    WITH CHECK (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);

                ALTER TABLE foundation.roles ENABLE ROW LEVEL SECURITY;
                ALTER TABLE foundation.roles FORCE ROW LEVEL SECURITY;
                CREATE POLICY roles_company_isolation ON foundation.roles
                    USING (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid)
                    WITH CHECK (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);

                ALTER TABLE foundation.role_capabilities ENABLE ROW LEVEL SECURITY;
                ALTER TABLE foundation.role_capabilities FORCE ROW LEVEL SECURITY;
                CREATE POLICY role_capabilities_company_isolation ON foundation.role_capabilities
                    USING (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid)
                    WITH CHECK (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);

                ALTER TABLE foundation.role_assignments ENABLE ROW LEVEL SECURITY;
                ALTER TABLE foundation.role_assignments FORCE ROW LEVEL SECURITY;
                CREATE POLICY role_assignments_company_isolation ON foundation.role_assignments
                    USING (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid)
                    WITH CHECK (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);

                ALTER TABLE foundation.files ENABLE ROW LEVEL SECURITY;
                ALTER TABLE foundation.files FORCE ROW LEVEL SECURITY;
                CREATE POLICY files_company_isolation ON foundation.files
                    USING (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid)
                    WITH CHECK (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);

                ALTER TABLE foundation.idempotency_records ENABLE ROW LEVEL SECURITY;
                ALTER TABLE foundation.idempotency_records FORCE ROW LEVEL SECURITY;
                CREATE POLICY idempotency_records_company_isolation ON foundation.idempotency_records
                    USING (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid)
                    WITH CHECK (company_id = NULLIF(current_setting('app.company_id', true), '')::uuid);

                ALTER TABLE audit.audit_events DISABLE ROW LEVEL SECURITY;
                REVOKE ALL ON TABLE audit.audit_events FROM PUBLIC, vinx_runtime;

                GRANT USAGE, CREATE ON SCHEMA audit TO vinx_audit_owner;

                CREATE FUNCTION audit.reject_audit_event_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                SET search_path TO audit, pg_catalog
                AS $function$
                BEGIN
                    IF current_user <> 'vinx_audit_owner' THEN
                        RAISE EXCEPTION 'audit events are append-only'
                            USING ERRCODE = '42501';
                    END IF;
                    RETURN OLD;
                END;
                $function$;

                REVOKE ALL ON FUNCTION audit.reject_audit_event_mutation()
                    FROM PUBLIC, vinx_runtime;

                CREATE FUNCTION audit.append_event(
                    p_actor_user_id uuid,
                    p_requested_company_id uuid,
                    p_source text,
                    p_action text,
                    p_resource_type text,
                    p_resource_id uuid,
                    p_outcome text,
                    p_correlation_id uuid,
                    p_reason text,
                    p_metadata jsonb)
                RETURNS void
                LANGUAGE plpgsql
                SECURITY DEFINER
                SET search_path TO audit, pg_catalog
                AS $function$
                BEGIN
                    IF p_requested_company_id IS NULL
                        AND (p_action <> 'company_context_required' OR p_outcome <> 'denied') THEN
                        RAISE EXCEPTION 'requested company is required for this audit event'
                            USING ERRCODE = '23514';
                    END IF;

                    INSERT INTO audit_events (
                        id,
                        actor_user_id,
                        requested_company_id,
                        source,
                        action,
                        resource_type,
                        resource_id,
                        outcome,
                        correlation_id,
                        reason,
                        metadata_json,
                        occurred_at)
                    VALUES (
                        gen_random_uuid(),
                        p_actor_user_id,
                        p_requested_company_id,
                        p_source,
                        p_action,
                        p_resource_type,
                        p_resource_id,
                        p_outcome,
                        p_correlation_id,
                        p_reason,
                        p_metadata,
                        clock_timestamp());
                END;
                $function$;

                CREATE TRIGGER audit_events_reject_mutation
                    BEFORE UPDATE OR DELETE ON audit.audit_events
                    FOR EACH ROW EXECUTE FUNCTION audit.reject_audit_event_mutation();

                REVOKE ALL ON FUNCTION audit.append_event(
                    uuid, uuid, text, text, text, uuid, text, uuid, text, jsonb) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION audit.append_event(
                    uuid, uuid, text, text, text, uuid, text, uuid, text, jsonb) TO vinx_runtime;

                ALTER TABLE audit.audit_events OWNER TO vinx_audit_owner;
                ALTER FUNCTION audit.append_event(
                    uuid, uuid, text, text, text, uuid, text, uuid, text, jsonb) OWNER TO vinx_audit_owner;
                ALTER FUNCTION audit.reject_audit_event_mutation() OWNER TO vinx_audit_owner;
                REVOKE CREATE ON SCHEMA audit FROM vinx_audit_owner;
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                SET ROLE vinx_audit_owner;
                DROP TRIGGER IF EXISTS audit_events_reject_mutation ON audit.audit_events;
                DROP FUNCTION IF EXISTS audit.append_event(
                    uuid, uuid, text, text, text, uuid, text, uuid, text, jsonb);
                DROP FUNCTION IF EXISTS audit.reject_audit_event_mutation();
                DROP TABLE IF EXISTS audit.audit_events;
                RESET ROLE;
                DROP POLICY IF EXISTS groups_company_isolation ON foundation.groups;
                """
            );

            migrationBuilder.DropTable(name: "employees", schema: "foundation");

            migrationBuilder.DropTable(name: "external_identities", schema: "foundation");

            migrationBuilder.DropTable(name: "files", schema: "foundation");

            migrationBuilder.DropTable(name: "idempotency_records", schema: "foundation");

            migrationBuilder.DropTable(name: "role_assignments", schema: "foundation");

            migrationBuilder.DropTable(name: "role_capabilities", schema: "foundation");

            migrationBuilder.DropTable(name: "teams", schema: "foundation");

            migrationBuilder.DropTable(name: "memberships", schema: "foundation");

            migrationBuilder.DropTable(name: "capabilities", schema: "foundation");

            migrationBuilder.DropTable(name: "roles", schema: "foundation");

            migrationBuilder.DropTable(name: "units", schema: "foundation");

            migrationBuilder.DropTable(name: "users", schema: "foundation");

            migrationBuilder.DropTable(name: "companies", schema: "foundation");

            migrationBuilder.DropTable(name: "groups", schema: "foundation");

            migrationBuilder.Sql(
                """
                DROP SCHEMA IF EXISTS audit;
                DROP SCHEMA IF EXISTS foundation;
                """
            );
        }
    }
}
