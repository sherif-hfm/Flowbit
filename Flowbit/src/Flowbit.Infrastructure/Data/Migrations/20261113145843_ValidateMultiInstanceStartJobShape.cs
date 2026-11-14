using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Validates <c>CK_workflow_jobs_multi_instance_start_shape</c>, which
    /// AddMultiInstanceStartJobShape added NOT VALID.
    /// </summary>
    /// <remarks>
    /// Validation scans the table, so it runs outside a transaction under a lock that lets reads
    /// and writes continue. Validating a constraint that is already valid changes nothing, so a
    /// run that stops partway is finished by applying the migration again.
    /// </remarks>
    public partial class ValidateMultiInstanceStartJobShape : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE flowbit.\"workflow_jobs\" VALIDATE CONSTRAINT \"CK_workflow_jobs_multi_instance_start_shape\";",
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Validation is idempotent; the schema migration owns constraint removal.
        }
    }
}
