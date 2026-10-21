using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLocalIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO flowbit.engine_settings ("Namespace", "Key", "Value", "Description", "CreatedAt", "UpdatedAt")
                SELECT 'Users', 'RequiredRole', 'admin', 'Roles allowed to manage local users and profile fields.', now(), now()
                WHERE NOT EXISTS (SELECT 1 FROM flowbit.engine_settings WHERE lower(CASE WHEN trim(coalesce("Namespace", '')) = '' THEN trim("Key") ELSE trim("Namespace") || '.' || trim("Key") END) = 'users.requiredrole');
                """);
            migrationBuilder.CreateTable(
                name: "local_users",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Username = table.Column<string>(type: "citext", maxLength: 300, nullable: false),
                    FirstName = table.Column<string>(type: "text", nullable: false),
                    LastName = table.Column<string>(type: "text", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    RolesJson = table.Column<string>(type: "jsonb", nullable: false),
                    ClaimsJson = table.Column<string>(type: "jsonb", nullable: false),
                    AttributesJson = table.Column<string>(type: "jsonb", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FailedAttempts = table.Column<int>(type: "integer", nullable: false),
                    LockedUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "user_attribute_definitions",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Key = table.Column<string>(type: "citext", maxLength: 100, nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Label = table.Column<string>(type: "text", nullable: false),
                    Required = table.Column<bool>(type: "boolean", nullable: false),
                    ChoicesJson = table.Column<string>(type: "jsonb", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_attribute_definitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "local_refresh_sessions",
                schema: "flowbit",
                columns: table => new
                {
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Used = table.Column<bool>(type: "boolean", nullable: false),
                    Revoked = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_refresh_sessions", x => x.Hash);
                    table.ForeignKey(
                        name: "FK_local_refresh_sessions_local_users_UserId",
                        column: x => x.UserId,
                        principalSchema: "flowbit",
                        principalTable: "local_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_local_refresh_sessions_ExpiresAt",
                schema: "flowbit",
                table: "local_refresh_sessions",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_local_refresh_sessions_FamilyId",
                schema: "flowbit",
                table: "local_refresh_sessions",
                column: "FamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_local_refresh_sessions_UserId",
                schema: "flowbit",
                table: "local_refresh_sessions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_local_users_Username",
                schema: "flowbit",
                table: "local_users",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_attribute_definitions_Key",
                schema: "flowbit",
                table: "user_attribute_definitions",
                column: "Key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "local_refresh_sessions",
                schema: "flowbit");

            migrationBuilder.DropTable(
                name: "user_attribute_definitions",
                schema: "flowbit");

            migrationBuilder.DropTable(
                name: "local_users",
                schema: "flowbit");
        }
    }
}
