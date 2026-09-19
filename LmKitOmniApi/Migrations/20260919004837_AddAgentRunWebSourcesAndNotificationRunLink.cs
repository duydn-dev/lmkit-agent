using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LmKitOmniApi.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentRunWebSourcesAndNotificationRunLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AgentRunId",
                table: "notifications",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WebSourcesJson",
                table: "agent_runs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AgentRunId",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "WebSourcesJson",
                table: "agent_runs");
        }
    }
}
