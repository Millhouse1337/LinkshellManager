using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class DropDeadLedgerLinkshellColumns : Migration
    {
        // Phase 4B, step 4. Drops ledger, treasury, linkshell, ToD, rule and addon-token columns no
        // code has read or written since Phase 4A, on AddonApiTokens, AddonPairingCodes,
        // Announcements, DkpLedgerEntries, Items, JournalEntries, JournalEntryLines, LedgerAccounts,
        // Linkshells, PartySetupSlots, Rules and Tods. That includes the old Google Sheets login
        // still saved on one linkshell, so no separate clean-up UPDATE is needed.
        // Linkshells.Status, Linkshells.TodMonsterTimings and PartySetupSlots.Label go too: the
        // production counts showed every linkshell is 'Active', none still needs the old timing
        // blob, and no slot has a label, so this step removed their last readers.
        // Down puts the columns back EMPTY; the values only come back from the pg_dump deploy.ps1
        // takes before migrating.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DkpLedgerEntries_AuctionHistories_SourceAuctionHistoryId",
                table: "DkpLedgerEntries");

            migrationBuilder.DropIndex(
                name: "IX_LedgerAccounts_LinkshellId_SortOrder",
                table: "LedgerAccounts");

            migrationBuilder.DropIndex(
                name: "IX_DkpLedgerEntries_AttInputRowNumber",
                table: "DkpLedgerEntries");

            migrationBuilder.DropIndex(
                name: "IX_DkpLedgerEntries_SourceAuctionHistoryId",
                table: "DkpLedgerEntries");

            migrationBuilder.DropColumn(
                name: "TotalClaims",
                table: "Tods");

            migrationBuilder.DropColumn(
                name: "TotalTods",
                table: "Tods");

            migrationBuilder.DropColumn(
                name: "LinkshellName",
                table: "Rules");

            migrationBuilder.DropColumn(
                name: "Label",
                table: "PartySetupSlots");

            migrationBuilder.DropColumn(
                name: "DkpExportDiscordChannelId",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "DkpExportDiscordChannelName",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "DkpSheetDiscordMessageId",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "DkpTemplateTabName",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "GoogleOAuthConnectedAt",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "GoogleOAuthRefreshTokenEnc",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "GoogleOAuthUserEmail",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "GoogleSheetAppCreated",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "GoogleSpreadsheetId",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "SheetTemplateSyncEnabled",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "TodMonsterTimings",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "LedgerAccounts");

            migrationBuilder.DropColumn(
                name: "IsPostable",
                table: "LedgerAccounts");

            migrationBuilder.DropColumn(
                name: "IsSystem",
                table: "LedgerAccounts");

            migrationBuilder.DropColumn(
                name: "SortOrder",
                table: "LedgerAccounts");

            migrationBuilder.DropColumn(
                name: "LineMemo",
                table: "JournalEntryLines");

            migrationBuilder.DropColumn(
                name: "SourceAuctionHistoryId",
                table: "JournalEntries");

            migrationBuilder.DropColumn(
                name: "SourceAuctionItemId",
                table: "JournalEntries");

            migrationBuilder.DropColumn(
                name: "LinkshellName",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "AttInputRowNumber",
                table: "DkpLedgerEntries");

            migrationBuilder.DropColumn(
                name: "EventEndTime",
                table: "DkpLedgerEntries");

            migrationBuilder.DropColumn(
                name: "EventStartTime",
                table: "DkpLedgerEntries");

            migrationBuilder.DropColumn(
                name: "SheetAppendedAt",
                table: "DkpLedgerEntries");

            migrationBuilder.DropColumn(
                name: "SourceAuctionHistoryId",
                table: "DkpLedgerEntries");

            migrationBuilder.DropColumn(
                name: "LinkshellName",
                table: "Announcements");

            migrationBuilder.DropColumn(
                name: "ConsumedTokenId",
                table: "AddonPairingCodes");

            migrationBuilder.DropColumn(
                name: "Label",
                table: "AddonPairingCodes");

            migrationBuilder.DropColumn(
                name: "Label",
                table: "AddonApiTokens");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TotalClaims",
                table: "Tods",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TotalTods",
                table: "Tods",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LinkshellName",
                table: "Rules",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Label",
                table: "PartySetupSlots",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DkpExportDiscordChannelId",
                table: "Linkshells",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DkpExportDiscordChannelName",
                table: "Linkshells",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DkpSheetDiscordMessageId",
                table: "Linkshells",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DkpTemplateTabName",
                table: "Linkshells",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "GoogleOAuthConnectedAt",
                table: "Linkshells",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GoogleOAuthRefreshTokenEnc",
                table: "Linkshells",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GoogleOAuthUserEmail",
                table: "Linkshells",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "GoogleSheetAppCreated",
                table: "Linkshells",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "GoogleSpreadsheetId",
                table: "Linkshells",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SheetTemplateSyncEnabled",
                table: "Linkshells",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "Linkshells",
                type: "text",
                nullable: true);

            // The old code only lists and lets people join linkshells whose Status is 'Active', and
            // every production row was 'Active' when this dropped. Without this a rollback would
            // hide every linkshell from search.
            migrationBuilder.Sql("UPDATE \"Linkshells\" SET \"Status\" = 'Active';");

            migrationBuilder.AddColumn<string>(
                name: "TodMonsterTimings",
                table: "Linkshells",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "LedgerAccounts",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsPostable",
                table: "LedgerAccounts",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsSystem",
                table: "LedgerAccounts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "SortOrder",
                table: "LedgerAccounts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "LineMemo",
                table: "JournalEntryLines",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceAuctionHistoryId",
                table: "JournalEntries",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceAuctionItemId",
                table: "JournalEntries",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LinkshellName",
                table: "Items",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AttInputRowNumber",
                table: "DkpLedgerEntries",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EventEndTime",
                table: "DkpLedgerEntries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EventStartTime",
                table: "DkpLedgerEntries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SheetAppendedAt",
                table: "DkpLedgerEntries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceAuctionHistoryId",
                table: "DkpLedgerEntries",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LinkshellName",
                table: "Announcements",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ConsumedTokenId",
                table: "AddonPairingCodes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Label",
                table: "AddonPairingCodes",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Label",
                table: "AddonApiTokens",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_LedgerAccounts_LinkshellId_SortOrder",
                table: "LedgerAccounts",
                columns: new[] { "LinkshellId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_DkpLedgerEntries_AttInputRowNumber",
                table: "DkpLedgerEntries",
                column: "AttInputRowNumber");

            migrationBuilder.CreateIndex(
                name: "IX_DkpLedgerEntries_SourceAuctionHistoryId",
                table: "DkpLedgerEntries",
                column: "SourceAuctionHistoryId");

            migrationBuilder.AddForeignKey(
                name: "FK_DkpLedgerEntries_AuctionHistories_SourceAuctionHistoryId",
                table: "DkpLedgerEntries",
                column: "SourceAuctionHistoryId",
                principalTable: "AuctionHistories",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
