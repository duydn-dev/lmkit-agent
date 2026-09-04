using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LmKitOmniApi.Migrations
{
    /// <inheritdoc />
    public partial class AddLoraAdapters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LoraAdapterId",
                table: "custom_agents",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "lora_adapter_registrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    FilePath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Scale = table.Column<float>(type: "real", nullable: false),
                    TargetModelId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lora_adapter_registrations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_lora_adapter_registrations_TenantId_IsActive",
                table: "lora_adapter_registrations",
                columns: new[] { "TenantId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_lora_adapter_registrations_TenantId_Name",
                table: "lora_adapter_registrations",
                columns: new[] { "TenantId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lora_adapter_registrations");

            migrationBuilder.DropColumn(
                name: "LoraAdapterId",
                table: "custom_agents");
        }
    }
}
