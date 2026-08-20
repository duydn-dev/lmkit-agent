using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using LmKitOmniApi.Infrastructure.Data;

#nullable disable

namespace LmKitOmniApi.Migrations;

[DbContext(typeof(HermesDbContext))]
[Migration("20260812000100_AddAuditTenantId")]
public partial class AddAuditTenantId : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "TenantId",
            table: "audit_logs",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_audit_logs_TenantId_CreatedAtUtc",
            table: "audit_logs",
            columns: new[] { "TenantId", "CreatedAtUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_audit_logs_TenantId_CreatedAtUtc",
            table: "audit_logs");

        migrationBuilder.DropColumn(name: "TenantId", table: "audit_logs");
    }
}
