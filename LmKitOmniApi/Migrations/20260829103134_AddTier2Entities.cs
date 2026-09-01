using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LmKitOmniApi.Migrations
{
    /// <inheritdoc />
    public partial class AddTier2Entities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_notifications_UserId",
                table: "notifications");

            migrationBuilder.AddColumn<Guid>(
                name: "CustomAgentId",
                table: "ChatSessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "canvas_artifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RootId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChatSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Language = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Content = table.Column<string>(type: "text", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_canvas_artifacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_canvas_artifacts_ChatSessions_ChatSessionId",
                        column: x => x.ChatSessionId,
                        principalTable: "ChatSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_canvas_artifacts_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_canvas_artifacts_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "custom_agents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Icon = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    PersonaPrompt = table.Column<string>(type: "text", nullable: false),
                    AllowedToolsCsv = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    KnowledgeDocumentIdsCsv = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    IsSharedWithTenant = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_custom_agents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_custom_agents_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_custom_agents_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "scheduled_tasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Prompt = table.Column<string>(type: "text", nullable: false),
                    ScheduleKind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IntervalMinutes = table.Column<int>(type: "integer", nullable: true),
                    TimeOfDayMinutes = table.Column<int>(type: "integer", nullable: true),
                    DayOfWeek = table.Column<int>(type: "integer", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    NextRunUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastRunUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastStatus = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ClaimedUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scheduled_tasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_scheduled_tasks_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_scheduled_tasks_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_notifications_UserId_IsRead_CreatedAtUtc",
                table: "notifications",
                columns: new[] { "UserId", "IsRead", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatSessions_CustomAgentId",
                table: "ChatSessions",
                column: "CustomAgentId");

            migrationBuilder.CreateIndex(
                name: "IX_canvas_artifacts_ChatSessionId",
                table: "canvas_artifacts",
                column: "ChatSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_canvas_artifacts_TenantId_UserId_RootId_Version",
                table: "canvas_artifacts",
                columns: new[] { "TenantId", "UserId", "RootId", "Version" });

            migrationBuilder.CreateIndex(
                name: "IX_canvas_artifacts_UserId",
                table: "canvas_artifacts",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_custom_agents_OwnerUserId",
                table: "custom_agents",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_custom_agents_TenantId_IsSharedWithTenant",
                table: "custom_agents",
                columns: new[] { "TenantId", "IsSharedWithTenant" });

            migrationBuilder.CreateIndex(
                name: "IX_custom_agents_TenantId_OwnerUserId",
                table: "custom_agents",
                columns: new[] { "TenantId", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_scheduled_tasks_Enabled_NextRunUtc",
                table: "scheduled_tasks",
                columns: new[] { "Enabled", "NextRunUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_scheduled_tasks_TenantId_UserId",
                table: "scheduled_tasks",
                columns: new[] { "TenantId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_scheduled_tasks_UserId",
                table: "scheduled_tasks",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_ChatSessions_custom_agents_CustomAgentId",
                table: "ChatSessions",
                column: "CustomAgentId",
                principalTable: "custom_agents",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ChatSessions_custom_agents_CustomAgentId",
                table: "ChatSessions");

            migrationBuilder.DropTable(
                name: "canvas_artifacts");

            migrationBuilder.DropTable(
                name: "custom_agents");

            migrationBuilder.DropTable(
                name: "scheduled_tasks");

            migrationBuilder.DropIndex(
                name: "IX_notifications_UserId_IsRead_CreatedAtUtc",
                table: "notifications");

            migrationBuilder.DropIndex(
                name: "IX_ChatSessions_CustomAgentId",
                table: "ChatSessions");

            migrationBuilder.DropColumn(
                name: "CustomAgentId",
                table: "ChatSessions");

            migrationBuilder.CreateIndex(
                name: "IX_notifications_UserId",
                table: "notifications",
                column: "UserId");
        }
    }
}
