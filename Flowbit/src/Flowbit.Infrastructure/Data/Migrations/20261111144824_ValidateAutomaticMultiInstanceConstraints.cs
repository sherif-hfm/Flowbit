using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ValidateAutomaticMultiInstanceConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (table, constraint) in new[]
            {
                ("workflow_jobs", "CK_workflow_jobs_automatic_control_shape"),
                ("workflow_jobs", "CK_workflow_jobs_automatic_item_shape"),
                ("node_executions", "CK_node_executions_execution_kind"),
                ("node_executions", "CK_node_executions_multi_instance_shape")
            })
                migrationBuilder.Sql($"ALTER TABLE flowbit.\"{table}\" VALIDATE CONSTRAINT \"{constraint}\";", suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Validation is idempotent; the schema migration owns constraint removal.
        }
    }
}
