using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Announces how a signal broadcast finished (EVENT-04D): the lifecycle event types
    /// <c>signalBroadcast.completed</c>, <c>signalBroadcast.completedWithIssues</c>, and
    /// <c>signalBroadcast.cancelled</c>, captured by two deferred constraint triggers on
    /// <c>signal_broadcasts</c> and <c>capture_signal_broadcast_lifecycle()</c>. Every writer of
    /// a broadcast's status is covered: the broadcast repository, a workflow's throw, and the
    /// raw-SQL reconciliation of a delivery job that lost its last lease.
    /// </summary>
    /// <remarks>
    /// No table changes, so the model snapshot does not either. Creating the triggers blocks
    /// writes to <c>signal_broadcasts</c> for a moment; nothing is captured until a subscription
    /// lists one of the new types. Down drops the triggers and the function and deletes no row:
    /// stored events still deliver.
    /// </remarks>
    public partial class AddSignalBroadcastLifecycleEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(CaptureFunctionSql);
            migrationBuilder.Sql(CaptureTriggersSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS "TR_signal_broadcasts_lifecycle_insert" ON flowbit.signal_broadcasts;
                DROP TRIGGER IF EXISTS "TR_signal_broadcasts_lifecycle_status" ON flowbit.signal_broadcasts;
                DROP FUNCTION IF EXISTS flowbit.capture_signal_broadcast_lifecycle();
                """);
        }

        // Runs at commit, inside the transaction that changed the status. It reads the row again
        // so the counts are the ones that transaction commits: a cancel writes the status before
        // it recounts. A status the transaction replaced, and a row it deleted, raise nothing.
        // The body never reads PayloadJson, the business key, the thrower's name, roles, claims,
        // the cancellation, or a delivery's description. Its free texts are the envelope's
        // family (300 characters) and the signal name (100 characters); at six escaped bytes per
        // character a body stays under 3 KiB, far below CK_lifecycle_events_body. A status
        // without an event type is ignored, so a status added later never fails a commit here;
        // it needs its type added to this function, LifecycleEventCatalog, and the tests.
        private const string CaptureFunctionSql = """
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
            BEGIN
                event_type := CASE NEW."Status"
                    WHEN 'completed' THEN 'signalBroadcast.completed'
                    WHEN 'completedWithIssues' THEN 'signalBroadcast.completedWithIssues'
                    WHEN 'cancelled' THEN 'signalBroadcast.cancelled'
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
                       broadcast."CancelledCount"
                  INTO broadcast_id, signal_name, workflow_key, scope_instance_id,
                       has_business_key, thrown_by_kind, thrower_instance_id,
                       committed_status, total_count, start_count,
                       delivered_count, skipped_count, failed_count,
                       cancelled_count
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
                        || '}',
                    '{}'::text[], targets);
                RETURN NULL;
            END;
            $function$;
            """;

        // Every recount names "Status" in its UPDATE, so the WHEN clauses, which PostgreSQL
        // evaluates when the row changes, keep a recount that leaves the status unchanged, and
        // a reopening, from queueing a call. A broadcast is inserted as delivering, which queues
        // nothing; a row inserted as finished, by raw SQL or a later writer, raises its event.
        private const string CaptureTriggersSql = """
            CREATE CONSTRAINT TRIGGER "TR_signal_broadcasts_lifecycle_insert"
            AFTER INSERT ON flowbit.signal_broadcasts
            DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW
            WHEN (NEW."Status" <> 'delivering')
            EXECUTE FUNCTION flowbit.capture_signal_broadcast_lifecycle();

            CREATE CONSTRAINT TRIGGER "TR_signal_broadcasts_lifecycle_status"
            AFTER UPDATE OF "Status" ON flowbit.signal_broadcasts
            DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW
            WHEN (OLD."Status" IS DISTINCT FROM NEW."Status" AND NEW."Status" <> 'delivering')
            EXECUTE FUNCTION flowbit.capture_signal_broadcast_lifecycle();
            """;
    }
}
