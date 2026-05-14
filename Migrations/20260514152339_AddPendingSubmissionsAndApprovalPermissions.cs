using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingSubmissionsAndApprovalPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CanSubmitAttendanceForApproval",
                table: "LinkshellRoles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanSubmitTodForApproval",
                table: "LinkshellRoles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "PendingAttendanceSnapshotSubmissions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    SubmittedByAppUserId = table.Column<string>(type: "text", nullable: true),
                    SubmittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReviewNotes = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CapturedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CapturedByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    UtcOffset = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    EntryCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingAttendanceSnapshotSubmissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PendingAttendanceSnapshotSubmissions_AspNetUsers_SubmittedB~",
                        column: x => x.SubmittedByAppUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PendingAttendanceSnapshotSubmissions_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PendingAttendanceWindowSubmissions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    SubmittedByAppUserId = table.Column<string>(type: "text", nullable: true),
                    SubmittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReviewNotes = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    EventId = table.Column<int>(type: "integer", nullable: false),
                    WindowIndex = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingAttendanceWindowSubmissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PendingAttendanceWindowSubmissions_AspNetUsers_SubmittedByA~",
                        column: x => x.SubmittedByAppUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PendingAttendanceWindowSubmissions_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PendingAttendanceWindowSubmissions_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PendingTodSubmissions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    SubmittedByAppUserId = table.Column<string>(type: "text", nullable: true),
                    SubmittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReviewNotes = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    MonsterName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DayNumber = table.Column<int>(type: "integer", nullable: true),
                    Claim = table.Column<bool>(type: "boolean", nullable: true),
                    Time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Cooldown = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Interval = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    RepopTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ImagePath = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingTodSubmissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PendingTodSubmissions_AspNetUsers_SubmittedByAppUserId",
                        column: x => x.SubmittedByAppUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PendingTodSubmissions_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PendingAttendanceSnapshotEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PendingAttendanceSnapshotSubmissionId = table.Column<int>(type: "integer", nullable: false),
                    CharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    MainJob = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    MainJobLevel = table.Column<int>(type: "integer", nullable: true),
                    SubJob = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    SubJobLevel = table.Column<int>(type: "integer", nullable: true),
                    Zone = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingAttendanceSnapshotEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PendingAttendanceSnapshotEntries_PendingAttendanceSnapshotS~",
                        column: x => x.PendingAttendanceSnapshotSubmissionId,
                        principalTable: "PendingAttendanceSnapshotSubmissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PendingAttendanceWindowMemberSubmissions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PendingAttendanceWindowSubmissionId = table.Column<int>(type: "integer", nullable: false),
                    CharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    MainJob = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    MainJobLevel = table.Column<int>(type: "integer", nullable: true),
                    SubJob = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    SubJobLevel = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingAttendanceWindowMemberSubmissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PendingAttendanceWindowMemberSubmissions_PendingAttendanceW~",
                        column: x => x.PendingAttendanceWindowSubmissionId,
                        principalTable: "PendingAttendanceWindowSubmissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PendingTodLootSubmissions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PendingTodSubmissionId = table.Column<int>(type: "integer", nullable: false),
                    ItemName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ItemWinner = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    WinningDkpSpent = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingTodLootSubmissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PendingTodLootSubmissions_PendingTodSubmissions_PendingTodS~",
                        column: x => x.PendingTodSubmissionId,
                        principalTable: "PendingTodSubmissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PendingAttendanceSnapshotEntries_PendingAttendanceSnapshotS~",
                table: "PendingAttendanceSnapshotEntries",
                column: "PendingAttendanceSnapshotSubmissionId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingAttendanceSnapshotSubmissions_LinkshellId",
                table: "PendingAttendanceSnapshotSubmissions",
                column: "LinkshellId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingAttendanceSnapshotSubmissions_SubmittedByAppUserId",
                table: "PendingAttendanceSnapshotSubmissions",
                column: "SubmittedByAppUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingAttendanceWindowMemberSubmissions_PendingAttendanceW~",
                table: "PendingAttendanceWindowMemberSubmissions",
                column: "PendingAttendanceWindowSubmissionId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingAttendanceWindowSubmissions_EventId",
                table: "PendingAttendanceWindowSubmissions",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingAttendanceWindowSubmissions_LinkshellId",
                table: "PendingAttendanceWindowSubmissions",
                column: "LinkshellId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingAttendanceWindowSubmissions_SubmittedByAppUserId",
                table: "PendingAttendanceWindowSubmissions",
                column: "SubmittedByAppUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingTodLootSubmissions_PendingTodSubmissionId",
                table: "PendingTodLootSubmissions",
                column: "PendingTodSubmissionId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingTodSubmissions_LinkshellId",
                table: "PendingTodSubmissions",
                column: "LinkshellId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingTodSubmissions_SubmittedByAppUserId",
                table: "PendingTodSubmissions",
                column: "SubmittedByAppUserId");

            // Backfill: every existing system Leader / Officer rank gets the
            // two new submit-for-approval perms set to true so the migration
            // doesn't silently disable a previously-working privilege the
            // moment the new code starts gating on them.
            migrationBuilder.Sql(@"
                UPDATE ""LinkshellRoles""
                SET ""CanSubmitTodForApproval"" = true,
                    ""CanSubmitAttendanceForApproval"" = true
                WHERE ""Name"" IN ('Leader', 'Officer') AND ""IsSystem"" = true;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PendingAttendanceSnapshotEntries");

            migrationBuilder.DropTable(
                name: "PendingAttendanceWindowMemberSubmissions");

            migrationBuilder.DropTable(
                name: "PendingTodLootSubmissions");

            migrationBuilder.DropTable(
                name: "PendingAttendanceSnapshotSubmissions");

            migrationBuilder.DropTable(
                name: "PendingAttendanceWindowSubmissions");

            migrationBuilder.DropTable(
                name: "PendingTodSubmissions");

            migrationBuilder.DropColumn(
                name: "CanSubmitAttendanceForApproval",
                table: "LinkshellRoles");

            migrationBuilder.DropColumn(
                name: "CanSubmitTodForApproval",
                table: "LinkshellRoles");
        }
    }
}
