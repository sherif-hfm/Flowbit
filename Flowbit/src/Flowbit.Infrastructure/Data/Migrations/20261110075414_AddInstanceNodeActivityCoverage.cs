using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInstanceNodeActivityCoverage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "NodeActivityFromCreation",
                schema: "flowbit",
                table: "workflow_instances",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NodeActivityPrunedAt",
                schema: "flowbit",
                table: "workflow_instances",
                type: "timestamp with time zone",
                nullable: true);

            // Old rows remain false until the separate restartable classification.
            // New inserts, including older API writers that omit the column, default true.
            migrationBuilder.Sql("""
                ALTER TABLE flowbit.workflow_instances
                    ALTER COLUMN "NodeActivityFromCreation" SET DEFAULT TRUE;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $guard$
                BEGIN
                    IF EXISTS (SELECT 1 FROM flowbit.workflow_instances WHERE "NodeActivityPrunedAt" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Cannot downgrade retention coverage after node activity has been pruned; preserve the permanent pruning marker.';
                    END IF;
                END $guard$;
                """);
            migrationBuilder.DropColumn(
                name: "NodeActivityFromCreation",
                schema: "flowbit",
                table: "workflow_instances");

            migrationBuilder.DropColumn(
                name: "NodeActivityPrunedAt",
                schema: "flowbit",
                table: "workflow_instances");
        }
    }
}
