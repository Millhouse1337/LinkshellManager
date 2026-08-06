using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddTreasuryLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JournalEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    LinkshellName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    EntryNumber = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    TransactionDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    TransactionKind = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: true),
                    Memo = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    ReversesJournalEntryId = table.Column<int>(type: "integer", nullable: true),
                    CorrectionReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    CreatedByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ConfirmedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConfirmedByAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    ConfirmedByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    SourceItemId = table.Column<int>(type: "integer", nullable: true),
                    SourceAuctionItemId = table.Column<int>(type: "integer", nullable: true),
                    SourceAuctionHistoryId = table.Column<int>(type: "integer", nullable: true),
                    LegacyRevenueEntryId = table.Column<int>(type: "integer", nullable: true),
                    LegacyEntryType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    LegacyCategory = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JournalEntries", x => x.Id);
                    table.CheckConstraint("CK_JournalEntries_ReasonRequiredForFixes", "\"Kind\" NOT IN ('Reversal', 'Correction') OR (\"CorrectionReason\" IS NOT NULL AND length(btrim(\"CorrectionReason\")) > 0)");
                    table.ForeignKey(
                        name: "FK_JournalEntries_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LedgerAccounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    AccountNumber = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    Description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    IsCash = table.Column<bool>(type: "boolean", nullable: false),
                    IsPostable = table.Column<bool>(type: "boolean", nullable: false),
                    IsSystem = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LedgerAccounts", x => x.Id);
                    table.CheckConstraint("CK_LedgerAccounts_AccountNumber_Range", "\"AccountNumber\" BETWEEN 1000 AND 5999");
                    table.ForeignKey(
                        name: "FK_LedgerAccounts_Linkshells_LinkshellId",
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
                    LockedThroughUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LockedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LockedByAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    LockedByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    UnlockedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UnlockedByAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    UnlockedByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    UnlockReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
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
                name: "JournalEntryLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    JournalEntryId = table.Column<int>(type: "integer", nullable: false),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    LedgerAccountId = table.Column<int>(type: "integer", nullable: false),
                    AccountNumber = table.Column<int>(type: "integer", nullable: false),
                    AccountName = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    Amount = table.Column<long>(type: "bigint", nullable: false),
                    TransactionDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LineNumber = table.Column<int>(type: "integer", nullable: false),
                    LineMemo = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CounterpartyAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    CounterpartyCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JournalEntryLines", x => x.Id);
                    table.CheckConstraint("CK_JournalEntryLines_LineNumber_Positive", "\"LineNumber\" >= 1");
                    table.ForeignKey(
                        name: "FK_JournalEntryLines_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_JournalEntryLines_LedgerAccounts_LedgerAccountId",
                        column: x => x.LedgerAccountId,
                        principalTable: "LedgerAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_LegacyRevenueEntryId",
                table: "JournalEntries",
                column: "LegacyRevenueEntryId",
                unique: true,
                filter: "\"LegacyRevenueEntryId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_LinkshellId_EntryNumber",
                table: "JournalEntries",
                columns: new[] { "LinkshellId", "EntryNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_LinkshellId_Sequence",
                table: "JournalEntries",
                columns: new[] { "LinkshellId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_LinkshellId_TransactionDate",
                table: "JournalEntries",
                columns: new[] { "LinkshellId", "TransactionDate" });

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_ReversesJournalEntryId",
                table: "JournalEntries",
                column: "ReversesJournalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntryLines_JournalEntryId",
                table: "JournalEntryLines",
                column: "JournalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntryLines_LedgerAccountId",
                table: "JournalEntryLines",
                column: "LedgerAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntryLines_LinkshellId_LedgerAccountId_TransactionDa~",
                table: "JournalEntryLines",
                columns: new[] { "LinkshellId", "LedgerAccountId", "TransactionDate" });

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntryLines_LinkshellId_TransactionDate",
                table: "JournalEntryLines",
                columns: new[] { "LinkshellId", "TransactionDate" });

            migrationBuilder.CreateIndex(
                name: "IX_LedgerAccounts_LinkshellId_AccountNumber",
                table: "LedgerAccounts",
                columns: new[] { "LinkshellId", "AccountNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LedgerAccounts_LinkshellId_Cash",
                table: "LedgerAccounts",
                column: "LinkshellId",
                unique: true,
                filter: "\"IsCash\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerAccounts_LinkshellId_SortOrder",
                table: "LedgerAccounts",
                columns: new[] { "LinkshellId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_LedgerPeriods_LinkshellId",
                table: "LedgerPeriods",
                column: "LinkshellId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JournalEntryLines");

            migrationBuilder.DropTable(
                name: "LedgerPeriods");

            migrationBuilder.DropTable(
                name: "JournalEntries");

            migrationBuilder.DropTable(
                name: "LedgerAccounts");
        }
    }
}
