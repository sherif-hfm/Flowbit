using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddActorAuditClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<JsonDocument>(
                name: "CompletedByClaimsJson",
                schema: "flowbit",
                table: "node_executions",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<JsonDocument>(
                name: "TriggeredByClaimsJson",
                schema: "flowbit",
                table: "node_executions",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<JsonDocument>(
                name: "ActorClaimsJson",
                schema: "flowbit",
                table: "instance_history",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CompletedByClaimsJson",
                schema: "flowbit",
                table: "node_executions");

            migrationBuilder.DropColumn(
                name: "TriggeredByClaimsJson",
                schema: "flowbit",
                table: "node_executions");

            migrationBuilder.DropColumn(
                name: "ActorClaimsJson",
                schema: "flowbit",
                table: "instance_history");
        }
    }
}
