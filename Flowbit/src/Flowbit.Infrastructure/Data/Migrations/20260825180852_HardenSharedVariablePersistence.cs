using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class HardenSharedVariablePersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_shared_variable_current_values_shared_variable_revisions_So~",
                schema: "flowbit",
                table: "shared_variable_current_values");

            migrationBuilder.DropForeignKey(
                name: "FK_shared_variable_wakes_shared_variable_revisions_RevisionId",
                schema: "flowbit",
                table: "shared_variable_wakes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_shared_variable_wakes_status",
                schema: "flowbit",
                table: "shared_variable_wakes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_shared_variable_wakes_lease_shape",
                schema: "flowbit",
                table: "shared_variable_wakes");

            migrationBuilder.DropIndex(
                name: "IX_shared_variable_wake_deliveries_TokenId",
                schema: "flowbit",
                table: "shared_variable_wake_deliveries");

            migrationBuilder.DropCheckConstraint(
                name: "CK_shared_variable_wake_deliveries_status",
                schema: "flowbit",
                table: "shared_variable_wake_deliveries");

            migrationBuilder.DropCheckConstraint(
                name: "CK_shared_variable_wake_deliveries_lease_shape",
                schema: "flowbit",
                table: "shared_variable_wake_deliveries");

            migrationBuilder.AddColumn<JsonDocument>(
                name: "SharedOutputValueVersionsJson",
                schema: "flowbit",
                table: "workflow_job_snapshots",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ValueRevision",
                schema: "flowbit",
                table: "shared_variables",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "ExpansionCursorTokenId",
                schema: "flowbit",
                table: "shared_variable_wakes",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "MaxAttempts",
                schema: "flowbit",
                table: "shared_variable_wakes",
                type: "integer",
                nullable: false,
                defaultValue: 25);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "HeartbeatAt",
                schema: "flowbit",
                table: "shared_variable_wakes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxAttempts",
                schema: "flowbit",
                table: "shared_variable_wake_deliveries",
                type: "integer",
                nullable: false,
                defaultValue: 25);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "HeartbeatAt",
                schema: "flowbit",
                table: "shared_variable_wake_deliveries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AllocatorMode",
                schema: "flowbit",
                table: "shared_variable_revision_state",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "legacy");

            // Repair the effective-value projection before tightening its
            // correlation to the authoritative revision ledger. Historical
            // metadata-only revisions must not become value fences.
            migrationBuilder.Sql(
                """
                WITH latest_value_revision AS (
                    SELECT DISTINCT ON (revision."SharedVariableId")
                        revision."Id",
                        revision."SharedVariableId",
                        revision."Revision",
                        revision."HasValue",
                        revision."ValueJson",
                        revision."CreatedAt"
                    FROM flowbit.shared_variable_revisions AS revision
                    WHERE revision."ValueChanged"
                    ORDER BY revision."SharedVariableId", revision."Revision" DESC, revision."Id" DESC
                )
                DELETE FROM flowbit.shared_variable_current_values AS current_value
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM latest_value_revision AS latest
                    WHERE latest."SharedVariableId" = current_value."SharedVariableId");

                WITH latest_value_revision AS (
                    SELECT DISTINCT ON (revision."SharedVariableId")
                        revision."Id",
                        revision."SharedVariableId",
                        revision."Revision",
                        revision."HasValue",
                        revision."ValueJson",
                        revision."CreatedAt"
                    FROM flowbit.shared_variable_revisions AS revision
                    WHERE revision."ValueChanged"
                    ORDER BY revision."SharedVariableId", revision."Revision" DESC, revision."Id" DESC
                )
                INSERT INTO flowbit.shared_variable_current_values (
                    "SharedVariableId",
                    "SourceRevisionId",
                    "Revision",
                    "ValueJson",
                    "IsDeleted",
                    "SetAt")
                SELECT
                    latest."SharedVariableId",
                    latest."Id",
                    latest."Revision",
                    CASE WHEN latest."HasValue" THEN latest."ValueJson" ELSE NULL END,
                    NOT latest."HasValue",
                    latest."CreatedAt"
                FROM latest_value_revision AS latest
                ON CONFLICT ("SharedVariableId") DO UPDATE SET
                    "SourceRevisionId" = EXCLUDED."SourceRevisionId",
                    "Revision" = EXCLUDED."Revision",
                    "ValueJson" = EXCLUDED."ValueJson",
                    "IsDeleted" = EXCLUDED."IsDeleted",
                    "SetAt" = EXCLUDED."SetAt";

                UPDATE flowbit.shared_variables AS variable
                SET "ValueRevision" = COALESCE((
                    SELECT revision."Revision"
                    FROM flowbit.shared_variable_revisions AS revision
                    WHERE revision."SharedVariableId" = variable."Id"
                      AND revision."ValueChanged"
                    ORDER BY revision."Revision" DESC, revision."Id" DESC
                    LIMIT 1), 0);

                UPDATE flowbit.shared_variable_wakes
                SET "MaxAttempts" = GREATEST(25, "AttemptCount", 1),
                    "HeartbeatAt" = CASE
                        WHEN "Status" = 'leased'
                            THEN COALESCE("UpdatedAt", "CreatedAt", clock_timestamp())
                        ELSE NULL
                    END,
                    "CompletedAt" = CASE
                        WHEN "Status" IN ('completed', 'cancelled')
                            THEN COALESCE("CompletedAt", "UpdatedAt", "CreatedAt", clock_timestamp())
                        ELSE NULL
                    END;

                UPDATE flowbit.shared_variable_wake_deliveries
                SET "MaxAttempts" = GREATEST(25, "AttemptCount", 1),
                    "HeartbeatAt" = CASE
                        WHEN "Status" = 'leased'
                            THEN COALESCE("UpdatedAt", "CreatedAt", clock_timestamp())
                        ELSE NULL
                    END,
                    "CompletedAt" = CASE
                        WHEN "Status" IN ('completed', 'cancelled')
                            THEN COALESCE("CompletedAt", "UpdatedAt", "CreatedAt", clock_timestamp())
                        ELSE NULL
                    END;
                """);

            // Mixed-version writers update the current-value projection but do
            // not know about ValueRevision. Keep the new fence authoritative
            // during the rolling API/worker replacement. Legacy writers also
            // rewrite the projection for identical values, so consult the
            // correlated ledger row and advance the value fence only for an
            // effective value-state change.
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION flowbit.sync_shared_variable_value_revision()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                DECLARE
                    value_changed boolean;
                BEGIN
                    SELECT revision."ValueChanged"
                    INTO value_changed
                    FROM flowbit.shared_variable_revisions AS revision
                    WHERE revision."Id" = NEW."SourceRevisionId"
                      AND revision."SharedVariableId" = NEW."SharedVariableId"
                      AND revision."Revision" = NEW."Revision";

                    IF value_changed IS NULL THEN
                        RAISE EXCEPTION
                            'Shared-variable current projection references a missing revision ledger row';
                    END IF;

                    UPDATE flowbit.shared_variables
                    SET "CurrentRevision" = GREATEST("CurrentRevision", NEW."Revision"),
                        "ValueRevision" = CASE
                            WHEN value_changed
                                THEN GREATEST("ValueRevision", NEW."Revision")
                            ELSE "ValueRevision"
                        END
                    WHERE "Id" = NEW."SharedVariableId"
                      AND ("CurrentRevision" < NEW."Revision"
                           OR (value_changed AND "ValueRevision" < NEW."Revision"));
                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER TR_shared_variable_current_values_value_revision
                AFTER INSERT OR UPDATE OF "Revision"
                ON flowbit.shared_variable_current_values
                FOR EACH ROW
                EXECUTE FUNCTION flowbit.sync_shared_variable_value_revision();
                """);

            // Legacy API replicas enqueue new wake rows with application-clock
            // timestamps. During the supported mixed-writer stage, normalize
            // only the unmistakable initial-insert shape; retries are updates
            // and any imported/recovery row with prior attempts or lease
            // generations keeps its authored schedule.
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION flowbit.stamp_initial_shared_variable_wake_clock()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                DECLARE
                    database_now timestamp with time zone;
                BEGIN
                    IF NEW."Status" = 'pending'
                       AND NEW."AttemptCount" = 0
                       AND NEW."LeaseGeneration" = 0 THEN
                        database_now := clock_timestamp();
                        NEW."AvailableAt" := database_now;
                        NEW."CreatedAt" := database_now;
                        NEW."UpdatedAt" := database_now;
                    END IF;
                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER TR_shared_variable_wakes_initial_insert_clock
                BEFORE INSERT
                ON flowbit.shared_variable_wakes
                FOR EACH ROW
                EXECUTE FUNCTION flowbit.stamp_initial_shared_variable_wake_clock();
                """);

            // Stage 1 remains compatible with legacy binaries: both old direct
            // singleton updates and the new allocator function serialize on the
            // same row. The sequence is seeded now but is not used until an
            // operator explicitly invokes the cutover function after every
            // writer replica has been upgraded.
            migrationBuilder.Sql(
                """
                CREATE SEQUENCE flowbit.shared_variable_revision_seq
                    AS bigint
                    MINVALUE 1
                    NO CYCLE;

                WITH revision_floor AS (
                    SELECT GREATEST(
                        COALESCE((SELECT "LastRevision"
                                  FROM flowbit.shared_variable_revision_state
                                  WHERE "Id" = 1), 0),
                        COALESCE((SELECT MAX("Revision")
                                  FROM flowbit.shared_variable_revisions), 0),
                        COALESCE((SELECT MAX("CurrentRevision")
                                  FROM flowbit.shared_variables), 0)) AS value
                )
                SELECT setval(
                    'flowbit.shared_variable_revision_seq'::regclass,
                    GREATEST(value, 1),
                    value > 0)
                FROM revision_floor;

                CREATE OR REPLACE FUNCTION flowbit.next_shared_variable_revision()
                RETURNS bigint
                LANGUAGE plpgsql
                VOLATILE
                AS $function$
                DECLARE
                    allocated_revision bigint;
                    allocator_mode character varying(16);
                BEGIN
                    SELECT state."AllocatorMode"
                    INTO allocator_mode
                    FROM flowbit.shared_variable_revision_state AS state
                    WHERE state."Id" = 1;

                    IF allocator_mode = 'sequence' THEN
                        RETURN nextval('flowbit.shared_variable_revision_seq'::regclass);
                    END IF;

                    UPDATE flowbit.shared_variable_revision_state
                    SET "LastRevision" = "LastRevision" + 1,
                        "UpdatedAt" = clock_timestamp()
                    WHERE "Id" = 1
                      AND "AllocatorMode" = 'legacy'
                    RETURNING "LastRevision" INTO allocated_revision;

                    IF allocated_revision IS NOT NULL THEN
                        RETURN allocated_revision;
                    END IF;

                    -- A concurrent cutover may have changed the mode while this
                    -- call waited for the singleton row lock.
                    RETURN nextval('flowbit.shared_variable_revision_seq'::regclass);
                END;
                $function$;

                CREATE OR REPLACE FUNCTION flowbit.cutover_shared_variable_revision_sequence()
                RETURNS bigint
                LANGUAGE plpgsql
                VOLATILE
                AS $function$
                DECLARE
                    allocator_mode character varying(16);
                    revision_floor bigint;
                BEGIN
                    SELECT state."AllocatorMode"
                    INTO allocator_mode
                    FROM flowbit.shared_variable_revision_state AS state
                    WHERE state."Id" = 1
                    FOR UPDATE;

                    IF allocator_mode = 'sequence' THEN
                        RETURN (SELECT "LastRevision"
                                FROM flowbit.shared_variable_revision_state
                                WHERE "Id" = 1);
                    END IF;

                    SELECT GREATEST(
                        COALESCE((SELECT "LastRevision"
                                  FROM flowbit.shared_variable_revision_state
                                  WHERE "Id" = 1), 0),
                        COALESCE((SELECT MAX("Revision")
                                  FROM flowbit.shared_variable_revisions), 0),
                        COALESCE((SELECT MAX("CurrentRevision")
                                  FROM flowbit.shared_variables), 0))
                    INTO revision_floor;

                    PERFORM setval(
                        'flowbit.shared_variable_revision_seq'::regclass,
                        GREATEST(revision_floor, 1),
                        revision_floor > 0);

                    UPDATE flowbit.shared_variable_revision_state
                    SET "AllocatorMode" = 'sequence',
                        "LastRevision" = revision_floor,
                        "UpdatedAt" = clock_timestamp()
                    WHERE "Id" = 1;

                    -- Replace the compatibility allocator in the same cutover
                    -- transaction. Subsequent new-writer calls are a pure
                    -- sequence nextval and no longer read the singleton.
                    EXECUTE $ddl$
                        CREATE OR REPLACE FUNCTION flowbit.next_shared_variable_revision()
                        RETURNS bigint
                        LANGUAGE sql
                        VOLATILE
                        AS $body$
                            SELECT nextval('flowbit.shared_variable_revision_seq'::regclass);
                        $body$
                    $ddl$;

                    RETURN revision_floor;
                END;
                $function$;

                CREATE OR REPLACE FUNCTION flowbit.guard_legacy_shared_variable_revision_update()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    IF OLD."AllocatorMode" = 'sequence'
                       AND NEW."LastRevision" IS DISTINCT FROM OLD."LastRevision" THEN
                        RAISE EXCEPTION
                            'Legacy shared-variable revision allocation is disabled after sequence cutover'
                            USING ERRCODE = '55000';
                    END IF;
                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER TR_shared_variable_revision_state_guard_legacy_update
                BEFORE UPDATE OF "LastRevision"
                ON flowbit.shared_variable_revision_state
                FOR EACH ROW
                EXECUTE FUNCTION flowbit.guard_legacy_shared_variable_revision_update();
                """);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_shared_variable_revisions_Id_SharedVariableId_Revision",
                schema: "flowbit",
                table: "shared_variable_revisions",
                columns: new[] { "Id", "SharedVariableId", "Revision" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_shared_variable_revisions_SharedVariableId_Revision",
                schema: "flowbit",
                table: "shared_variable_revisions",
                columns: new[] { "SharedVariableId", "Revision" });

            migrationBuilder.CreateTable(
                name: "shared_variable_wake_incidents",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WorkKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    WakeId = table.Column<long>(type: "bigint", nullable: true),
                    DeliveryId = table.Column<long>(type: "bigint", nullable: true),
                    OriginalWakeId = table.Column<long>(type: "bigint", nullable: false),
                    OriginalDeliveryId = table.Column<long>(type: "bigint", nullable: true),
                    SharedVariableId = table.Column<long>(type: "bigint", nullable: false),
                    SharedKey = table.Column<string>(type: "citext", maxLength: 300, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    InstanceId = table.Column<long>(type: "bigint", nullable: true),
                    WorkflowDefinitionId = table.Column<long>(type: "bigint", nullable: true),
                    TokenId = table.Column<long>(type: "bigint", nullable: true),
                    ActivationId = table.Column<Guid>(type: "uuid", nullable: true),
                    NodeId = table.Column<int>(type: "integer", nullable: true),
                    Type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Details = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ResolutionReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ResolvedBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shared_variable_wake_incidents", x => x.Id);
                    table.CheckConstraint("CK_shared_variable_wake_incidents_resolution_shape", "(\"Status\" = 'open' AND \"ResolvedAt\" IS NULL AND \"ResolvedBy\" IS NULL AND \"ResolutionReason\" IS NULL) OR (\"Status\" = 'resolved' AND \"ResolvedAt\" IS NOT NULL AND \"ResolvedBy\" IS NOT NULL)");
                    table.CheckConstraint("CK_shared_variable_wake_incidents_status", "\"Status\" IN ('open', 'resolved')");
                    table.CheckConstraint("CK_shared_variable_wake_incidents_target_shape", "(\"WorkKind\" = 'expansion' AND \"OriginalDeliveryId\" IS NULL AND \"DeliveryId\" IS NULL AND (\"Status\" <> 'open' OR \"WakeId\" IS NOT NULL)) OR (\"WorkKind\" = 'delivery' AND \"OriginalDeliveryId\" IS NOT NULL AND (\"Status\" <> 'open' OR \"DeliveryId\" IS NOT NULL))");
                    table.CheckConstraint("CK_shared_variable_wake_incidents_work_kind", "\"WorkKind\" IN ('expansion', 'delivery')");
                    table.ForeignKey(
                        name: "FK_shared_variable_wake_incidents_shared_variable_wake_deliver~",
                        column: x => x.DeliveryId,
                        principalSchema: "flowbit",
                        principalTable: "shared_variable_wake_deliveries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_shared_variable_wake_incidents_shared_variable_wakes_WakeId",
                        column: x => x.WakeId,
                        principalSchema: "flowbit",
                        principalTable: "shared_variable_wakes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_shared_variable_wake_incidents_shared_variables_SharedVaria~",
                        column: x => x.SharedVariableId,
                        principalSchema: "flowbit",
                        principalTable: "shared_variables",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // Convert legacy terminal failures into durable operator incidents.
            // The original ids and bounded diagnostics remain queryable even
            // after resolved outbox rows are later cleaned up.
            migrationBuilder.Sql(
                """
                INSERT INTO flowbit.shared_variable_wake_incidents (
                    "WorkKind", "WakeId", "DeliveryId", "OriginalWakeId", "OriginalDeliveryId",
                    "SharedVariableId", "SharedKey", "Revision", "InstanceId",
                    "WorkflowDefinitionId", "TokenId", "ActivationId", "NodeId",
                    "Type", "Status", "Summary", "Details", "ResolutionReason",
                    "ResolvedBy", "CreatedAt", "UpdatedAt", "ResolvedAt")
                SELECT
                    'expansion', wake."Id", NULL, wake."Id", NULL,
                    wake."SharedVariableId", variable."Key", wake."Revision", NULL,
                    NULL, NULL, NULL, NULL,
                    'legacyFailure', 'open',
                    'Legacy shared-variable wake expansion failure.',
                    left(NULLIF(wake."LastError", ''), 4000), NULL,
                    NULL, wake."UpdatedAt", wake."UpdatedAt", NULL
                FROM flowbit.shared_variable_wakes AS wake
                JOIN flowbit.shared_variables AS variable
                  ON variable."Id" = wake."SharedVariableId"
                WHERE wake."Status" = 'failed';

                INSERT INTO flowbit.shared_variable_wake_incidents (
                    "WorkKind", "WakeId", "DeliveryId", "OriginalWakeId", "OriginalDeliveryId",
                    "SharedVariableId", "SharedKey", "Revision", "InstanceId",
                    "WorkflowDefinitionId", "TokenId", "ActivationId", "NodeId",
                    "Type", "Status", "Summary", "Details", "ResolutionReason",
                    "ResolvedBy", "CreatedAt", "UpdatedAt", "ResolvedAt")
                SELECT
                    'delivery', wake."Id", delivery."Id", wake."Id", delivery."Id",
                    wake."SharedVariableId", variable."Key", wake."Revision", delivery."InstanceId",
                    delivery."WorkflowDefinitionId", delivery."TokenId", delivery."ActivationId", delivery."NodeId",
                    'legacyFailure', 'open',
                    'Legacy shared-variable wake delivery failure.',
                    left(NULLIF(delivery."LastError", ''), 4000), NULL,
                    NULL, delivery."UpdatedAt", delivery."UpdatedAt", NULL
                FROM flowbit.shared_variable_wake_deliveries AS delivery
                JOIN flowbit.shared_variable_wakes AS wake
                  ON wake."Id" = delivery."WakeId"
                JOIN flowbit.shared_variables AS variable
                  ON variable."Id" = wake."SharedVariableId"
                WHERE delivery."Status" = 'failed';

                UPDATE flowbit.shared_variable_wakes
                SET "Status" = 'incident',
                    "LeaseToken" = NULL,
                    "LeasedBy" = NULL,
                    "LeaseExpiresAt" = NULL,
                    "CompletedAt" = NULL
                WHERE "Status" = 'failed';

                UPDATE flowbit.shared_variable_wake_deliveries
                SET "Status" = 'incident',
                    "LeaseToken" = NULL,
                    "LeasedBy" = NULL,
                    "LeaseExpiresAt" = NULL,
                    "CompletedAt" = NULL
                WHERE "Status" = 'failed';
                """);

            migrationBuilder.UpdateData(
                schema: "flowbit",
                table: "shared_variable_revision_state",
                keyColumn: "Id",
                keyValue: (short)1,
                column: "AllocatorMode",
                value: "legacy");

            migrationBuilder.AddCheckConstraint(
                name: "CK_shared_variables_value_revision",
                schema: "flowbit",
                table: "shared_variables",
                sql: "\"ValueRevision\" >= 0 AND \"ValueRevision\" <= \"CurrentRevision\"");

            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_wakes_expired_lease",
                schema: "flowbit",
                table: "shared_variable_wakes",
                columns: new[] { "LeaseExpiresAt", "Revision", "Id" },
                filter: "\"Status\" = 'leased' AND \"LeaseExpiresAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_wakes_pending_available",
                schema: "flowbit",
                table: "shared_variable_wakes",
                columns: new[] { "AvailableAt", "Revision", "Id" },
                filter: "\"Status\" = 'pending'");

            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_wakes_RevisionId_SharedVariableId_Revision",
                schema: "flowbit",
                table: "shared_variable_wakes",
                columns: new[] { "RevisionId", "SharedVariableId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_wakes_terminal_cleanup",
                schema: "flowbit",
                table: "shared_variable_wakes",
                columns: new[] { "CompletedAt", "Id" },
                filter: "\"Status\" IN ('completed', 'cancelled')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_shared_variable_wakes_completion_shape",
                schema: "flowbit",
                table: "shared_variable_wakes",
                sql: "(\"Status\" IN ('completed', 'cancelled') AND \"CompletedAt\" IS NOT NULL) OR (\"Status\" NOT IN ('completed', 'cancelled') AND \"CompletedAt\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_shared_variable_wakes_cursor",
                schema: "flowbit",
                table: "shared_variable_wakes",
                sql: "\"ExpansionCursorTokenId\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_shared_variable_wakes_max_attempts",
                schema: "flowbit",
                table: "shared_variable_wakes",
                sql: "\"MaxAttempts\" > 0 AND \"AttemptCount\" <= \"MaxAttempts\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_shared_variable_wakes_lease_shape",
                schema: "flowbit",
                table: "shared_variable_wakes",
                sql: "(\"Status\" = 'leased' AND \"LeaseToken\" IS NOT NULL AND \"LeasedBy\" IS NOT NULL AND \"LeaseExpiresAt\" IS NOT NULL AND \"HeartbeatAt\" IS NOT NULL) OR (\"Status\" <> 'leased' AND \"LeaseToken\" IS NULL AND \"LeasedBy\" IS NULL AND \"LeaseExpiresAt\" IS NULL AND \"HeartbeatAt\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_shared_variable_wakes_status",
                schema: "flowbit",
                table: "shared_variable_wakes",
                sql: "\"Status\" IN ('pending', 'leased', 'completed', 'failed', 'cancelled', 'incident')");

            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_wake_deliveries_expired_lease",
                schema: "flowbit",
                table: "shared_variable_wake_deliveries",
                columns: new[] { "LeaseExpiresAt", "Id" },
                filter: "\"Status\" = 'leased' AND \"LeaseExpiresAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_wake_deliveries_open_predecessor",
                schema: "flowbit",
                table: "shared_variable_wake_deliveries",
                columns: new[] { "TokenId", "ActivationId", "WakeId", "Id" },
                filter: "\"Status\" IN ('pending', 'leased')");

            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_wake_deliveries_pending_available",
                schema: "flowbit",
                table: "shared_variable_wake_deliveries",
                columns: new[] { "AvailableAt", "Id" },
                filter: "\"Status\" = 'pending'");

            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_wake_deliveries_terminal_cleanup",
                schema: "flowbit",
                table: "shared_variable_wake_deliveries",
                columns: new[] { "CompletedAt", "Id" },
                filter: "\"Status\" IN ('completed', 'cancelled')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_shared_variable_wake_deliveries_completion_shape",
                schema: "flowbit",
                table: "shared_variable_wake_deliveries",
                sql: "(\"Status\" IN ('completed', 'cancelled') AND \"CompletedAt\" IS NOT NULL) OR (\"Status\" NOT IN ('completed', 'cancelled') AND \"CompletedAt\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_shared_variable_wake_deliveries_max_attempts",
                schema: "flowbit",
                table: "shared_variable_wake_deliveries",
                sql: "\"MaxAttempts\" > 0 AND \"AttemptCount\" <= \"MaxAttempts\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_shared_variable_wake_deliveries_lease_shape",
                schema: "flowbit",
                table: "shared_variable_wake_deliveries",
                sql: "(\"Status\" = 'leased' AND \"LeaseToken\" IS NOT NULL AND \"LeasedBy\" IS NOT NULL AND \"LeaseExpiresAt\" IS NOT NULL AND \"HeartbeatAt\" IS NOT NULL) OR (\"Status\" <> 'leased' AND \"LeaseToken\" IS NULL AND \"LeasedBy\" IS NULL AND \"LeaseExpiresAt\" IS NULL AND \"HeartbeatAt\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_shared_variable_wake_deliveries_status",
                schema: "flowbit",
                table: "shared_variable_wake_deliveries",
                sql: "\"Status\" IN ('pending', 'leased', 'completed', 'failed', 'cancelled', 'incident')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_shared_variable_revision_state_allocator_mode",
                schema: "flowbit",
                table: "shared_variable_revision_state",
                sql: "\"AllocatorMode\" IN ('legacy', 'sequence')");

            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_current_values_SourceRevisionId_SharedVaria~",
                schema: "flowbit",
                table: "shared_variable_current_values",
                columns: new[] { "SourceRevisionId", "SharedVariableId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_wake_incidents_DeliveryId",
                schema: "flowbit",
                table: "shared_variable_wake_incidents",
                column: "DeliveryId",
                unique: true,
                filter: "\"Status\" = 'open' AND \"WorkKind\" = 'delivery'");

            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_wake_incidents_OriginalWakeId_OriginalDeliv~",
                schema: "flowbit",
                table: "shared_variable_wake_incidents",
                columns: new[] { "OriginalWakeId", "OriginalDeliveryId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_wake_incidents_SharedVariableId",
                schema: "flowbit",
                table: "shared_variable_wake_incidents",
                column: "SharedVariableId");

            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_wake_incidents_Status_UpdatedAt_Id",
                schema: "flowbit",
                table: "shared_variable_wake_incidents",
                columns: new[] { "Status", "UpdatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_wake_incidents_WakeId",
                schema: "flowbit",
                table: "shared_variable_wake_incidents",
                column: "WakeId",
                unique: true,
                filter: "\"Status\" = 'open' AND \"WorkKind\" = 'expansion'");

            migrationBuilder.AddForeignKey(
                name: "FK_shared_variable_current_values_shared_variable_revisions_So~",
                schema: "flowbit",
                table: "shared_variable_current_values",
                columns: new[] { "SourceRevisionId", "SharedVariableId", "Revision" },
                principalSchema: "flowbit",
                principalTable: "shared_variable_revisions",
                principalColumns: new[] { "Id", "SharedVariableId", "Revision" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_shared_variable_requests_shared_variable_revisions_SharedVa~",
                schema: "flowbit",
                table: "shared_variable_requests",
                columns: new[] { "SharedVariableId", "ResultRevision" },
                principalSchema: "flowbit",
                principalTable: "shared_variable_revisions",
                principalColumns: new[] { "SharedVariableId", "Revision" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_shared_variable_wakes_shared_variable_revisions_RevisionId_~",
                schema: "flowbit",
                table: "shared_variable_wakes",
                columns: new[] { "RevisionId", "SharedVariableId", "Revision" },
                principalSchema: "flowbit",
                principalTable: "shared_variable_revisions",
                principalColumns: new[] { "Id", "SharedVariableId", "Revision" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS TR_shared_variable_current_values_value_revision
                    ON flowbit.shared_variable_current_values;
                DROP FUNCTION IF EXISTS flowbit.sync_shared_variable_value_revision();
                DROP TRIGGER IF EXISTS TR_shared_variable_wakes_initial_insert_clock
                    ON flowbit.shared_variable_wakes;
                DROP FUNCTION IF EXISTS flowbit.stamp_initial_shared_variable_wake_clock();
                DROP TRIGGER IF EXISTS TR_shared_variable_revision_state_guard_legacy_update
                    ON flowbit.shared_variable_revision_state;
                DROP FUNCTION IF EXISTS flowbit.guard_legacy_shared_variable_revision_update();

                -- A cut-over sequence can be ahead of both the original
                -- singleton and the committed ledger. Reseed the legacy
                -- allocator before removing the sequence so a rollback cannot
                -- allocate a duplicate revision.
                UPDATE flowbit.shared_variable_revision_state
                SET "AllocatorMode" = 'legacy',
                    "LastRevision" = GREATEST(
                        "LastRevision",
                        COALESCE((SELECT MAX("Revision")
                                  FROM flowbit.shared_variable_revisions), 0),
                        COALESCE((SELECT MAX("CurrentRevision")
                                  FROM flowbit.shared_variables), 0),
                        COALESCE((SELECT last_value
                                  FROM flowbit.shared_variable_revision_seq), 0)),
                    "UpdatedAt" = clock_timestamp()
                WHERE "Id" = 1;

                DROP FUNCTION IF EXISTS flowbit.cutover_shared_variable_revision_sequence();
                DROP FUNCTION IF EXISTS flowbit.next_shared_variable_revision();
                DROP SEQUENCE IF EXISTS flowbit.shared_variable_revision_seq;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_shared_variable_current_values_shared_variable_revisions_So~",
                schema: "flowbit",
                table: "shared_variable_current_values");

            migrationBuilder.DropForeignKey(
                name: "FK_shared_variable_requests_shared_variable_revisions_SharedVa~",
                schema: "flowbit",
                table: "shared_variable_requests");

            migrationBuilder.DropForeignKey(
                name: "FK_shared_variable_wakes_shared_variable_revisions_RevisionId_~",
                schema: "flowbit",
                table: "shared_variable_wakes");

            migrationBuilder.Sql(
                """
                UPDATE flowbit.shared_variable_wakes AS wake
                SET "LastError" = left(COALESCE(
                        NULLIF(incident."Details", ''),
                        NULLIF(incident."Summary", ''),
                        NULLIF(wake."LastError", ''),
                        'Shared-variable expansion incident downgraded during rollback.'), 1000)
                FROM flowbit.shared_variable_wake_incidents AS incident
                WHERE incident."WorkKind" = 'expansion'
                  AND incident."WakeId" = wake."Id"
                  AND wake."Status" = 'incident';

                UPDATE flowbit.shared_variable_wake_deliveries AS delivery
                SET "LastError" = left(COALESCE(
                        NULLIF(incident."Details", ''),
                        NULLIF(incident."Summary", ''),
                        NULLIF(delivery."LastError", ''),
                        'Shared-variable delivery incident downgraded during rollback.'), 1000)
                FROM flowbit.shared_variable_wake_incidents AS incident
                WHERE incident."WorkKind" = 'delivery'
                  AND incident."DeliveryId" = delivery."Id"
                  AND delivery."Status" = 'incident';

                UPDATE flowbit.shared_variable_wakes
                SET "Status" = 'failed',
                    "LeaseToken" = NULL,
                    "LeasedBy" = NULL,
                    "LeaseExpiresAt" = NULL,
                    "HeartbeatAt" = NULL,
                    "CompletedAt" = NULL,
                    "LastError" = COALESCE(
                        NULLIF("LastError", ''),
                        'Shared-variable expansion incident downgraded during rollback.')
                WHERE "Status" = 'incident';

                UPDATE flowbit.shared_variable_wake_deliveries
                SET "Status" = 'failed',
                    "LeaseToken" = NULL,
                    "LeasedBy" = NULL,
                    "LeaseExpiresAt" = NULL,
                    "HeartbeatAt" = NULL,
                    "CompletedAt" = NULL,
                    "LastError" = COALESCE(
                        NULLIF("LastError", ''),
                        'Shared-variable delivery incident downgraded during rollback.')
                WHERE "Status" = 'incident';
                """);

            migrationBuilder.DropTable(
                name: "shared_variable_wake_incidents",
                schema: "flowbit");

            migrationBuilder.DropCheckConstraint(
                name: "CK_shared_variables_value_revision",
                schema: "flowbit",
                table: "shared_variables");

            migrationBuilder.DropIndex(
                name: "IX_shared_variable_wakes_expired_lease",
                schema: "flowbit",
                table: "shared_variable_wakes");

            migrationBuilder.DropIndex(
                name: "IX_shared_variable_wakes_pending_available",
                schema: "flowbit",
                table: "shared_variable_wakes");

            migrationBuilder.DropIndex(
                name: "IX_shared_variable_wakes_RevisionId_SharedVariableId_Revision",
                schema: "flowbit",
                table: "shared_variable_wakes");

            migrationBuilder.DropIndex(
                name: "IX_shared_variable_wakes_terminal_cleanup",
                schema: "flowbit",
                table: "shared_variable_wakes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_shared_variable_wakes_completion_shape",
                schema: "flowbit",
                table: "shared_variable_wakes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_shared_variable_wakes_cursor",
                schema: "flowbit",
                table: "shared_variable_wakes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_shared_variable_wakes_max_attempts",
                schema: "flowbit",
                table: "shared_variable_wakes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_shared_variable_wakes_lease_shape",
                schema: "flowbit",
                table: "shared_variable_wakes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_shared_variable_wakes_status",
                schema: "flowbit",
                table: "shared_variable_wakes");

            migrationBuilder.DropIndex(
                name: "IX_shared_variable_wake_deliveries_expired_lease",
                schema: "flowbit",
                table: "shared_variable_wake_deliveries");

            migrationBuilder.DropIndex(
                name: "IX_shared_variable_wake_deliveries_open_predecessor",
                schema: "flowbit",
                table: "shared_variable_wake_deliveries");

            migrationBuilder.DropIndex(
                name: "IX_shared_variable_wake_deliveries_pending_available",
                schema: "flowbit",
                table: "shared_variable_wake_deliveries");

            migrationBuilder.DropIndex(
                name: "IX_shared_variable_wake_deliveries_terminal_cleanup",
                schema: "flowbit",
                table: "shared_variable_wake_deliveries");

            migrationBuilder.DropCheckConstraint(
                name: "CK_shared_variable_wake_deliveries_completion_shape",
                schema: "flowbit",
                table: "shared_variable_wake_deliveries");

            migrationBuilder.DropCheckConstraint(
                name: "CK_shared_variable_wake_deliveries_max_attempts",
                schema: "flowbit",
                table: "shared_variable_wake_deliveries");

            migrationBuilder.DropCheckConstraint(
                name: "CK_shared_variable_wake_deliveries_lease_shape",
                schema: "flowbit",
                table: "shared_variable_wake_deliveries");

            migrationBuilder.DropCheckConstraint(
                name: "CK_shared_variable_wake_deliveries_status",
                schema: "flowbit",
                table: "shared_variable_wake_deliveries");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_shared_variable_revisions_Id_SharedVariableId_Revision",
                schema: "flowbit",
                table: "shared_variable_revisions");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_shared_variable_revisions_SharedVariableId_Revision",
                schema: "flowbit",
                table: "shared_variable_revisions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_shared_variable_revision_state_allocator_mode",
                schema: "flowbit",
                table: "shared_variable_revision_state");

            migrationBuilder.DropIndex(
                name: "IX_shared_variable_current_values_SourceRevisionId_SharedVaria~",
                schema: "flowbit",
                table: "shared_variable_current_values");

            migrationBuilder.DropColumn(
                name: "SharedOutputValueVersionsJson",
                schema: "flowbit",
                table: "workflow_job_snapshots");

            migrationBuilder.DropColumn(
                name: "ValueRevision",
                schema: "flowbit",
                table: "shared_variables");

            migrationBuilder.DropColumn(
                name: "ExpansionCursorTokenId",
                schema: "flowbit",
                table: "shared_variable_wakes");

            migrationBuilder.DropColumn(
                name: "MaxAttempts",
                schema: "flowbit",
                table: "shared_variable_wakes");

            migrationBuilder.DropColumn(
                name: "HeartbeatAt",
                schema: "flowbit",
                table: "shared_variable_wakes");

            migrationBuilder.DropColumn(
                name: "MaxAttempts",
                schema: "flowbit",
                table: "shared_variable_wake_deliveries");

            migrationBuilder.DropColumn(
                name: "HeartbeatAt",
                schema: "flowbit",
                table: "shared_variable_wake_deliveries");

            migrationBuilder.DropColumn(
                name: "AllocatorMode",
                schema: "flowbit",
                table: "shared_variable_revision_state");

            migrationBuilder.AddCheckConstraint(
                name: "CK_shared_variable_wakes_status",
                schema: "flowbit",
                table: "shared_variable_wakes",
                sql: "\"Status\" IN ('pending', 'leased', 'completed', 'failed', 'cancelled')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_shared_variable_wakes_lease_shape",
                schema: "flowbit",
                table: "shared_variable_wakes",
                sql: "(\"Status\" = 'leased' AND \"LeaseToken\" IS NOT NULL AND \"LeasedBy\" IS NOT NULL AND \"LeaseExpiresAt\" IS NOT NULL) OR (\"Status\" <> 'leased' AND \"LeaseToken\" IS NULL AND \"LeasedBy\" IS NULL AND \"LeaseExpiresAt\" IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_wake_deliveries_TokenId",
                schema: "flowbit",
                table: "shared_variable_wake_deliveries",
                column: "TokenId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_shared_variable_wake_deliveries_status",
                schema: "flowbit",
                table: "shared_variable_wake_deliveries",
                sql: "\"Status\" IN ('pending', 'leased', 'completed', 'failed', 'cancelled')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_shared_variable_wake_deliveries_lease_shape",
                schema: "flowbit",
                table: "shared_variable_wake_deliveries",
                sql: "(\"Status\" = 'leased' AND \"LeaseToken\" IS NOT NULL AND \"LeasedBy\" IS NOT NULL AND \"LeaseExpiresAt\" IS NOT NULL) OR (\"Status\" <> 'leased' AND \"LeaseToken\" IS NULL AND \"LeasedBy\" IS NULL AND \"LeaseExpiresAt\" IS NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_shared_variable_current_values_shared_variable_revisions_So~",
                schema: "flowbit",
                table: "shared_variable_current_values",
                column: "SourceRevisionId",
                principalSchema: "flowbit",
                principalTable: "shared_variable_revisions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_shared_variable_wakes_shared_variable_revisions_RevisionId",
                schema: "flowbit",
                table: "shared_variable_wakes",
                column: "RevisionId",
                principalSchema: "flowbit",
                principalTable: "shared_variable_revisions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
