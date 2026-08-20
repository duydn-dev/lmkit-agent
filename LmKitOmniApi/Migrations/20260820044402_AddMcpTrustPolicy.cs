using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LmKitOmniApi.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpTrustPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "TrustReadOnlyAnnotations",
                table: "external_mcp_servers",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TrustReadOnlyAnnotations",
                table: "external_mcp_servers");
        }
    }
}
