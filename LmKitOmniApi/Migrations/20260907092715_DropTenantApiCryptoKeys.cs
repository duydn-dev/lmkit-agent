using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LmKitOmniApi.Migrations
{
    /// <summary>
    /// Drops <c>tenant_api_crypto_keys</c>, the table behind the only entity in this
    /// repository with neither a read path nor a write path.
    ///
    /// <para><b>*** DESTRUCTIVE AND IRREVERSIBLE FOR DATA. READ BEFORE APPLYING. ***</b>
    /// <c>Down</c> below rebuilds the table's full structure — columns, primary key, the
    /// cascading foreign key to <c>Tenants</c>, the <c>TenantId</c> index — so the SCHEMA
    /// change rolls back cleanly. It cannot bring back a single row. Any deployment that
    /// somehow populated this table MUST export it first
    /// (<c>\copy tenant_api_crypto_keys TO 'tenant_api_crypto_keys.csv' CSV HEADER</c>)
    /// and store that export as the key material it is — the <c>PrivateKeyPem</c> column
    /// holds unencrypted private keys, so the dump is a secret, not a backup file. Check
    /// before you apply:
    /// <c>SELECT count(*) FROM tenant_api_crypto_keys;</c> — if that is not 0, stop and
    /// find out who wrote them.</para>
    ///
    /// <para><b>Why this is believed empty everywhere.</b> Nothing has ever inserted into
    /// it. Verified across the whole repository: the type was referenced in exactly three
    /// places — its own declaration, the <c>DbSet</c>, and one <c>OnModelCreating</c>
    /// relationship — all of which this change removes; there is no query, no handler, no
    /// controller, no seeder, and no service that names it. There is no raw SQL anywhere
    /// in the codebase (no <c>FromSql</c>, no <c>ExecuteSql</c>), and the only reflection
    /// over types is <c>LmKitToolCatalogRenderer</c> enumerating the LM-Kit vendor
    /// assembly, not this one. The <c>"cryptokey"</c> / <c>"keypem"</c> fragments in
    /// <c>AuditValueSanitizer</c> are a property-NAME denylist matched against strings and
    /// never referenced the entity; they are left in place as defence for any future key
    /// material.</para>
    ///
    /// <para><b>Why this is a separate migration.</b> Kept apart from
    /// <c>ChatShareLinkExpiry</c> on purpose, so the two need not be accepted together.
    /// An operator can take the additive share-link deadline and stop there —
    /// <c>dotnet ef database update ChatShareLinkExpiry</c> — verify the count above at
    /// leisure, and apply this one deliberately afterwards. Folding both into one
    /// migration would have made a security fix that should ship immediately hostage to a
    /// table drop that should not be rushed.</para>
    /// </summary>
    public partial class DropTenantApiCryptoKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drops the table, its primary key, its TenantId index and the cascading FK
            // to Tenants in one statement. See the class comment: export first if the
            // table is not empty.
            migrationBuilder.DropTable(
                name: "tenant_api_crypto_keys");
        }

        /// <summary>
        /// Recreates the table exactly as <c>AddLockoutToUser</c> first built it: the
        /// same six columns and nullability, <c>PK_tenant_api_crypto_keys</c>, the
        /// cascading <c>FK_tenant_api_crypto_keys_Tenants_TenantId</c>, and
        /// <c>IX_tenant_api_crypto_keys_TenantId</c>. A rollback therefore leaves a
        /// schema an older build can run against unchanged — but an EMPTY one. Rows are
        /// restored only from the export the class comment demands, never by this method.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenant_api_crypto_keys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    PrivateKeyPem = table.Column<string>(type: "text", nullable: false),
                    PublicKeyPem = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_api_crypto_keys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tenant_api_crypto_keys_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tenant_api_crypto_keys_TenantId",
                table: "tenant_api_crypto_keys",
                column: "TenantId");
        }
    }
}
