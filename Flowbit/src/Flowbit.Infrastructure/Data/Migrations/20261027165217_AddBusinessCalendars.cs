using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBusinessCalendars : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ScheduleCalendar",
                schema: "flowbit",
                table: "timer_subscriptions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "business_calendars",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "citext", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    TimeZone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    WeeklyHours = table.Column<JsonElement>(type: "jsonb", nullable: false),
                    Holidays = table.Column<JsonElement>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_business_calendars", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "workflow_definition_business_calendars",
                schema: "flowbit",
                columns: table => new
                {
                    WorkflowDefinitionId = table.Column<long>(type: "bigint", nullable: false),
                    BusinessCalendarId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_definition_business_calendars", x => new { x.WorkflowDefinitionId, x.BusinessCalendarId });
                    table.ForeignKey(
                        name: "FK_workflow_definition_business_calendars_business_calendars_B~",
                        column: x => x.BusinessCalendarId,
                        principalSchema: "flowbit",
                        principalTable: "business_calendars",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_workflow_definition_business_calendars_workflow_definitions~",
                        column: x => x.WorkflowDefinitionId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_business_calendars_Name",
                schema: "flowbit",
                table: "business_calendars",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_definition_business_calendars_BusinessCalendarId",
                schema: "flowbit",
                table: "workflow_definition_business_calendars",
                column: "BusinessCalendarId");

            // The example catalog's working-time workflow names this calendar.
            migrationBuilder.Sql(
                """
                INSERT INTO flowbit.business_calendars ("Name", "Description", "TimeZone", "WeeklyHours", "Holidays", "CreatedAt", "UpdatedAt")
                SELECT 'examples.weekdays',
                       'Monday to Friday, 09:00 to 17:00 UTC, without holidays. Used by the example workflows.',
                       'Etc/UTC',
                       '{"monday":[{"start":"09:00","end":"17:00"}],"tuesday":[{"start":"09:00","end":"17:00"}],"wednesday":[{"start":"09:00","end":"17:00"}],"thursday":[{"start":"09:00","end":"17:00"}],"friday":[{"start":"09:00","end":"17:00"}],"saturday":[],"sunday":[]}'::jsonb,
                       '[]'::jsonb,
                       now(),
                       now()
                WHERE NOT EXISTS (
                    SELECT 1 FROM flowbit.business_calendars WHERE "Name" = 'examples.weekdays'::citext);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workflow_definition_business_calendars",
                schema: "flowbit");

            migrationBuilder.DropTable(
                name: "business_calendars",
                schema: "flowbit");

            migrationBuilder.DropColumn(
                name: "ScheduleCalendar",
                schema: "flowbit",
                table: "timer_subscriptions");
        }
    }
}
