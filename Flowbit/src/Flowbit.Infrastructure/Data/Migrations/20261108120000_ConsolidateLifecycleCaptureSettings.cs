using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ConsolidateLifecycleCaptureSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CreatedBy", schema: "flowbit", table: "engine_settings",
                type: "character varying(300)", maxLength: 300, nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "UpdatedBy", schema: "flowbit", table: "engine_settings",
                type: "character varying(300)", maxLength: 300, nullable: true);

            // The prior database switch is authoritative. Refuse an ambiguous pre-existing logical key
            // instead of silently replacing a user setting or changing the effective capture state.
            migrationBuilder.Sql("""
                LOCK TABLE flowbit.engine_settings, flowbit.lifecycle_event_capture_state IN ACCESS EXCLUSIVE MODE;
                DO $block$
                DECLARE
                    -- Match .NET Char.IsWhiteSpace used by Trim/IsNullOrWhiteSpace in LifecycleCaptureSetting.
                    -- PostgreSQL btrim's default removes only ordinary spaces, allowing protected-key aliases through.
                    trim_characters constant text := U&'\0009\000A\000B\000C\000D\0020\0085\00A0\1680\2000\2001\2002\2003\2004\2005\2006\2007\2008\2009\200A\2028\2029\202F\205F\3000';
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM flowbit.engine_settings
                        WHERE lower(CASE WHEN btrim(COALESCE("Namespace", ''), trim_characters) = '' THEN btrim("Key", trim_characters)
                                         ELSE btrim("Namespace", trim_characters) || '.' || btrim("Key", trim_characters) END) = 'webhooks.captureenabled') THEN
                        RAISE EXCEPTION 'Cannot migrate lifecycle capture: engine setting Webhooks.CaptureEnabled or an equivalent alias already exists.';
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM flowbit.lifecycle_event_capture_state WHERE "Id" = 1) THEN
                        RAISE EXCEPTION 'Cannot migrate lifecycle capture: the existing capture switch row is missing.';
                    END IF;
                END;
                $block$;

                INSERT INTO flowbit.engine_settings
                    ("Namespace", "Key", "Value", "Description", "CreatedAt", "UpdatedAt", "CreatedBy", "UpdatedBy")
                SELECT 'Webhooks', 'CaptureEnabled', CASE WHEN "Enabled" THEN 'true' ELSE 'false' END,
                       'Records lifecycle events for matching active webhook subscriptions. Disabling capture permanently skips new events; use Worker delivery settings to pause sending.',
                       "ChangedAt", "ChangedAt", NULL, "ChangedBy"
                FROM flowbit.lifecycle_event_capture_state WHERE "Id" = 1;
                """);

            migrationBuilder.Sql(CaptureSettingFunctions);
            migrationBuilder.Sql(CaptureWantedFromEngineSettings);
            migrationBuilder.DropTable(name: "lifecycle_event_capture_state", schema: "flowbit");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lifecycle_event_capture_state", schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    ChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    ChangedBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lifecycle_event_capture_state", x => x.Id);
                    table.CheckConstraint("CK_lifecycle_event_capture_state_singleton", "\"Id\" = 1");
                });

            migrationBuilder.Sql("""
                LOCK TABLE flowbit.engine_settings IN ACCESS EXCLUSIVE MODE;
                INSERT INTO flowbit.lifecycle_event_capture_state ("Id", "Enabled", "ChangedAt", "ChangedBy")
                SELECT 1, "Value" = 'true', "UpdatedAt", "UpdatedBy"
                FROM flowbit.engine_settings WHERE "Namespace" = 'Webhooks' AND "Key" = 'CaptureEnabled';
                DROP TRIGGER "TR_engine_settings_capture_audit" ON flowbit.engine_settings;
                DROP TRIGGER "TR_engine_settings_capture_guard" ON flowbit.engine_settings;
                DROP FUNCTION flowbit.audit_lifecycle_capture_setting();
                DROP FUNCTION flowbit.guard_lifecycle_capture_setting();
                DELETE FROM flowbit.engine_settings WHERE "Namespace" = 'Webhooks' AND "Key" = 'CaptureEnabled';
                """);
            migrationBuilder.Sql(CaptureWantedFromLegacyTable);
            migrationBuilder.Sql(LegacyCaptureSetter);
            migrationBuilder.DropColumn(name: "CreatedBy", schema: "flowbit", table: "engine_settings");
            migrationBuilder.DropColumn(name: "UpdatedBy", schema: "flowbit", table: "engine_settings");
        }

        private const string CaptureSettingFunctions = """
            CREATE OR REPLACE FUNCTION flowbit.guard_lifecycle_capture_setting()
            RETURNS trigger
            LANGUAGE plpgsql
            SECURITY INVOKER
            SET search_path = pg_catalog, pg_temp
            AS $function$
            DECLARE
                -- Keep reserved-key matching identical to .NET Trim/IsNullOrWhiteSpace, including Unicode whitespace.
                trim_characters constant text := U&'\0009\000A\000B\000C\000D\0020\0085\00A0\1680\2000\2001\2002\2003\2004\2005\2006\2007\2008\2009\200A\2028\2029\202F\205F\3000';
                reserved_old boolean := false;
                reserved_new boolean := false;
            BEGIN
                IF TG_OP <> 'INSERT' THEN
                    reserved_old := OLD."Namespace" = 'Webhooks' AND OLD."Key" = 'CaptureEnabled';
                END IF;
                IF reserved_old AND TG_OP = 'DELETE' THEN
                    RAISE EXCEPTION 'Engine setting Webhooks.CaptureEnabled cannot be deleted; set it to false instead.'
                        USING ERRCODE = '23514';
                END IF;
                IF TG_OP = 'DELETE' THEN
                    RETURN OLD;
                END IF;
                reserved_new := lower(CASE WHEN btrim(COALESCE(NEW."Namespace", ''), trim_characters) = '' THEN btrim(NEW."Key", trim_characters)
                                           ELSE btrim(NEW."Namespace", trim_characters) || '.' || btrim(NEW."Key", trim_characters) END) = 'webhooks.captureenabled';
                IF reserved_old AND (NEW."Namespace" IS DISTINCT FROM 'Webhooks' OR NEW."Key" IS DISTINCT FROM 'CaptureEnabled') THEN
                    RAISE EXCEPTION 'Engine setting Webhooks.CaptureEnabled cannot be renamed or moved.'
                        USING ERRCODE = '23514';
                END IF;
                IF reserved_new THEN
                    IF NEW."Namespace" IS DISTINCT FROM 'Webhooks' OR NEW."Key" IS DISTINCT FROM 'CaptureEnabled' THEN
                        RAISE EXCEPTION 'Use the canonical engine setting namespace Webhooks and key CaptureEnabled.'
                            USING ERRCODE = '23514';
                    END IF;
                    IF NEW."Value" IS NULL OR NEW."Value" NOT IN ('true', 'false') THEN
                        RAISE EXCEPTION 'Engine setting Webhooks.CaptureEnabled must be true or false.'
                            USING ERRCODE = '23514';
                    END IF;
                    IF TG_OP = 'UPDATE' AND OLD."Value" IS DISTINCT FROM NEW."Value" THEN
                        NEW."UpdatedBy" := btrim(NEW."UpdatedBy", trim_characters);
                        IF NEW."UpdatedBy" IS NULL OR NEW."UpdatedBy" = '' THEN
                            RAISE EXCEPTION 'Changing Webhooks.CaptureEnabled requires a nonblank operator.'
                                USING ERRCODE = '23514';
                        END IF;
                    END IF;
                END IF;
                RETURN NEW;
            END;
            $function$;

            CREATE TRIGGER "TR_engine_settings_capture_guard"
                BEFORE INSERT OR UPDATE OR DELETE ON flowbit.engine_settings
                FOR EACH ROW EXECUTE FUNCTION flowbit.guard_lifecycle_capture_setting();

            CREATE OR REPLACE FUNCTION flowbit.audit_lifecycle_capture_setting()
            RETURNS trigger
            LANGUAGE plpgsql
            SECURITY INVOKER
            SET search_path = pg_catalog, pg_temp
            AS $function$
            BEGIN
                IF NEW."Namespace" = 'Webhooks' AND NEW."Key" = 'CaptureEnabled'
                   AND OLD."Value" IS DISTINCT FROM NEW."Value" THEN
                    INSERT INTO flowbit.webhook_management_audit ("Operation", "Actor", "OccurredAt", "ChangesJson")
                    VALUES (CASE WHEN NEW."Value" = 'true' THEN 'captureEnabled' ELSE 'captureDisabled' END,
                            NEW."UpdatedBy", now(), jsonb_build_object('enabled', NEW."Value" = 'true'));
                END IF;
                RETURN NEW;
            END;
            $function$;

            CREATE TRIGGER "TR_engine_settings_capture_audit"
                AFTER UPDATE ON flowbit.engine_settings
                FOR EACH ROW EXECUTE FUNCTION flowbit.audit_lifecycle_capture_setting();

            CREATE OR REPLACE FUNCTION flowbit.set_lifecycle_event_capture(capture_enabled boolean, operator_name text)
            RETURNS void
            LANGUAGE plpgsql
            VOLATILE
            SECURITY INVOKER
            SET search_path = pg_catalog, pg_temp
            AS $function$
            DECLARE
                -- Match the application's actor Trim/IsNullOrWhiteSpace rules, not only ordinary spaces.
                trim_characters constant text := U&'\0009\000A\000B\000C\000D\0020\0085\00A0\1680\2000\2001\2002\2003\2004\2005\2006\2007\2008\2009\200A\2028\2029\202F\205F\3000';
                normalized_operator text := btrim(operator_name, trim_characters);
            BEGIN
                IF capture_enabled IS NULL THEN
                    RAISE EXCEPTION 'capture_enabled must be true or false.';
                END IF;
                IF normalized_operator IS NULL OR normalized_operator = '' OR char_length(normalized_operator) > 300 THEN
                    RAISE EXCEPTION 'operator_name must be a nonblank name of at most 300 characters.';
                END IF;
                UPDATE flowbit.engine_settings
                   SET "Value" = CASE WHEN capture_enabled THEN 'true' ELSE 'false' END,
                       "UpdatedAt" = greatest(now(), "UpdatedAt" + interval '1 microsecond'),
                       "UpdatedBy" = normalized_operator
                 WHERE "Namespace" = 'Webhooks' AND "Key" = 'CaptureEnabled'
                   AND "Value" IS DISTINCT FROM CASE WHEN capture_enabled THEN 'true' ELSE 'false' END;
                -- The engine-settings trigger owns audit insertion for both this function and ordinary settings writes.
            END;
            $function$;
            """;

        private const string CaptureWantedFromEngineSettings = """
            CREATE OR REPLACE FUNCTION flowbit.lifecycle_capture_wanted(event_types text[])
            RETURNS boolean
            LANGUAGE plpgsql
            STABLE
            SECURITY INVOKER
            SET search_path = pg_catalog, pg_temp
            AS $function$
            BEGIN
                IF lower(COALESCE(current_setting('flowbit.lifecycle_events', true), '')) = 'off' THEN
                    RETURN false;
                END IF;
                RETURN EXISTS (SELECT 1 FROM flowbit.engine_settings
                               WHERE "Namespace" = 'Webhooks' AND "Key" = 'CaptureEnabled' AND "Value" = 'true')
                    AND EXISTS (
                        SELECT 1 FROM flowbit.webhook_subscriptions subscription
                        JOIN flowbit.webhook_subscription_revisions revision ON revision."Id" = subscription."CurrentRevisionId"
                        WHERE subscription."State" = 'active' AND revision."EventTypes" && event_types);
            END;
            $function$;
            """;

        private const string CaptureWantedFromLegacyTable = """
            CREATE OR REPLACE FUNCTION flowbit.lifecycle_capture_wanted(event_types text[])
            RETURNS boolean
            LANGUAGE plpgsql
            STABLE
            SECURITY INVOKER
            SET search_path = pg_catalog, pg_temp
            AS $function$
            BEGIN
                IF lower(COALESCE(current_setting('flowbit.lifecycle_events', true), '')) = 'off' THEN
                    RETURN false;
                END IF;
                RETURN EXISTS (SELECT 1 FROM flowbit.lifecycle_event_capture_state WHERE "Id" = 1 AND "Enabled")
                    AND EXISTS (
                        SELECT 1 FROM flowbit.webhook_subscriptions subscription
                        JOIN flowbit.webhook_subscription_revisions revision ON revision."Id" = subscription."CurrentRevisionId"
                        WHERE subscription."State" = 'active' AND revision."EventTypes" && event_types);
            END;
            $function$;
            """;

        private const string LegacyCaptureSetter = """
            CREATE OR REPLACE FUNCTION flowbit.set_lifecycle_event_capture(capture_enabled boolean, operator_name text)
            RETURNS void
            LANGUAGE plpgsql
            VOLATILE
            SECURITY INVOKER
            SET search_path = pg_catalog, pg_temp
            AS $function$
            DECLARE changed integer;
            BEGIN
                IF capture_enabled IS NULL THEN
                    RAISE EXCEPTION 'capture_enabled must be true or false.';
                END IF;
                IF operator_name IS NULL OR btrim(operator_name) = '' OR char_length(btrim(operator_name)) > 300 THEN
                    RAISE EXCEPTION 'operator_name must be a nonblank name of at most 300 characters.';
                END IF;
                UPDATE flowbit.lifecycle_event_capture_state
                   SET "Enabled" = capture_enabled, "ChangedAt" = now(), "ChangedBy" = btrim(operator_name)
                 WHERE "Id" = 1 AND "Enabled" IS DISTINCT FROM capture_enabled;
                GET DIAGNOSTICS changed = ROW_COUNT;
                IF changed > 0 THEN
                    INSERT INTO flowbit.webhook_management_audit ("Operation", "Actor", "OccurredAt", "ChangesJson")
                    VALUES (CASE WHEN capture_enabled THEN 'captureEnabled' ELSE 'captureDisabled' END,
                            btrim(operator_name), now(), jsonb_build_object('enabled', capture_enabled));
                END IF;
            END;
            $function$;
            """;
    }
}
