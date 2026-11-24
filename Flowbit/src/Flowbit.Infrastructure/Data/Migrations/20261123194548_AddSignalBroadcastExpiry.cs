using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Lets a broadcast thrown through the API expire (EVENT-04E). <c>ExpiresAt</c> is the time
    /// the database computed at the throw, null for a broadcast that never expires, and
    /// <c>ExpiredCount</c> counts the skipped deliveries whose result is <c>broadcast_expired</c>.
    /// The status <c>expired</c> joins the status check, a new check keeps the expired count
    /// within the skipped one and only on broadcasts with an expiry, and a partial index serves
    /// the Worker's expiry sweep. The capture function gains the event type
    /// <c>signalBroadcast.expired</c>; the two triggers stay.
    /// </summary>
    /// <remarks>
    /// Every existing row gets a null <c>ExpiresAt</c> and an <c>ExpiredCount</c> of 0, which both
    /// checks accept, so nothing is backfilled. Both columns change only the catalog; each check
    /// scans <c>signal_broadcasts</c> once, and the index is empty when it is built. Down refuses
    /// while a broadcast has an expiry or the status <c>expired</c>: dropping the column would turn
    /// a broadcast that expires into one that never does, and the restored check rejects the status.
    /// </remarks>
    public partial class AddSignalBroadcastExpiry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_signal_broadcasts_status",
                schema: "flowbit",
                table: "signal_broadcasts");

            migrationBuilder.AddColumn<int>(
                name: "ExpiredCount",
                schema: "flowbit",
                table: "signal_broadcasts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExpiresAt",
                schema: "flowbit",
                table: "signal_broadcasts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_signal_broadcasts_open_expiry",
                schema: "flowbit",
                table: "signal_broadcasts",
                column: "ExpiresAt",
                filter: "\"Status\" = 'delivering' AND \"ExpiresAt\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_signal_broadcasts_expiry",
                schema: "flowbit",
                table: "signal_broadcasts",
                sql: "\"ExpiredCount\" BETWEEN 0 AND \"SkippedCount\" AND (\"ExpiresAt\" IS NOT NULL OR (\"ExpiredCount\" = 0 AND \"Status\" <> 'expired')) AND (\"Status\" <> 'expired' OR \"ExpiredCount\" > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_signal_broadcasts_status",
                schema: "flowbit",
                table: "signal_broadcasts",
                sql: "\"Status\" IN ('delivering', 'completed', 'completedWithIssues', 'expired', 'cancelled')");

            migrationBuilder.Sql(CaptureFunctionSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Retention removes finished broadcasts once they are old enough; cancel the ones
            // that are still delivering.
            migrationBuilder.Sql("""
                DO $guard$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM flowbit.signal_broadcasts
                        WHERE "ExpiresAt" IS NOT NULL OR "Status" = 'expired') THEN
                        RAISE EXCEPTION 'Signal broadcasts with an expiry exist. Let them finish and be removed by retention, or cancel them, before reverting AddSignalBroadcastExpiry.';
                    END IF;
                END
                $guard$;
                """);

            migrationBuilder.Sql(AddSignalBroadcastLifecycleEvents.CaptureFunctionSql);

            migrationBuilder.DropIndex(
                name: "IX_signal_broadcasts_open_expiry",
                schema: "flowbit",
                table: "signal_broadcasts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_signal_broadcasts_expiry",
                schema: "flowbit",
                table: "signal_broadcasts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_signal_broadcasts_status",
                schema: "flowbit",
                table: "signal_broadcasts");

            migrationBuilder.DropColumn(
                name: "ExpiredCount",
                schema: "flowbit",
                table: "signal_broadcasts");

            migrationBuilder.DropColumn(
                name: "ExpiresAt",
                schema: "flowbit",
                table: "signal_broadcasts");

            migrationBuilder.AddCheckConstraint(
                name: "CK_signal_broadcasts_status",
                schema: "flowbit",
                table: "signal_broadcasts",
                sql: "\"Status\" IN ('delivering', 'completed', 'completedWithIssues', 'cancelled')");
        }

        // The EVENT-04D function with a fourth status. Its body is the shared one for the three
        // earlier types, byte for byte, and the shared one plus ,"expiredCount":n for
        // signalBroadcast.expired. The expired count adds no string, so a body stays under 3 KiB.
        // A status without an event type is still ignored; a later status needs its type added
        // here, in LifecycleEventCatalog, and in the tests.
        internal const string CaptureFunctionSql = """
            CREATE OR REPLACE FUNCTION flowbit.capture_signal_broadcast_lifecycle()
            RETURNS trigger
            LANGUAGE plpgsql
            SECURITY INVOKER
            SET search_path = pg_catalog, pg_temp
            AS $function$
            DECLARE
                event_type text;
                targets bigint[];
                broadcast_id bigint;
                signal_name text;
                workflow_key text;
                scope_instance_id bigint;
                has_business_key boolean;
                thrown_by_kind text;
                thrower_instance_id bigint;
                committed_status text;
                total_count integer;
                start_count integer;
                delivered_count integer;
                skipped_count integer;
                failed_count integer;
                cancelled_count integer;
                expired_count integer;
            BEGIN
                event_type := CASE NEW."Status"
                    WHEN 'completed' THEN 'signalBroadcast.completed'
                    WHEN 'completedWithIssues' THEN 'signalBroadcast.completedWithIssues'
                    WHEN 'cancelled' THEN 'signalBroadcast.cancelled'
                    WHEN 'expired' THEN 'signalBroadcast.expired'
                END;
                IF event_type IS NULL
                   OR NOT flowbit.lifecycle_capture_wanted(ARRAY[event_type]) THEN
                    RETURN NULL;
                END IF;

                -- The row as this transaction commits it, without its payload.
                SELECT broadcast."Id", broadcast."SignalName", broadcast."WorkflowKey", broadcast."InstanceId",
                       broadcast."BusinessKey" IS NOT NULL, broadcast."ThrownByKind", broadcast."ThrowerInstanceId",
                       broadcast."Status", broadcast."TotalDeliveryCount", broadcast."StartDeliveryCount",
                       broadcast."DeliveredCount", broadcast."SkippedCount", broadcast."FailedCount",
                       broadcast."CancelledCount", broadcast."ExpiredCount"
                  INTO broadcast_id, signal_name, workflow_key, scope_instance_id,
                       has_business_key, thrown_by_kind, thrower_instance_id,
                       committed_status, total_count, start_count,
                       delivered_count, skipped_count, failed_count,
                       cancelled_count, expired_count
                  FROM flowbit.signal_broadcasts broadcast
                 WHERE broadcast."Id" = NEW."Id";
                IF NOT FOUND OR committed_status IS DISTINCT FROM NEW."Status" THEN
                    RETURN NULL;
                END IF;

                -- A broadcast's family is the one its scope names, so a broadcast to all
                -- workflows matches only subscriptions without a family.
                targets := flowbit.lifecycle_event_targets(event_type, workflow_key);
                IF cardinality(targets) = 0 THEN
                    RETURN NULL;
                END IF;
                PERFORM flowbit.record_lifecycle_event(
                    event_type, NEW."UpdatedAt", 'signal_broadcasts', broadcast_id, workflow_key,
                    NULL, scope_instance_id, NULL,
                    '{"broadcastId":' || broadcast_id::text
                        || ',"signalName":' || to_json(signal_name)::text
                        || ',"scope":' || to_json(CASE
                               WHEN workflow_key IS NULL THEN 'allWorkflows'
                               WHEN scope_instance_id IS NOT NULL THEN 'instance'
                               WHEN has_business_key THEN 'businessKey'
                               ELSE 'workflow'
                           END::text)::text
                        || ',"thrownByKind":' || to_json(thrown_by_kind)::text
                        || ',"thrownByInstanceId":' || COALESCE(
                               CASE WHEN thrown_by_kind = 'instance' THEN thrower_instance_id END::text, 'null')
                        || ',"status":' || to_json(committed_status)::text
                        || ',"totalDeliveryCount":' || total_count::text
                        || ',"startCount":' || start_count::text
                        || ',"deliveredCount":' || delivered_count::text
                        || ',"skippedCount":' || skipped_count::text
                        || ',"failedCount":' || failed_count::text
                        || ',"cancelledCount":' || cancelled_count::text
                        || CASE WHEN committed_status = 'expired'
                               THEN ',"expiredCount":' || expired_count::text
                               ELSE ''
                           END
                        || '}',
                    '{}'::text[], targets);
                RETURN NULL;
            END;
            $function$;
            """;
    }
}
