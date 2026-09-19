using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LmKitOmniApi.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentRunProducedFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProducedFilesJson",
                table: "agent_runs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProducedFilesJson",
                table: "agent_runs");
        }
    }
}
