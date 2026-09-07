using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LmKitOmniApi.Migrations
{
    /// <summary>
    /// Makes a run that stopped at a human-in-the-loop gate RESUMABLE. Before this, an
    /// approved gated call was recorded as a step and the run ended at
    /// <c>CompletedAfterApproval</c> — truthful, but a smaller answer than the agent
    /// could have given, because the ReAct pass that would have continued lives inside an
    /// <c>await foreach</c> whose state dies with the request that started it.
    ///
    /// <para>Three columns are the entire new state. Everything else a continuation needs
    /// is already persisted — the goal on <c>agent_runs</c>, the ordered tool history
    /// (including the approved call and its real output) on <c>agent_run_steps</c>, the
    /// execution scope on <c>task_approvals.RequestOptionsJson</c> — because the ReAct
    /// pass is history-free and takes only a query plus a context string.</para>
    /// </summary>
    public partial class AgentRunResume : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Which runs are owed a continuation, and whether somebody is driving one.
            // NULL for every run that is not mid-continuation — all of them at deploy
            // time and almost all of them at any moment after, which is also what keeps
            // the index below small.
            migrationBuilder.AddColumn<string>(
                name: "ResumeState",
                table: "agent_runs",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            // When a claim stops being owned by whoever took it, so a worker that died
            // mid-pass cannot park its run forever.
            migrationBuilder.AddColumn<DateTime>(
                name: "ResumeLeaseUntilUtc",
                table: "agent_runs",
                type: "timestamp with time zone",
                nullable: true);

            // Added nullable, backfilled, then tightened — deliberately NOT the single
            // `nullable: false, defaultValue: 0` ADD COLUMN the scaffolder wrote. That
            // form is valid PostgreSQL and would even backfill correctly here, but it
            // leaves behind a permanent DEFAULT 0 that no part of the EF model knows
            // about: the schema and the model snapshot then disagree forever, and the
            // next person to read the table sees a default the code never asked for. The
            // same three-statement shape TaskApprovalExpiry used, for the same reason —
            // it leaves nothing behind and is safe against a populated table.
            migrationBuilder.AddColumn<int>(
                name: "ResumeCount",
                table: "agent_runs",
                type: "integer",
                nullable: true);

            // Every pre-existing run has been resumed exactly zero times: the feature did
            // not exist. Runs sitting at AwaitingApproval are deliberately left parked
            // rather than queued — nobody has approved them yet, and continuing a run
            // whose human has not decided is the opposite of what the gate is for. When
            // their approval is answered they take the new path with a full budget.
            migrationBuilder.Sql("""
                UPDATE agent_runs
                SET "ResumeCount" = 0
                WHERE "ResumeCount" IS NULL;
                """);

            migrationBuilder.AlterColumn<int>(
                name: "ResumeCount",
                table: "agent_runs",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            // Serves the resume worker's only selection query (equality on ResumeState,
            // range on the lease) and the claim predicate built from it. Column order is
            // the same reasoning as IX_task_approvals_Status_ExpiresAtUtc: equality
            // first, so one index covers both the filter and the range.
            migrationBuilder.CreateIndex(
                name: "IX_agent_runs_ResumeState_ResumeLeaseUntilUtc",
                table: "agent_runs",
                columns: new[] { "ResumeState", "ResumeLeaseUntilUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_agent_runs_ResumeState_ResumeLeaseUntilUtc",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "ResumeCount",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "ResumeLeaseUntilUtc",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "ResumeState",
                table: "agent_runs");
        }
    }
}
