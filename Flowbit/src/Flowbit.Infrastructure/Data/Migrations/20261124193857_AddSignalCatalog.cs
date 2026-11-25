using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Adds the signal catalog (EVENT-04F): entries with a payload contract and throw permissions,
    /// and the signal names each workflow definition version uses. The references are backfilled
    /// from every stored definition, with the five signal event types and names that pass the
    /// name rule, as <c>WorkflowDefinitionRepository.AddAsync</c> writes them for a new version.
    /// The seed adds Signals.CatalogMode without overwriting a row an operator created. Nothing
    /// behaves differently until an entry exists or the mode is set to required.
    /// </summary>
    public partial class AddSignalCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "signal_catalog_entries",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Fields = table.Column<JsonElement>(type: "jsonb", nullable: false),
                    ThrowRoles = table.Column<JsonElement>(type: "jsonb", nullable: false),
                    AllowClients = table.Column<bool>(type: "boolean", nullable: false),
                    AllowWorkflows = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_signal_catalog_entries", x => x.Id);
                    table.CheckConstraint("CK_signal_catalog_entries_name", "\"Name\" ~ '^[a-z0-9][a-z0-9._-]{0,99}$'");
                    table.CheckConstraint("CK_signal_catalog_entries_shape", "((\"Status\" = 'active' AND \"ArchivedAt\" IS NULL) OR (\"Status\" = 'archived' AND \"ArchivedAt\" IS NOT NULL)) AND jsonb_typeof(\"Fields\") = 'array' AND jsonb_typeof(\"ThrowRoles\") = 'array'");
                });

            migrationBuilder.CreateTable(
                name: "workflow_definition_signal_names",
                schema: "flowbit",
                columns: table => new
                {
                    WorkflowDefinitionId = table.Column<long>(type: "bigint", nullable: false),
                    NodeId = table.Column<int>(type: "integer", nullable: false),
                    SignalName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    Usage = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_definition_signal_names", x => new { x.WorkflowDefinitionId, x.NodeId });
                    table.ForeignKey(
                        name: "FK_workflow_definition_signal_names_workflow_definitions_Workf~",
                        column: x => x.WorkflowDefinitionId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_signal_catalog_entries_Name",
                schema: "flowbit",
                table: "signal_catalog_entries",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_definition_signal_names_SignalName",
                schema: "flowbit",
                table: "workflow_definition_signal_names",
                column: "SignalName");

            migrationBuilder.Sql(BackfillSql);

            migrationBuilder.Sql("""
                INSERT INTO flowbit.engine_settings
                    ("Namespace", "Key", "Value", "Description", "CreatedAt", "UpdatedAt")
                SELECT 'Signals', 'CatalogMode', 'optional',
                    'Whether the signal catalog is required. optional (the default; also missing, blank, or any other value) lets names without a catalog entry be used as before. required refuses publishing a definition, and throwing through the API, with a signal name that has no entry; drafts can still be saved and published definitions keep running.',
                    CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
                WHERE NOT EXISTS
                (
                    SELECT 1
                    FROM flowbit.engine_settings
                    WHERE
                        ("Namespace" = 'Signals' AND "Key" = 'CatalogMode')
                        OR
                        (BTRIM(COALESCE("Namespace", '')) = '' AND "Key" = 'Signals.CatalogMode')
                )
                ON CONFLICT ("Namespace", "Key") DO NOTHING;
                """);
        }

        /// <summary>
        /// One row per signal event with a valid name. A definition inserted without validation
        /// can repeat a node ID, so a repeat keeps one row.
        /// </summary>
        internal const string BackfillSql = """
            INSERT INTO flowbit.workflow_definition_signal_names
                ("WorkflowDefinitionId", "NodeId", "SignalName", "Usage")
            SELECT
                definition."Id",
                (node ->> 'id')::integer,
                node -> 'signal' ->> 'name',
                CASE node ->> 'type'
                    WHEN 'intermediateSignalCatchEvent' THEN 'catch'
                    WHEN 'signalBoundaryEvent' THEN 'boundary'
                    WHEN 'signalStartEvent' THEN 'start'
                    WHEN 'intermediateSignalThrowEvent' THEN 'throw'
                    ELSE 'end'
                END
            FROM flowbit.workflow_definitions definition
            CROSS JOIN LATERAL jsonb_array_elements(
                CASE WHEN jsonb_typeof(definition."Definition" -> 'flowNodes') = 'array'
                    THEN definition."Definition" -> 'flowNodes'
                    ELSE '[]'::jsonb
                END) node
            WHERE jsonb_typeof(node) = 'object'
              AND node ->> 'type' IN (
                  'intermediateSignalCatchEvent',
                  'signalBoundaryEvent',
                  'signalStartEvent',
                  'intermediateSignalThrowEvent',
                  'signalEndEvent')
              AND jsonb_typeof(node -> 'id') = 'number'
              AND (node ->> 'id') ~ '^-?[0-9]{1,9}$'
              AND jsonb_typeof(node -> 'signal') = 'object'
              AND jsonb_typeof(node -> 'signal' -> 'name') = 'string'
              AND (node -> 'signal' ->> 'name') COLLATE "C" ~ '^[a-z0-9][a-z0-9._-]{0,99}$'
            ON CONFLICT ("WorkflowDefinitionId", "NodeId") DO NOTHING;
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dropping the entries would silently end the roles and switches that narrow who may
            // throw a name, so an operator deletes them with SQL first, knowing that.
            migrationBuilder.Sql("""
                DO $guard$
                BEGIN
                    IF EXISTS (SELECT 1 FROM flowbit.signal_catalog_entries) THEN
                        RAISE EXCEPTION 'Signal catalog entries exist. Delete them before reverting AddSignalCatalog; their throw roles and switches then stop applying.';
                    END IF;
                END
                $guard$;
                """);

            migrationBuilder.Sql("""
                DELETE FROM flowbit.engine_settings
                WHERE "Namespace" = 'Signals' AND "Key" = 'CatalogMode' AND "Value" = 'optional';
                """);

            migrationBuilder.DropTable(
                name: "signal_catalog_entries",
                schema: "flowbit");

            migrationBuilder.DropTable(
                name: "workflow_definition_signal_names",
                schema: "flowbit");
        }
    }
}
