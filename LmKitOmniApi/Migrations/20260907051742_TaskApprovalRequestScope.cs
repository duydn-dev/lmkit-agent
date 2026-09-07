using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LmKitOmniApi.Migrations
{
    /// <summary>
    /// Stores the requesting turn's tool / knowledge / web narrowing on the approval row
    /// itself. Before this, that scope was recoverable only by walking
    /// approval → session → bound custom agent — and DELETING the agent NULLs every
    /// session's binding (<c>ChatSession.CustomAgentId</c>, <c>DeleteBehavior.SetNull</c>),
    /// so a pending approval from such a session executed with NO narrowing at all:
    /// strictly more authority than the turn that requested it (known-issues #2).
    /// </summary>
    public partial class TaskApprovalRequestScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Nullable, with NO backfill and NO column default — unlike the ExpiresAtUtc
            // column added by TaskApprovalExpiry, which was backfilled from each row's own
            // CreatedAtUtc. The difference: there is nothing to backfill FROM. The
            // request-time scope was never persisted anywhere, so no statement over this
            // database can reconstruct it for a pre-existing row, and deriving it from the
            // session's CURRENT binding would be worse than leaving it empty — it would
            // stamp today's agent configuration onto the row as if it were what the turn
            // ran under, which is precisely the false provenance this column exists to end.
            //
            // NULL is already the right value for those rows: the resolver reads it as "no
            // snapshot" and falls back to the session walk, i.e. byte-identical
            // pre-migration behaviour, which is correct for every case except the
            // deleted-agent one — and no backfill could have covered that one either. Rows
            // written from this deploy onward carry a real snapshot.
            //
            // PostgreSQL: adding a nullable column with no default is a catalog-only change
            // (no table rewrite, no long lock) — safe against a populated task_approvals.
            // No index is created: nothing ever filters or sorts on this column; it is read
            // by primary key alongside the row the approval handler has already loaded.
            migrationBuilder.AddColumn<string>(
                name: "RequestOptionsJson",
                table: "task_approvals",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequestOptionsJson",
                table: "task_approvals");
        }
    }
}
