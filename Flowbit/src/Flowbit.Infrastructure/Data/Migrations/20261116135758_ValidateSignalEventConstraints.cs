using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Validates <c>CK_node_executions_completion_reason</c>, which AddSignalEvents replaced
    /// NOT VALID to add <c>signalReceived</c>.
    /// </summary>
    /// <remarks>
    /// Validation scans the table, so it runs outside a transaction under a lock that lets reads
    /// and writes continue. Validating a constraint that is already valid changes nothing, so a
    /// run that stops partway is finished by applying the migration again.
    /// </remarks>
    public partial class ValidateSignalEventConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE flowbit.\"node_executions\" VALIDATE CONSTRAINT \"CK_node_executions_completion_reason\";",
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Validation is idempotent; the schema migration owns constraint replacement.
        }
    }
}
