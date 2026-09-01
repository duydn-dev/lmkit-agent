using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LmKitOmniApi.Migrations;

public partial class SecureUsersAndDocumentLifecycle : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            UPDATE "Users"
            SET "Email" = LOWER(BTRIM("Email"));
            """);

        migrationBuilder.CreateIndex(
            name: "IX_Users_Email",
            table: "Users",
            column: "Email",
            unique: true);

        migrationBuilder.AddColumn<string>(
            name: "LastProcessingError",
            table: "Documents",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "ProcessingAttempts",
            table: "Documents",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<DateTime>(
            name: "ProcessingLeaseUntilUtc",
            table: "Documents",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "VectorizationStatus",
            table: "Documents",
            type: "text",
            nullable: false,
            defaultValue: "Pending");

        migrationBuilder.Sql("""
            UPDATE "Documents"
            SET "IsVectorized" = FALSE,
                "VectorizationStatus" = 'Pending',
                "ProcessingAttempts" = 0,
                "ProcessingLeaseUntilUtc" = NULL,
                "LastProcessingError" = NULL;
            """);

        migrationBuilder.CreateIndex(
            name: "IX_Documents_VectorizationStatus_ProcessingLeaseUntilUtc_UploadedAt",
            table: "Documents",
            columns: new[] { "VectorizationStatus", "ProcessingLeaseUntilUtc", "UploadedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_Users_Email", table: "Users");
        migrationBuilder.DropIndex(
            name: "IX_Documents_VectorizationStatus_ProcessingLeaseUntilUtc_UploadedAt",
            table: "Documents");
        migrationBuilder.DropColumn(name: "LastProcessingError", table: "Documents");
        migrationBuilder.DropColumn(name: "ProcessingAttempts", table: "Documents");
        migrationBuilder.DropColumn(name: "ProcessingLeaseUntilUtc", table: "Documents");
        migrationBuilder.DropColumn(name: "VectorizationStatus", table: "Documents");
    }
}
