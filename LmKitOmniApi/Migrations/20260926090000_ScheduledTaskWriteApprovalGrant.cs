using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LmKitOmniApi.Migrations
{
    [DbContext(typeof(LmKitOmniApi.Infrastructure.Data.HermesDbContext))]
    [Migration("20260926090000_ScheduledTaskWriteApprovalGrant")]
    public partial class ScheduledTaskWriteApprovalGrant : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ApproveFutureRuns",
                table: "scheduled_tasks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "LastAgentRunId",
                table: "scheduled_tasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ScheduledTaskId",
                table: "ChatSessions",
                type: "uuid",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "ApproveFutureRuns", table: "scheduled_tasks");
            migrationBuilder.DropColumn(name: "LastAgentRunId", table: "scheduled_tasks");
            migrationBuilder.DropColumn(name: "ScheduledTaskId", table: "ChatSessions");
        }
    }
}
