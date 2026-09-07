using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LmKitOmniApi.Migrations
{
    /// <summary>
    /// Puts a deadline on a public chat share link. Before this, <c>chat_share_links</c>
    /// had only <c>CreatedAtUtc</c> and a nullable <c>RevokedAtUtc</c>, and every read
    /// path tested nothing but <c>RevokedAtUtc IS NULL</c> — so a link minted once stayed
    /// publicly resolvable forever unless a human remembered to revoke it. The resource
    /// behind that URL is an entire private conversation.
    ///
    /// <para>Purely additive and reversible. It does not drop anything, and
    /// <c>Down</c> restores the previous behaviour exactly.</para>
    /// </summary>
    public partial class ChatShareLinkExpiry : Migration
    {
        /// <summary>
        /// Lifetime for a new link, mirroring <c>ChatShareLink.DefaultTimeToLiveDays</c>.
        /// Hardcoded because a migration cannot read <c>ShareLinks:TimeToLiveDays</c> —
        /// and should not: the backfill is a one-time historical decision about rows that
        /// already exist, not a running policy. A deployment that later tunes the option
        /// changes only what gets minted from then on.
        /// </summary>
        private const int TimeToLiveDays = 30;

        /// <summary>
        /// Minimum runway granted to a link that is ALREADY older than the TTL on the day
        /// this migration runs. See the backfill comment for why it exists and why it is
        /// shorter than <see cref="TimeToLiveDays"/>.
        /// </summary>
        private const int LegacyGraceDays = 14;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Added nullable, backfilled, then tightened — deliberately NOT the single
            // non-nullable ADD COLUMN the scaffolder wrote (same reasoning as
            // TaskApprovalExpiry). That one is valid PostgreSQL — Npgsql renders EF's
            // DateTime.MinValue default as TIMESTAMPTZ '-infinity' — but it is wrong
            // twice over: it would put EVERY existing share link at '-infinity', killing
            // on deploy day every link anyone is currently using, and it would leave a
            // permanent DEFAULT '-infinity' on the column, so any later INSERT that
            // omitted ExpiresAtUtc would silently mint a link that is already dead. The
            // three statements below leave no column default behind and are safe against
            // a populated table.
            migrationBuilder.AddColumn<DateTime>(
                name: "ExpiresAtUtc",
                table: "chat_share_links",
                type: "timestamp with time zone",
                nullable: true);

            // BACKFILL DECISION: every pre-existing link gets a real deadline — none is
            // grandfathered as permanent — but nothing that works today stops working
            // tomorrow morning.
            //
            //   GREATEST(CreatedAtUtc + 30 days, now() + 14 days)
            //
            // A link younger than the TTL keeps the exact window it would have been given
            // had it been minted under this code: measured from its own CreatedAtUtc, so
            // it is treated no better and no worse than a new one. A link already older
            // than the TTL would otherwise land in the past and stop resolving the instant
            // the migration commits — a silent, unannounced revocation of links people are
            // actively using, which is a data-loss-shaped surprise and the one outcome
            // worth engineering around. Those rows get a 14-day floor instead: long enough
            // to span a holiday or a sprint, and for a recipient to say "your link died"
            // and the owner to re-share (one click — the create endpoint already rotates).
            //
            // The floor is deliberately SHORTER than the TTL, which is what makes this
            // different from a flat `now() + 30 days`. A flat clock would hand a fresh
            // full lifetime to links that have been public and forgotten for a year — the
            // very rows this change exists to close down — and would also stretch young
            // links past the window the policy says they get. GREATEST gives each row
            // whichever is later of "its own natural window" and "a fortnight from now",
            // and nothing else.
            //
            // The alternative — leaving legacy rows NULL and treating them as permanent —
            // was rejected. It is the option that changes nothing for anybody, which is
            // precisely the problem: the hole is not "new links are permanent", it is
            // "links are permanent", and every link that exists today was created under
            // the broken rule. Grandfathering them would leave the entire installed base
            // of public URLs immortal and make the fix cosmetic. NOT NULL below is the
            // enforcement: after this migration the schema itself cannot hold a permanent
            // link.
            migrationBuilder.Sql($"""
                UPDATE chat_share_links
                SET "ExpiresAtUtc" = GREATEST(
                        "CreatedAtUtc" + INTERVAL '{TimeToLiveDays} days',
                        now() + INTERVAL '{LegacyGraceDays} days')
                WHERE "ExpiresAtUtc" IS NULL;
                """);

            // DEPLOY ORDER: this leaves the column NOT NULL with no default, so an OLD
            // build still serving traffic cannot insert into chat_share_links (its INSERT
            // omits ExpiresAtUtc). During a rolling deploy, apply this migration with the
            // new build, or accept that share-link CREATION 500s on old pods until they
            // are cycled. Reads, revocation and every other table are unaffected. A
            // temporary column DEFAULT would paper over that window, but a DEFAULT that
            // outlives the deploy is exactly the trap described above — a forgotten one
            // would quietly mint links with the wrong deadline forever — so the ordering
            // requirement is stated instead of hidden behind one.
            migrationBuilder.AlterColumn<DateTime>(
                name: "ExpiresAtUtc",
                table: "chat_share_links",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverts to "a link lives until someone revokes it". Losing the deadlines is
            // the point of rolling back, not a casualty of it: the pre-migration code has
            // no column to read them from. RevokedAtUtc is untouched, so anything revoked
            // stays revoked.
            migrationBuilder.DropColumn(
                name: "ExpiresAtUtc",
                table: "chat_share_links");
        }
    }
}
