using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LmKitOmniApi.Migrations
{
    /// <inheritdoc />
    public partial class AddWidgetQuotaAndRotation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RequestsPerDay",
                table: "tenant_widget_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RequestsPerMinute",
                table: "tenant_widget_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "RotatedAtUtc",
                table: "tenant_widget_settings",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequestsPerDay",
                table: "tenant_widget_settings");

            migrationBuilder.DropColumn(
                name: "RequestsPerMinute",
                table: "tenant_widget_settings");

            migrationBuilder.DropColumn(
                name: "RotatedAtUtc",
                table: "tenant_widget_settings");
        }
    }
}
