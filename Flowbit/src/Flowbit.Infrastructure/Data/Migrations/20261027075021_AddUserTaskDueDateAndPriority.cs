using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUserTaskDueDateAndPriority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DueAt",
                schema: "flowbit",
                table: "user_tasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Priority",
                schema: "flowbit",
                table: "user_tasks",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_tasks_Status_DueAt_Id",
                schema: "flowbit",
                table: "user_tasks",
                columns: new[] { "Status", "DueAt", "Id" });

            migrationBuilder.Sql("""
                INSERT INTO flowbit.engine_settings
                    ("Namespace", "Key", "Value", "Description", "CreatedAt", "UpdatedAt")
                SELECT
                    'UserTasks',
                    'DueSoonThreshold',
                    'P1D',
                    'How long before its due time a user task counts as due soon, as a fixed ISO-8601 duration. Invalid or missing values default to P1D.',
                    CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP
                WHERE NOT EXISTS
                (
                    SELECT 1
                    FROM flowbit.engine_settings
                    WHERE
                        ("Namespace" = 'UserTasks' AND "Key" = 'DueSoonThreshold')
                        OR
                        (BTRIM(COALESCE("Namespace", '')) = ''
                         AND "Key" = 'UserTasks.DueSoonThreshold')
                )
                ON CONFLICT ("Namespace", "Key") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM flowbit.engine_settings
                WHERE "Namespace" = 'UserTasks'
                  AND "Key" = 'DueSoonThreshold'
                  AND "Value" = 'P1D'
                  AND "Description" = 'How long before its due time a user task counts as due soon, as a fixed ISO-8601 duration. Invalid or missing values default to P1D.';
                """);

            migrationBuilder.DropIndex(
                name: "IX_user_tasks_Status_DueAt_Id",
                schema: "flowbit",
                table: "user_tasks");

            migrationBuilder.DropColumn(
                name: "DueAt",
                schema: "flowbit",
                table: "user_tasks");

            migrationBuilder.DropColumn(
                name: "Priority",
                schema: "flowbit",
                table: "user_tasks");
        }
    }
}
