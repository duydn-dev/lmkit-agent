using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LmKitOmniApi.Migrations
{
    /// <inheritdoc />
    public partial class McpOAuthClientCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AuthMode",
                table: "external_mcp_servers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Static");

            migrationBuilder.AddColumn<string>(
                name: "OAuthClientId",
                table: "external_mcp_servers",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OAuthClientSecretProtected",
                table: "external_mcp_servers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OAuthScopes",
                table: "external_mcp_servers",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OAuthTokenUrl",
                table: "external_mcp_servers",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AuthMode",
                table: "external_mcp_servers");

            migrationBuilder.DropColumn(
                name: "OAuthClientId",
                table: "external_mcp_servers");

            migrationBuilder.DropColumn(
                name: "OAuthClientSecretProtected",
                table: "external_mcp_servers");

            migrationBuilder.DropColumn(
                name: "OAuthScopes",
                table: "external_mcp_servers");

            migrationBuilder.DropColumn(
                name: "OAuthTokenUrl",
                table: "external_mcp_servers");
        }
    }
}
