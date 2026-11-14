using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Adds the shape check of a call body's <c>multiInstanceStart</c> entry job, which TASK-05A
    /// left open: it names an instance and token, but no body, item, control generation, task,
    /// or invocation, and it runs on the control queue.
    /// </summary>
    /// <remarks>
    /// The constraint is added <c>NOT VALID</c>, so adding it takes a brief lock and does not
    /// scan <c>workflow_jobs</c>. ValidateMultiInstanceStartJobShape validates it outside a
    /// transaction, as the foundation's widened checks are validated.
    /// </remarks>
    public partial class AddMultiInstanceStartJobShape : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE flowbit.workflow_jobs
                    ADD CONSTRAINT "CK_workflow_jobs_multi_instance_start_shape"
                    CHECK ("Kind" <> 'multiInstanceStart' OR ("MultiInstanceExecutionId" IS NULL AND "AutomaticMultiInstanceItemId" IS NULL AND "MultiInstanceControlGeneration" IS NULL AND "InstanceId" IS NOT NULL AND "TokenId" IS NOT NULL AND "UserTaskId" IS NULL AND "CallInvocationId" IS NULL AND "QueueClass" = 'control'))
                    NOT VALID;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_workflow_jobs_multi_instance_start_shape",
                schema: "flowbit",
                table: "workflow_jobs");
        }
    }
}
