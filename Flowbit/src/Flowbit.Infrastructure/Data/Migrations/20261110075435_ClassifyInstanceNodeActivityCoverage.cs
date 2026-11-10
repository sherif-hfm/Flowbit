using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ClassifyInstanceNodeActivityCoverage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // This separate migration permits bounded commits and restart after interruption.
            // UI upgrades follow classification, so it does not alter the instance revision.
            migrationBuilder.Sql("""
                DO $classify$
                DECLARE
                    last_id bigint := -9223372036854775808;
                    upper_id bigint;
                    batch_upper bigint;
                BEGIN
                    SELECT MAX("Id") INTO upper_id FROM flowbit.workflow_instances;
                    LOOP
                        SELECT MAX(batch."Id") INTO batch_upper
                        FROM (
                            SELECT "Id" FROM flowbit.workflow_instances
                            WHERE "Id" > last_id AND "Id" <= upper_id
                            ORDER BY "Id" LIMIT 1000
                        ) batch;
                        EXIT WHEN batch_upper IS NULL;

                        UPDATE flowbit.workflow_instances instance
                        SET "NodeActivityFromCreation" = TRUE
                        WHERE instance."Id" > last_id AND instance."Id" <= batch_upper
                            AND NOT instance."NodeActivityFromCreation"
                            AND instance."HistoryPrunedAt" IS NULL
                            AND EXISTS (
                                SELECT 1 FROM flowbit.node_executions execution
                                WHERE execution."InstanceId" = instance."Id"
                                    AND NOT execution."IsCutoverSeeded"
                                    AND execution."ExecutionKind" = 'node'
                                    AND execution."NodeType" IN ('startEvent', 'messageStartEvent', 'timerStartEvent')
                            );
                        last_id := batch_upper;
                        COMMIT;
                    END LOOP;
                END $classify$;
                """, suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Classification is evidence; rolling back never erases it.
        }
    }
}
