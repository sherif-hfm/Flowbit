using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveSharedVariableRevisionState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Stop every API/Worker writer before upgrading or downgrading. Preserve allocated
            // sequence values too: rolled-back writes can leave the sequence ahead of history.
            migrationBuilder.Sql(AdvanceSequence);
            migrationBuilder.Sql(SequenceAllocator);
            migrationBuilder.Sql(
                """
                DROP TRIGGER TR_shared_variable_revision_state_guard_legacy_update
                    ON flowbit.shared_variable_revision_state;
                DROP FUNCTION flowbit.guard_legacy_shared_variable_revision_update();
                DROP FUNCTION flowbit.cutover_shared_variable_revision_sequence();
                """);
            migrationBuilder.DropTable(
                name: "shared_variable_revision_state",
                schema: "flowbit");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "shared_variable_revision_state",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<short>(type: "smallint", nullable: false),
                    AllocatorMode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "legacy"),
                    LastRevision = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shared_variable_revision_state", x => x.Id);
                    table.CheckConstraint("CK_shared_variable_revision_state_allocator_mode", "\"AllocatorMode\" IN ('legacy', 'sequence')");
                    table.CheckConstraint("CK_shared_variable_revision_state_revision", "\"LastRevision\" >= 0");
                    table.CheckConstraint("CK_shared_variable_revision_state_singleton", "\"Id\" = 1");
                });

            // The previous application already supports sequence mode. Restore that state,
            // rather than reintroducing the singleton allocator or reusing allocated numbers.
            migrationBuilder.Sql(AdvanceSequence);
            migrationBuilder.Sql(
                """
                INSERT INTO flowbit.shared_variable_revision_state
                    ("Id", "AllocatorMode", "LastRevision", "UpdatedAt")
                SELECT 1, 'sequence', CASE WHEN is_called THEN last_value ELSE last_value - 1 END, now()
                FROM flowbit.shared_variable_revision_seq;
                """);
            migrationBuilder.Sql(SequenceAllocator);
            migrationBuilder.Sql(RestoreLegacyHelpers);
        }

        private const string AdvanceSequence = """
            DO $migration$
            DECLARE revision_floor bigint;
            BEGIN
                SELECT GREATEST(
                    0,
                    COALESCE((SELECT "LastRevision"
                              FROM flowbit.shared_variable_revision_state WHERE "Id" = 1), 0),
                    COALESCE((SELECT MAX("Revision") FROM flowbit.shared_variable_revisions), 0),
                    COALESCE((SELECT MAX("CurrentRevision") FROM flowbit.shared_variables), 0),
                    (SELECT CASE WHEN is_called THEN last_value ELSE last_value - 1 END
                     FROM flowbit.shared_variable_revision_seq))
                INTO revision_floor;

                -- ALTER SEQUENCE RESTART is transactional, unlike setval. Preserve the first
                -- allocation of 1 when both the old counter and the unused sequence are empty.
                EXECUTE format('ALTER SEQUENCE flowbit.shared_variable_revision_seq RESTART WITH %s',
                    revision_floor + 1);
            END;
            $migration$;
            """;

        private const string SequenceAllocator = """
            CREATE OR REPLACE FUNCTION flowbit.next_shared_variable_revision()
            RETURNS bigint
            LANGUAGE sql
            VOLATILE
            AS $function$
                SELECT nextval('flowbit.shared_variable_revision_seq'::regclass);
            $function$;
            """;

        private const string RestoreLegacyHelpers = """
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

                EXECUTE $ddl$
                    CREATE OR REPLACE FUNCTION flowbit.next_shared_variable_revision()
                    RETURNS bigint
                    LANGUAGE sql
                    VOLATILE
                    AS $body$
                        SELECT nextval('flowbit.shared_variable_revision_seq'::regclass);
                    $body$;
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
            """;
    }
}
