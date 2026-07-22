using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddDkpPools : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DkpPoolId",
                table: "TodLootDetails",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DkpPoolId",
                table: "DkpLedgerEntries",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DkpPoolPinned",
                table: "DkpLedgerEntries",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "DkpPoolId",
                table: "Auctions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DkpPoolId",
                table: "AuctionHistories",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DkpPoolLedgerFromId",
                table: "AppUserLinkshells",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "DkpPools",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    Accent = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DkpPools", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DkpPools_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DkpPoolEventTypes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    DkpPoolId = table.Column<int>(type: "integer", nullable: false),
                    EventType = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    NormalizedEventType = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DkpPoolEventTypes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DkpPoolEventTypes_DkpPools_DkpPoolId",
                        column: x => x.DkpPoolId,
                        principalTable: "DkpPools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DkpPoolEventTypes_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TodLootDetails_DkpPoolId",
                table: "TodLootDetails",
                column: "DkpPoolId");

            migrationBuilder.CreateIndex(
                name: "IX_DkpLedgerEntries_LinkshellId_DkpPoolId",
                table: "DkpLedgerEntries",
                columns: new[] { "LinkshellId", "DkpPoolId" });

            migrationBuilder.CreateIndex(
                name: "IX_Auctions_DkpPoolId",
                table: "Auctions",
                column: "DkpPoolId");

            migrationBuilder.CreateIndex(
                name: "IX_AuctionHistories_DkpPoolId",
                table: "AuctionHistories",
                column: "DkpPoolId");

            migrationBuilder.CreateIndex(
                name: "IX_DkpPoolEventTypes_DkpPoolId",
                table: "DkpPoolEventTypes",
                column: "DkpPoolId");

            migrationBuilder.CreateIndex(
                name: "IX_DkpPoolEventTypes_LinkshellId_NormalizedEventType",
                table: "DkpPoolEventTypes",
                columns: new[] { "LinkshellId", "NormalizedEventType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DkpPools_LinkshellId_Default",
                table: "DkpPools",
                column: "LinkshellId",
                unique: true,
                filter: "\"IsDefault\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_DkpPools_LinkshellId_Name",
                table: "DkpPools",
                columns: new[] { "LinkshellId", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AuctionHistories_DkpPools_DkpPoolId",
                table: "AuctionHistories",
                column: "DkpPoolId",
                principalTable: "DkpPools",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Auctions_DkpPools_DkpPoolId",
                table: "Auctions",
                column: "DkpPoolId",
                principalTable: "DkpPools",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_TodLootDetails_DkpPools_DkpPoolId",
                table: "TodLootDetails",
                column: "DkpPoolId",
                principalTable: "DkpPools",
                principalColumn: "Id");

            // ---- data backfill -------------------------------------------------------------
            //
            // The goal is that every EXISTING linkshell comes out of this migration behaving
            // exactly as it did going in. That needs surprisingly little:

            // 1. One default pool per linkshell. NOW() is already timestamptz — NOW() AT TIME ZONE
            //    'UTC' would hand back a timestamp WITHOUT a zone and get re-read in the session's
            //    zone on the way into Npgsql's timestamptz mapping.
            migrationBuilder.Sql("""
                INSERT INTO "DkpPools" ("LinkshellId", "Name", "SortOrder", "IsDefault", "Accent", "CreatedAtUtc")
                SELECT l."Id", 'Main', 0, TRUE, 'Neutral', NOW()
                FROM "Linkshells" l
                WHERE NOT EXISTS (SELECT 1 FROM "DkpPools" p WHERE p."LinkshellId" = l."Id");
                """);

            // 2. NO "DkpPoolEventTypes" rows. An empty mapping means every event type — known,
            //    custom or null — falls through to the default pool, which IS the pre-pools
            //    behaviour. Nothing to insert.

            // 3. NO "DkpLedgerEntries" UPDATE. Deliberate. A NULL DkpPoolId reads as "the default
            //    pool" everywhere (DkpPoolBalanceService), so every historical row is already
            //    attributed correctly for a single-pool linkshell. Rewriting a mature ledger here
            //    would mean minutes of ACCESS EXCLUSIVE lock inside Database.Migrate() at startup —
            //    a 502 window on deploy. The ledger gets stamped lazily instead, one linkshell at a
            //    time, by DkpPoolEditor the first time an officer actually creates a second pool.

            // 4. ToD loot IS pinned to the default pool now, because the refund path reads the pool
            //    back off this row: an officer who later maps HNM elsewhere must still see old loot
            //    refunded to the pool it was originally taken from. Bounded table, one UPDATE.
            migrationBuilder.Sql("""
                UPDATE "TodLootDetails" d
                SET "DkpPoolId" = p."Id"
                FROM "Tods" t
                JOIN "DkpPools" p ON p."LinkshellId" = t."LinkshellId" AND p."IsDefault"
                WHERE d."TodId" = t."Id" AND d."DkpPoolId" IS NULL;
                """);

            // 5. Live auctions get an explicit pool so their card and their bid validation have
            //    something concrete to name. AuctionHistories stay NULL (= default at read time).
            migrationBuilder.Sql("""
                UPDATE "Auctions" a
                SET "DkpPoolId" = p."Id"
                FROM "DkpPools" p
                WHERE p."LinkshellId" = a."LinkshellId" AND p."IsDefault" AND a."DkpPoolId" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AuctionHistories_DkpPools_DkpPoolId",
                table: "AuctionHistories");

            migrationBuilder.DropForeignKey(
                name: "FK_Auctions_DkpPools_DkpPoolId",
                table: "Auctions");

            migrationBuilder.DropForeignKey(
                name: "FK_TodLootDetails_DkpPools_DkpPoolId",
                table: "TodLootDetails");

            migrationBuilder.DropTable(
                name: "DkpPoolEventTypes");

            migrationBuilder.DropTable(
                name: "DkpPools");

            migrationBuilder.DropIndex(
                name: "IX_TodLootDetails_DkpPoolId",
                table: "TodLootDetails");

            migrationBuilder.DropIndex(
                name: "IX_DkpLedgerEntries_LinkshellId_DkpPoolId",
                table: "DkpLedgerEntries");

            migrationBuilder.DropIndex(
                name: "IX_Auctions_DkpPoolId",
                table: "Auctions");

            migrationBuilder.DropIndex(
                name: "IX_AuctionHistories_DkpPoolId",
                table: "AuctionHistories");

            migrationBuilder.DropColumn(
                name: "DkpPoolId",
                table: "TodLootDetails");

            migrationBuilder.DropColumn(
                name: "DkpPoolId",
                table: "DkpLedgerEntries");

            migrationBuilder.DropColumn(
                name: "DkpPoolPinned",
                table: "DkpLedgerEntries");

            migrationBuilder.DropColumn(
                name: "DkpPoolId",
                table: "Auctions");

            migrationBuilder.DropColumn(
                name: "DkpPoolId",
                table: "AuctionHistories");

            migrationBuilder.DropColumn(
                name: "DkpPoolLedgerFromId",
                table: "AppUserLinkshells");
        }
    }
}
