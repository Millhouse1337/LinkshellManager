using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class DropDeadTables : Migration
    {
        // Phase 4B, step 1. Drops seven tables no code has read or written since Phase 4A, plus the
        // three id columns that only pointed into them:
        //   ChartBossProgresses, Notifications       - never built into any screen / never used.
        //   LinkshellDiscordWebhooks, ...Channels    - replaced by LinkshellChannelRoutes.
        //   RevenueEntries (+ Items.RevenueEntryId, JournalEntries.LegacyRevenueEntryId)
        //                                            - the old gil list, converted to JournalEntries.
        //   LedgerPeriods                            - the removed treasury lock.
        //   TodLootDetails (+ DkpLedgerEntries.SourceTodLootDetailId)
        //                                            - a ToD carries no loot.
        // Down puts the tables and columns back EMPTY; the rows only come back from the pg_dump
        // deploy.ps1 takes before migrating.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChartBossProgresses");

            migrationBuilder.DropTable(
                name: "LedgerPeriods");

            migrationBuilder.DropTable(
                name: "LinkshellDiscordChannels");

            migrationBuilder.DropTable(
                name: "LinkshellDiscordWebhooks");

            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropTable(
                name: "RevenueEntries");

            migrationBuilder.DropTable(
                name: "TodLootDetails");

            migrationBuilder.DropIndex(
                name: "IX_JournalEntries_LegacyRevenueEntryId",
                table: "JournalEntries");

            migrationBuilder.DropIndex(
                name: "IX_DkpLedgerEntries_SourceTodLootDetailId",
                table: "DkpLedgerEntries");

            migrationBuilder.DropColumn(
                name: "LegacyRevenueEntryId",
                table: "JournalEntries");

            migrationBuilder.DropColumn(
                name: "RevenueEntryId",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "SourceTodLootDetailId",
                table: "DkpLedgerEntries");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LegacyRevenueEntryId",
                table: "JournalEntries",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RevenueEntryId",
                table: "Items",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceTodLootDetailId",
                table: "DkpLedgerEntries",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ChartBossProgresses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    Board = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Boss = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProgressNote = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    UpdatedByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChartBossProgresses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChartBossProgresses_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LedgerPeriods",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    LockedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LockedByAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    LockedByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    LockedThroughUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UnlockReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    UnlockedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UnlockedByAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    UnlockedByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LedgerPeriods", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LedgerPeriods_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LinkshellDiscordChannels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    ChannelId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ChannelName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Purpose = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LinkshellDiscordChannels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LinkshellDiscordChannels_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LinkshellDiscordWebhooks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PostAttendanceSnapshot = table.Column<bool>(type: "boolean", nullable: false),
                    PostAuctions = table.Column<bool>(type: "boolean", nullable: false),
                    PostDkpSpendLog = table.Column<bool>(type: "boolean", nullable: false),
                    PostTodBoard = table.Column<bool>(type: "boolean", nullable: false),
                    TodBoardMessageId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Url = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LinkshellDiscordWebhooks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LinkshellDiscordWebhooks_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AppUserId = table.Column<string>(type: "text", nullable: true),
                    CharacterNameSender = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NotificationDetails = table.Column<string>(type: "text", nullable: true),
                    NotificationType = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Notifications_AspNetUsers_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "RevenueEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    Category = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    CreatedByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Details = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    EntryType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LinkshellName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Value = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RevenueEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RevenueEntries_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TodLootDetails",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DkpPoolId = table.Column<int>(type: "integer", nullable: true),
                    TodId = table.Column<int>(type: "integer", nullable: true),
                    ActualDeductedDkp = table.Column<double>(type: "double precision", nullable: true),
                    EditedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EditedByAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    EditedByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ItemName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ItemWinner = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    LastEditReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    WinningDkpSpent = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TodLootDetails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TodLootDetails_DkpPools_DkpPoolId",
                        column: x => x.DkpPoolId,
                        principalTable: "DkpPools",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_TodLootDetails_Tods_TodId",
                        column: x => x.TodId,
                        principalTable: "Tods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_LegacyRevenueEntryId",
                table: "JournalEntries",
                column: "LegacyRevenueEntryId",
                unique: true,
                filter: "\"LegacyRevenueEntryId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DkpLedgerEntries_SourceTodLootDetailId",
                table: "DkpLedgerEntries",
                column: "SourceTodLootDetailId");

            migrationBuilder.CreateIndex(
                name: "IX_ChartBossProgresses_LinkshellId_Board_Boss",
                table: "ChartBossProgresses",
                columns: new[] { "LinkshellId", "Board", "Boss" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LedgerPeriods_LinkshellId",
                table: "LedgerPeriods",
                column: "LinkshellId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LinkshellDiscordChannels_LinkshellId",
                table: "LinkshellDiscordChannels",
                column: "LinkshellId");

            migrationBuilder.CreateIndex(
                name: "IX_LinkshellDiscordWebhooks_LinkshellId",
                table: "LinkshellDiscordWebhooks",
                column: "LinkshellId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_AppUserId",
                table: "Notifications",
                column: "AppUserId");

            migrationBuilder.CreateIndex(
                name: "IX_RevenueEntries_LinkshellId_OccurredAt",
                table: "RevenueEntries",
                columns: new[] { "LinkshellId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TodLootDetails_DkpPoolId",
                table: "TodLootDetails",
                column: "DkpPoolId");

            migrationBuilder.CreateIndex(
                name: "IX_TodLootDetails_TodId",
                table: "TodLootDetails",
                column: "TodId");
        }
    }
}
