using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LmKitOmniApi.Migrations
{
    /// <inheritdoc />
    public partial class AddTier3Entities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tenant_api_keys_TenantId",
                table: "tenant_api_keys");

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "tenant_api_keys",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "ProjectId",
                table: "ChatSessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "projects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Icon = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Instructions = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_projects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_projects_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_projects_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tenant_api_keys_ApiKey",
                table: "tenant_api_keys",
                column: "ApiKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tenant_api_keys_TenantId_UserId",
                table: "tenant_api_keys",
                columns: new[] { "TenantId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatSessions_ProjectId",
                table: "ChatSessions",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_projects_TenantId_UserId",
                table: "projects",
                columns: new[] { "TenantId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_projects_UserId",
                table: "projects",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_ChatSessions_projects_ProjectId",
                table: "ChatSessions",
                column: "ProjectId",
                principalTable: "projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ChatSessions_projects_ProjectId",
                table: "ChatSessions");

            migrationBuilder.DropTable(
                name: "projects");

            migrationBuilder.DropIndex(
                name: "IX_tenant_api_keys_ApiKey",
                table: "tenant_api_keys");

            migrationBuilder.DropIndex(
                name: "IX_tenant_api_keys_TenantId_UserId",
                table: "tenant_api_keys");

            migrationBuilder.DropIndex(
                name: "IX_ChatSessions_ProjectId",
                table: "ChatSessions");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "tenant_api_keys");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                table: "ChatSessions");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_api_keys_TenantId",
                table: "tenant_api_keys",
                column: "TenantId");
        }
    }
}
