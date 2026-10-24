using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddManageAttendanceWithAddonPermission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PendingAttendanceSnapshotEntries");

            migrationBuilder.DropTable(
                name: "PendingAttendanceWindowMemberSubmissions");

            migrationBuilder.DropTable(
                name: "PendingAttendanceSnapshotSubmissions");

            migrationBuilder.DropTable(
                name: "PendingAttendanceWindowSubmissions");

            // NOT a rename, which is what EF scaffolded (two bools, one gone and one new). The old
            // flag meant "may queue attendance for approval"; the new one means "may manage
            // attendance from the addon", which used to be CanModerateLiveEvent. Every role keeps
            // exactly the addon access it had on the deploy that splits the two.
            migrationBuilder.DropColumn(
                name: "CanSubmitAttendanceForApproval",
                table: "LinkshellRoles");

            migrationBuilder.AddColumn<bool>(
                name: "CanManageAttendanceWithAddon",
                table: "LinkshellRoles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql(
                @"UPDATE ""LinkshellRoles"" SET ""CanManageAttendanceWithAddon"" = ""CanModerateLiveEvent"";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CanManageAttendanceWithAddon",
                table: "LinkshellRoles");

            // The defaults granted it to Leader and Officer, the roles that hold CanModerateLiveEvent.
            migrationBuilder.AddColumn<bool>(
                name: "CanSubmitAttendanceForApproval",
                table: "LinkshellRoles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql(
                @"UPDATE ""LinkshellRoles"" SET ""CanSubmitAttendanceForApproval"" = ""CanModerateLiveEvent"";");

            migrationBuilder.CreateTable(
                name: "PendingAttendanceSnapshotSubmissions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkedEventId = table.Column<int>(type: "integer", nullable: true),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    SubmittedByAppUserId = table.Column<string>(type: "text", nullable: true),
                    CapturedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CapturedByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EntryCount = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ReviewNotes = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    SubmittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UtcOffset = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true)
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
                        name: "FK_PendingAttendanceSnapshotSubmissions_Events_LinkedEventId",
                        column: x => x.LinkedEventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
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
                    EventId = table.Column<int>(type: "integer", nullable: false),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    SubmittedByAppUserId = table.Column<string>(type: "text", nullable: true),
                    ReviewNotes = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    SubmittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
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

            migrationBuilder.CreateIndex(
                name: "IX_PendingAttendanceSnapshotEntries_PendingAttendanceSnapshotS~",
                table: "PendingAttendanceSnapshotEntries",
                column: "PendingAttendanceSnapshotSubmissionId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingAttendanceSnapshotSubmissions_LinkedEventId",
                table: "PendingAttendanceSnapshotSubmissions",
                column: "LinkedEventId");

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
        }
    }
}
