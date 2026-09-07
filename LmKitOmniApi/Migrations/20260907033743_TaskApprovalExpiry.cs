using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LmKitOmniApi.Migrations
{
    /// <summary>
    /// Puts a deadline on a human-in-the-loop approval. Before this, a
    /// <c>task_approvals</c> row nobody answered stayed Pending forever, the agent run
    /// parked on it stayed in AwaitingApproval forever, and the approve endpoint would
    /// still execute a side-effecting tool call that had been proposed weeks earlier.
    /// </summary>
    public partial class TaskApprovalExpiry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Added nullable, backfilled, then tightened — deliberately NOT the single
            // non-nullable ADD COLUMN the scaffolder wrote. That one is valid PostgreSQL
            // (Npgsql renders EF's DateTime.MinValue default as TIMESTAMPTZ '-infinity';
            // checked with `dotnet ef migrations script`), but it is wrong twice over: it
            // backdates EVERY existing approval to -infinity, including one raised five
            // minutes before the deploy that a human is in the middle of reading, and it
            // leaves a permanent DEFAULT '-infinity' on the column, so any later INSERT
            // that omits ExpiresAtUtc would silently create an approval that is already
            // dead. The three statements below leave no column default behind and are
            // safe against a populated table.
            migrationBuilder.AddColumn<DateTime>(
                name: "ExpiresAtUtc",
                table: "task_approvals",
                type: "timestamp with time zone",
                nullable: true);

            // Every pre-existing approval gets the window it would have been given had it
            // been created under this code: measured from its own CreatedAtUtc, with the
            // same 24 hours as TaskApproval.DefaultTimeToLiveHours. So an approval already
            // older than a day lands overdue, and the first sweep after deployment closes
            // it and releases the agent run it had parked — which is the point of the
            // change, not a side effect of it. Deliberately NOT now() + 24h, which would
            // hand a fresh day of life to rows that have been rotting for months.
            migrationBuilder.Sql("""
                UPDATE task_approvals
                SET "ExpiresAtUtc" = "CreatedAtUtc" + INTERVAL '24 hours'
                WHERE "ExpiresAtUtc" IS NULL;
                """);

            migrationBuilder.AlterColumn<DateTime>(
                name: "ExpiresAtUtc",
                table: "task_approvals",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);

            // Serves the sweeper's only query (equality on Status, range + ORDER BY on
            // ExpiresAtUtc) and the deadline predicate the pending list adds.
            migrationBuilder.CreateIndex(
                name: "IX_task_approvals_Status_ExpiresAtUtc",
                table: "task_approvals",
                columns: new[] { "Status", "ExpiresAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_task_approvals_Status_ExpiresAtUtc",
                table: "task_approvals");

            migrationBuilder.DropColumn(
                name: "ExpiresAtUtc",
                table: "task_approvals");
        }
    }
}
