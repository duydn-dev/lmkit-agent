using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LmKitOmniApi.Migrations
{
    /// <inheritdoc />
    public partial class CanvasVersionUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Replaces the non-unique (TenantId, UserId, RootId, Version) index with a
            // UNIQUE (TenantId, RootId, Version) one — the DB-level guard against two
            // concurrent saves writing the same version number for one canvas root.
            // NOTE for deployment: if the previously-benign version race already left
            // duplicate (TenantId, RootId, Version) rows in an existing database, they
            // must be de-duplicated before this index will build.
            migrationBuilder.DropIndex(
                name: "IX_canvas_artifacts_TenantId_UserId_RootId_Version",
                table: "canvas_artifacts");

            migrationBuilder.CreateIndex(
                name: "IX_canvas_artifacts_TenantId_RootId_Version",
                table: "canvas_artifacts",
                columns: new[] { "TenantId", "RootId", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_canvas_artifacts_TenantId_RootId_Version",
                table: "canvas_artifacts");

            migrationBuilder.CreateIndex(
                name: "IX_canvas_artifacts_TenantId_UserId_RootId_Version",
                table: "canvas_artifacts",
                columns: new[] { "TenantId", "UserId", "RootId", "Version" });
        }
    }
}
