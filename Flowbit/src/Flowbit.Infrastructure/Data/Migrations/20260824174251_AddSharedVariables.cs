using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSharedVariables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<JsonDocument>(
                name: "SharedVariableWritesJson",
                schema: "flowbit",
                table: "sequence_flow_occurrences",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<JsonDocument>(
                name: "LastActionSharedVariableWritesJson",
                schema: "flowbit",
                table: "sequence_flow_summaries",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<JsonDocument>(
                name: "LastTraversalSharedVariableWritesJson",
                schema: "flowbit",
                table: "sequence_flow_summaries",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<JsonDocument>(
                name: "SharedVariableWritesJson",
                schema: "flowbit",
                table: "instance_history",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<JsonDocument>(
                name: "SharedVariableRevisionsJson",
                schema: "flowbit",
                table: "workflow_job_snapshots",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "shared_variable_clients",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClientId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    DisplayName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Scopes = table.Column<List<string>>(type: "text[]", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    ActiveSecretVersion = table.Column<int>(type: "integer", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedByKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedById = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    UpdatedByKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    UpdatedById = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    RevocationReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shared_variable_clients", x => x.Id);
                    table.CheckConstraint("CK_shared_variable_clients_revision", "\"Revision\" > 0");
                    table.CheckConstraint("CK_shared_variable_clients_revocation_shape", "(\"Status\" = 'revoked' AND \"RevokedAt\" IS NOT NULL) OR (\"Status\" = 'active' AND \"RevokedAt\" IS NULL)");
                    table.CheckConstraint("CK_shared_variable_clients_secret_version", "\"ActiveSecretVersion\" > 0");
                    table.CheckConstraint("CK_shared_variable_clients_status", "\"Status\" IN ('active', 'revoked')");
                });

            migrationBuilder.CreateTable(
                name: "shared_variable_revision_state",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<short>(type: "smallint", nullable: false),
                    LastRevision = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shared_variable_revision_state", x => x.Id);
                    table.CheckConstraint("CK_shared_variable_revision_state_revision", "\"LastRevision\" >= 0");
                    table.CheckConstraint("CK_shared_variable_revision_state_singleton", "\"Id\" = 1");
                });

            migrationBuilder.CreateTable(
                name: "shared_variables",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Key = table.Column<string>(type: "citext", maxLength: 300, nullable: false),
                    DataType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IsArray = table.Column<bool>(type: "boolean", nullable: false),
                    Nullable = table.Column<bool>(type: "boolean", nullable: false),
                    Validation = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CurrentRevision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedByKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedById = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    UpdatedByKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    UpdatedById = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shared_variables", x => x.Id);
                    table.CheckConstraint("CK_shared_variables_archive_shape", "(\"Status\" = 'archived' AND \"ArchivedAt\" IS NOT NULL) OR (\"Status\" = 'active' AND \"ArchivedAt\" IS NULL)");
                    table.CheckConstraint("CK_shared_variables_revision", "\"CurrentRevision\" >= 0");
                    table.CheckConstraint("CK_shared_variables_status", "\"Status\" IN ('active', 'archived')");
                });

            migrationBuilder.CreateTable(
                name: "shared_variable_client_secrets",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClientId = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Algorithm = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Iterations = table.Column<int>(type: "integer", nullable: false),
                    Salt = table.Column<byte[]>(type: "bytea", nullable: false),
                    Digest = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    ValidUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shared_variable_client_secrets", x => x.Id);
                    table.CheckConstraint("CK_shared_variable_client_secrets_digest", "octet_length(\"Digest\") >= 32");
                    table.CheckConstraint("CK_shared_variable_client_secrets_iterations", "\"Iterations\" BETWEEN 100000 AND 2000000");
                    table.CheckConstraint("CK_shared_variable_client_secrets_salt", "octet_length(\"Salt\") >= 16");
                    table.CheckConstraint("CK_shared_variable_client_secrets_version", "\"Version\" > 0");
                    table.ForeignKey(
                        name: "FK_shared_variable_client_secrets_shared_variable_clients_Clie~",
                        column: x => x.ClientId,
                        principalSchema: "flowbit",
                        principalTable: "shared_variable_clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "shared_variable_requests",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CallerKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    CallerId = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false, collation: "C"),
                    RequestId = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false, collation: "C"),
                    Operation = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SharedKey = table.Column<string>(type: "citext", maxLength: 300, nullable: false),
                    RequestHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    SharedVariableId = table.Column<long>(type: "bigint", nullable: false),
                    ResultRevision = table.Column<long>(type: "bigint", nullable: false),
                    ResponseJson = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shared_variable_requests", x => x.Id);
                    table.CheckConstraint("CK_shared_variable_requests_hash", "octet_length(\"RequestHash\") = 32");
                    table.CheckConstraint("CK_shared_variable_requests_revision", "\"ResultRevision\" > 0");
                    table.ForeignKey(
                        name: "FK_shared_variable_requests_shared_variables_SharedVariableId",
                        column: x => x.SharedVariableId,
                        principalSchema: "flowbit",
                        principalTable: "shared_variables",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "workflow_definition_shared_variable_bindings",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WorkflowDefinitionId = table.Column<long>(type: "bigint", nullable: false),
                    SharedVariableId = table.Column<long>(type: "bigint", nullable: false),
                    Alias = table.Column<string>(type: "citext", maxLength: 300, nullable: false),
                    SharedKey = table.Column<string>(type: "citext", maxLength: 300, nullable: false),
                    Access = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_definition_shared_variable_bindings", x => x.Id);
                    table.CheckConstraint("CK_workflow_definition_shared_variable_bindings_access", "\"Access\" IN ('read', 'readWrite')");
                    table.ForeignKey(
                        name: "FK_workflow_definition_shared_variable_bindings_shared_variabl~",
                        column: x => x.SharedVariableId,
                        principalSchema: "flowbit",
                        principalTable: "shared_variables",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workflow_definition_shared_variable_bindings_workflow_defin~",
                        column: x => x.WorkflowDefinitionId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workflow_definition_shared_variable_dependencies",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WorkflowDefinitionId = table.Column<long>(type: "bigint", nullable: false),
                    SharedVariableId = table.Column<long>(type: "bigint", nullable: false),
                    SharedKey = table.Column<string>(type: "citext", maxLength: 300, nullable: false),
                    NodeId = table.Column<int>(type: "integer", nullable: false),
                    NodeExternalId = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_definition_shared_variable_dependencies", x => x.Id);
                    table.CheckConstraint("CK_workflow_definition_shared_variable_dependencies_kind", "\"Kind\" IN ('conditionalCatch')");
                    table.ForeignKey(
                        name: "FK_workflow_definition_shared_variable_dependencies_shared_var~",
                        column: x => x.SharedVariableId,
                        principalSchema: "flowbit",
                        principalTable: "shared_variables",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workflow_definition_shared_variable_dependencies_workflow_d~",
                        column: x => x.WorkflowDefinitionId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "shared_variable_revisions",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SharedVariableId = table.Column<long>(type: "bigint", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    Operation = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ValueChanged = table.Column<bool>(type: "boolean", nullable: false),
                    ValueJson = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    HasValue = table.Column<bool>(type: "boolean", nullable: false),
                    CallerKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CallerId = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RequestId = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true, collation: "C"),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    WorkflowDefinitionId = table.Column<long>(type: "bigint", nullable: true),
                    InstanceId = table.Column<long>(type: "bigint", nullable: true),
                    NodeExecutionId = table.Column<long>(type: "bigint", nullable: true),
                    SourceActionId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shared_variable_revisions", x => x.Id);
                    table.CheckConstraint("CK_shared_variable_revisions_operation", "\"Operation\" IN ('create', 'set', 'deleteValue', 'updateDescription', 'archive', 'reactivate')");
                    table.CheckConstraint("CK_shared_variable_revisions_revision", "\"Revision\" > 0");
                    table.CheckConstraint("CK_shared_variable_revisions_value_shape", "(\"HasValue\" AND \"ValueJson\" IS NOT NULL) OR (NOT \"HasValue\" AND \"ValueJson\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_shared_variable_revisions_node_executions_NodeExecutionId",
                        column: x => x.NodeExecutionId,
                        principalSchema: "flowbit",
                        principalTable: "node_executions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_shared_variable_revisions_shared_variables_SharedVariableId",
                        column: x => x.SharedVariableId,
                        principalSchema: "flowbit",
                        principalTable: "shared_variables",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_shared_variable_revisions_workflow_definitions_WorkflowDefi~",
                        column: x => x.WorkflowDefinitionId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_shared_variable_revisions_workflow_instances_InstanceId",
                        column: x => x.InstanceId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "shared_variable_current_values",
                schema: "flowbit",
                columns: table => new
                {
                    SharedVariableId = table.Column<long>(type: "bigint", nullable: false),
                    SourceRevisionId = table.Column<long>(type: "bigint", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    ValueJson = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    SetAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shared_variable_current_values", x => x.SharedVariableId);
                    table.CheckConstraint("CK_shared_variable_current_values_revision", "\"Revision\" > 0");
                    table.CheckConstraint("CK_shared_variable_current_values_shape", "(\"IsDeleted\" AND \"ValueJson\" IS NULL) OR (NOT \"IsDeleted\" AND \"ValueJson\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_shared_variable_current_values_shared_variables_SharedVaria~",
                        column: x => x.SharedVariableId,
                        principalSchema: "flowbit",
                        principalTable: "shared_variables",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_shared_variable_current_values_shared_variable_revisions_So~",
                        column: x => x.SourceRevisionId,
                        principalSchema: "flowbit",
                        principalTable: "shared_variable_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "shared_variable_wakes",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SharedVariableId = table.Column<long>(type: "bigint", nullable: false),
                    RevisionId = table.Column<long>(type: "bigint", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseGeneration = table.Column<long>(type: "bigint", nullable: false),
                    LeasedBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AvailableAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shared_variable_wakes", x => x.Id);
                    table.CheckConstraint("CK_shared_variable_wakes_attempts", "\"AttemptCount\" >= 0");
                    table.CheckConstraint("CK_shared_variable_wakes_lease_shape", "(\"Status\" = 'leased' AND \"LeaseToken\" IS NOT NULL AND \"LeasedBy\" IS NOT NULL AND \"LeaseExpiresAt\" IS NOT NULL) OR (\"Status\" <> 'leased' AND \"LeaseToken\" IS NULL AND \"LeasedBy\" IS NULL AND \"LeaseExpiresAt\" IS NULL)");
                    table.CheckConstraint("CK_shared_variable_wakes_status", "\"Status\" IN ('pending', 'leased', 'completed', 'failed', 'cancelled')");
                    table.ForeignKey(
                        name: "FK_shared_variable_wakes_shared_variable_revisions_RevisionId",
                        column: x => x.RevisionId,
                        principalSchema: "flowbit",
                        principalTable: "shared_variable_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_shared_variable_wakes_shared_variables_SharedVariableId",
                        column: x => x.SharedVariableId,
                        principalSchema: "flowbit",
                        principalTable: "shared_variables",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "shared_variable_wake_deliveries",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WakeId = table.Column<long>(type: "bigint", nullable: false),
                    InstanceId = table.Column<long>(type: "bigint", nullable: false),
                    WorkflowDefinitionId = table.Column<long>(type: "bigint", nullable: false),
                    TokenId = table.Column<long>(type: "bigint", nullable: false),
                    ActivationId = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeId = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseGeneration = table.Column<long>(type: "bigint", nullable: false),
                    LeasedBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AvailableAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shared_variable_wake_deliveries", x => x.Id);
                    table.CheckConstraint("CK_shared_variable_wake_deliveries_attempts", "\"AttemptCount\" >= 0");
                    table.CheckConstraint("CK_shared_variable_wake_deliveries_lease_shape", "(\"Status\" = 'leased' AND \"LeaseToken\" IS NOT NULL AND \"LeasedBy\" IS NOT NULL AND \"LeaseExpiresAt\" IS NOT NULL) OR (\"Status\" <> 'leased' AND \"LeaseToken\" IS NULL AND \"LeasedBy\" IS NULL AND \"LeaseExpiresAt\" IS NULL)");
                    table.CheckConstraint("CK_shared_variable_wake_deliveries_status", "\"Status\" IN ('pending', 'leased', 'completed', 'failed', 'cancelled')");
                    table.ForeignKey(
                        name: "FK_shared_variable_wake_deliveries_execution_tokens_TokenId",
                        column: x => x.TokenId,
                        principalSchema: "flowbit",
                        principalTable: "execution_tokens",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_shared_variable_wake_deliveries_shared_variable_wakes_WakeId",
                        column: x => x.WakeId,
                        principalSchema: "flowbit",
                        principalTable: "shared_variable_wakes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_shared_variable_wake_deliveries_workflow_definitions_Workfl~",
                        column: x => x.WorkflowDefinitionId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_shared_variable_wake_deliveries_workflow_instances_Instance~",
                        column: x => x.InstanceId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                schema: "flowbit",
                table: "shared_variable_revision_state",
                columns: new[] { "Id", "LastRevision", "UpdatedAt" },
                values: new object[]
                {
                    (short)1,
                    0L,
                    new DateTimeOffset(new DateTime(2026, 8, 24, 0, 0, 0, DateTimeKind.Unspecified), TimeSpan.Zero)
                });

            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_client_secrets_ClientId_Version",
                schema: "flowbit",
                table: "shared_variable_client_secrets",
                columns: new[] { "ClientId", "Version" },
                unique: true);
            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_clients_ClientId",
                schema: "flowbit",
                table: "shared_variable_clients",
                column: "ClientId",
                unique: true);
            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_clients_Status_Id",
                schema: "flowbit",
                table: "shared_variable_clients",
                columns: new[] { "Status", "Id" });
            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_current_values_Revision",
                schema: "flowbit",
                table: "shared_variable_current_values",
                column: "Revision");
            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_current_values_SourceRevisionId",
                schema: "flowbit",
                table: "shared_variable_current_values",
                column: "SourceRevisionId",
                unique: true);
            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_current_values_ValueJson_gin",
                schema: "flowbit",
                table: "shared_variable_current_values",
                column: "ValueJson",
                filter: "NOT \"IsDeleted\"")
                .Annotation("Npgsql:IndexMethod", "gin");
            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_requests_CallerKind_CallerId_RequestId",
                schema: "flowbit",
                table: "shared_variable_requests",
                columns: new[] { "CallerKind", "CallerId", "RequestId" },
                unique: true);
            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_requests_SharedVariableId_ResultRevision",
                schema: "flowbit",
                table: "shared_variable_requests",
                columns: new[] { "SharedVariableId", "ResultRevision" });
            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_revisions_InstanceId",
                schema: "flowbit",
                table: "shared_variable_revisions",
                column: "InstanceId");
            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_revisions_NodeExecutionId",
                schema: "flowbit",
                table: "shared_variable_revisions",
                column: "NodeExecutionId");
            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_revisions_Revision",
                schema: "flowbit",
                table: "shared_variable_revisions",
                column: "Revision",
                unique: true);
            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_revisions_SharedVariableId_Revision",
                schema: "flowbit",
                table: "shared_variable_revisions",
                columns: new[] { "SharedVariableId", "Revision" },
                unique: true);
            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_revisions_WorkflowDefinitionId",
                schema: "flowbit",
                table: "shared_variable_revisions",
                column: "WorkflowDefinitionId");
            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_wake_deliveries_InstanceId_Status_Id",
                schema: "flowbit",
                table: "shared_variable_wake_deliveries",
                columns: new[] { "InstanceId", "Status", "Id" });
            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_wake_deliveries_Status_AvailableAt_Id",
                schema: "flowbit",
                table: "shared_variable_wake_deliveries",
                columns: new[] { "Status", "AvailableAt", "Id" });
            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_wake_deliveries_TokenId",
                schema: "flowbit",
                table: "shared_variable_wake_deliveries",
                column: "TokenId");
            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_wake_deliveries_WakeId_TokenId_ActivationId",
                schema: "flowbit",
                table: "shared_variable_wake_deliveries",
                columns: new[] { "WakeId", "TokenId", "ActivationId" },
                unique: true);
            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_wake_deliveries_WorkflowDefinitionId",
                schema: "flowbit",
                table: "shared_variable_wake_deliveries",
                column: "WorkflowDefinitionId");
            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_wakes_RevisionId",
                schema: "flowbit",
                table: "shared_variable_wakes",
                column: "RevisionId",
                unique: true);
            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_wakes_SharedVariableId",
                schema: "flowbit",
                table: "shared_variable_wakes",
                column: "SharedVariableId");
            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_wakes_Status_AvailableAt_Id",
                schema: "flowbit",
                table: "shared_variable_wakes",
                columns: new[] { "Status", "AvailableAt", "Id" });
            migrationBuilder.CreateIndex(
                name: "IX_shared_variables_Key",
                schema: "flowbit",
                table: "shared_variables",
                column: "Key",
                unique: true);
            migrationBuilder.CreateIndex(
                name: "IX_shared_variables_Status_Key",
                schema: "flowbit",
                table: "shared_variables",
                columns: new[] { "Status", "Key" });
            migrationBuilder.CreateIndex(
                name: "IX_workflow_definition_shared_variable_bindings_SharedVariable~",
                schema: "flowbit",
                table: "workflow_definition_shared_variable_bindings",
                columns: new[] { "SharedVariableId", "WorkflowDefinitionId" });
            migrationBuilder.CreateIndex(
                name: "IX_workflow_definition_shared_variable_bindings_WorkflowDefin~1",
                schema: "flowbit",
                table: "workflow_definition_shared_variable_bindings",
                columns: new[] { "WorkflowDefinitionId", "SharedVariableId" },
                unique: true);
            migrationBuilder.CreateIndex(
                name: "IX_workflow_definition_shared_variable_bindings_WorkflowDefini~",
                schema: "flowbit",
                table: "workflow_definition_shared_variable_bindings",
                columns: new[] { "WorkflowDefinitionId", "Alias" },
                unique: true);
            migrationBuilder.CreateIndex(
                name: "IX_workflow_definition_shared_variable_dependencies_SharedVari~",
                schema: "flowbit",
                table: "workflow_definition_shared_variable_dependencies",
                columns: new[] { "SharedVariableId", "WorkflowDefinitionId", "NodeId" });
            migrationBuilder.CreateIndex(
                name: "IX_workflow_definition_shared_variable_dependencies_WorkflowDe~",
                schema: "flowbit",
                table: "workflow_definition_shared_variable_dependencies",
                columns: new[] { "WorkflowDefinitionId", "NodeId", "SharedVariableId", "Kind" },
                unique: true);

            migrationBuilder.Sql("""
                WITH desired("Namespace", "Key", "FullKey", "Value", "Description") AS
                (
                    VALUES
                        (
                            'SharedVariables',
                            'RequiredRole',
                            'SharedVariables.RequiredRole',
                            'admin',
                            'Comma-separated roles allowed to administer deployment-wide shared variables and API clients. Missing or blank values default to admin.'
                        )
                )
                INSERT INTO flowbit.engine_settings
                    ("Namespace", "Key", "Value", "Description", "CreatedAt", "UpdatedAt")
                SELECT
                    desired."Namespace",
                    desired."Key",
                    desired."Value",
                    desired."Description",
                    CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP
                FROM desired
                WHERE NOT EXISTS
                (
                    SELECT 1
                    FROM flowbit.engine_settings AS existing
                    WHERE
                        (existing."Namespace" = desired."Namespace"
                         AND existing."Key" = desired."Key")
                        OR
                        (BTRIM(COALESCE(existing."Namespace", '')) = ''
                         AND existing."Key" = desired."FullKey")
                )
                ON CONFLICT ("Namespace", "Key") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM flowbit.engine_settings
                WHERE "Namespace" = 'SharedVariables'
                  AND "Key" = 'RequiredRole'
                  AND "Value" = 'admin'
                  AND "Description" = 'Comma-separated roles allowed to administer deployment-wide shared variables and API clients. Missing or blank values default to admin.';
                """);

            migrationBuilder.DropTable(name: "shared_variable_client_secrets", schema: "flowbit");
            migrationBuilder.DropTable(name: "shared_variable_current_values", schema: "flowbit");
            migrationBuilder.DropTable(name: "shared_variable_requests", schema: "flowbit");
            migrationBuilder.DropTable(name: "shared_variable_revision_state", schema: "flowbit");
            migrationBuilder.DropTable(name: "shared_variable_wake_deliveries", schema: "flowbit");
            migrationBuilder.DropTable(name: "workflow_definition_shared_variable_bindings", schema: "flowbit");
            migrationBuilder.DropTable(name: "workflow_definition_shared_variable_dependencies", schema: "flowbit");
            migrationBuilder.DropTable(name: "shared_variable_clients", schema: "flowbit");
            migrationBuilder.DropTable(name: "shared_variable_wakes", schema: "flowbit");
            migrationBuilder.DropTable(name: "shared_variable_revisions", schema: "flowbit");
            migrationBuilder.DropTable(name: "shared_variables", schema: "flowbit");

            migrationBuilder.DropColumn(
                name: "SharedVariableRevisionsJson",
                schema: "flowbit",
                table: "workflow_job_snapshots");

            migrationBuilder.DropColumn(
                name: "SharedVariableWritesJson",
                schema: "flowbit",
                table: "sequence_flow_occurrences");

            migrationBuilder.DropColumn(
                name: "LastActionSharedVariableWritesJson",
                schema: "flowbit",
                table: "sequence_flow_summaries");

            migrationBuilder.DropColumn(
                name: "LastTraversalSharedVariableWritesJson",
                schema: "flowbit",
                table: "sequence_flow_summaries");

            migrationBuilder.DropColumn(
                name: "SharedVariableWritesJson",
                schema: "flowbit",
                table: "instance_history");
        }
    }
}
