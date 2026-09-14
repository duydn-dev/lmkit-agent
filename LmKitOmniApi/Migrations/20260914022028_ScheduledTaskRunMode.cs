using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LmKitOmniApi.Migrations
{
    /// <inheritdoc />
    public partial class ScheduledTaskRunMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RunMode",
                table: "scheduled_tasks",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "completion");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RunMode",
                table: "scheduled_tasks");
        }
    }
}
