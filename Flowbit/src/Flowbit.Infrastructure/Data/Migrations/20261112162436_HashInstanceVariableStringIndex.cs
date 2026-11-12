using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Replaces the case-insensitive string index on the current-variable projection,
    /// which indexed each whole lowercased value. A B-tree entry holds at most 2,704
    /// bytes, so any string variable with more text than that could not be written at
    /// all. The new index holds a fixed-size MD5 of the lowercased value; searches match
    /// it and then compare the full value, so results are unchanged.
    /// </summary>
    public partial class HashInstanceVariableStringIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // No stored value can be longer than the old index allowed, so building the
            // new index first and dropping the old one afterwards needs no data change.
            migrationBuilder.Sql(
                """
                CREATE INDEX "IX_iv_current_name_root_string_ci_hash_instance"
                ON flowbit.instance_variable_current_values
                    ("VariableName", md5(lower("ValueJson" #>> '{}')), "InstanceId")
                WHERE jsonb_typeof("ValueJson") = 'string';

                DROP INDEX flowbit."IX_iv_current_name_root_string_ci_instance";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Fails, naming the index, while any string value is too long for the old
            // index; shorten or remove those values before rolling back.
            migrationBuilder.Sql(
                """
                CREATE INDEX "IX_iv_current_name_root_string_ci_instance"
                ON flowbit.instance_variable_current_values
                    ("VariableName", lower("ValueJson" #>> '{}'), "InstanceId")
                WHERE jsonb_typeof("ValueJson") = 'string';

                DROP INDEX flowbit."IX_iv_current_name_root_string_ci_hash_instance";
                """);
        }
    }
}
