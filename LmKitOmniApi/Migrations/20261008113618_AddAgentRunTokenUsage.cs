using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LmKitOmniApi.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentRunTokenUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CompletionTokens",
                table: "agent_runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LatencyMs",
                table: "agent_runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ModelName",
                table: "agent_runs",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PromptTokens",
                table: "agent_runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_agent_runs_CreatedAtUtc",
                table: "agent_runs",
                column: "CreatedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_agent_runs_CreatedAtUtc",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "CompletionTokens",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "LatencyMs",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "ModelName",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "PromptTokens",
                table: "agent_runs");
        }
    }
}
