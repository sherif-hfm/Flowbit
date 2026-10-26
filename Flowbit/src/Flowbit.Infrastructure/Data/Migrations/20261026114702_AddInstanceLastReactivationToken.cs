using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInstanceLastReactivationToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LastReactivationTokenId",
                schema: "flowbit",
                table: "workflow_instances",
                type: "bigint",
                nullable: true);

            // Reactivations recorded so far keep their root token in history, unless
            // retention has already removed it.
            migrationBuilder.Sql("""
                UPDATE flowbit.workflow_instances AS i
                SET "LastReactivationTokenId" = h."TokenId"
                FROM (
                    SELECT "InstanceId", MAX("TokenId") AS "TokenId"
                    FROM flowbit.instance_history
                    WHERE "Note" = 'instanceReactivated' AND "TokenId" IS NOT NULL
                    GROUP BY "InstanceId"
                ) AS h
                WHERE i."Id" = h."InstanceId";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastReactivationTokenId",
                schema: "flowbit",
                table: "workflow_instances");
        }
    }
}
