using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LmKitOmniApi.Migrations
{
    /// <inheritdoc />
    public partial class WidgetSettingsTenantUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A database that already lost the admin upsert's read-then-insert race
            // carries duplicate rows for a tenant (which is what bricks that tenant's
            // widget: every read is a SingleOrDefaultAsync). Creating the unique index
            // over that data would fail outright, so collapse duplicates first, keeping
            // the most recently updated row — the one the admin last saved.
            migrationBuilder.Sql("""
                DELETE FROM tenant_widget_settings AS a
                USING tenant_widget_settings AS b
                WHERE a."TenantId" = b."TenantId"
                  AND (a."UpdatedAtUtc", a."Id") < (b."UpdatedAtUtc", b."Id");
                """);

            migrationBuilder.DropIndex(
                name: "IX_tenant_widget_settings_TenantId",
                table: "tenant_widget_settings");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_widget_settings_TenantId",
                table: "tenant_widget_settings",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tenant_widget_settings_WidgetApiKeyHash",
                table: "tenant_widget_settings",
                column: "WidgetApiKeyHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tenant_widget_settings_TenantId",
                table: "tenant_widget_settings");

            migrationBuilder.DropIndex(
                name: "IX_tenant_widget_settings_WidgetApiKeyHash",
                table: "tenant_widget_settings");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_widget_settings_TenantId",
                table: "tenant_widget_settings",
                column: "TenantId");
        }
    }
}
