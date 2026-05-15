using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddWindowEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AttendanceSnapshots_LinkshellId",
                table: "AttendanceSnapshots");

            migrationBuilder.AddColumn<int>(
                name: "DuplicateOfSnapshotId",
                table: "AttendanceSnapshots",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SnapshotStatus",
                table: "AttendanceSnapshots",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Active");

            migrationBuilder.AddColumn<int>(
                name: "WindowEventId",
                table: "AttendanceSnapshots",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WindowEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    NormalizedName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Open"),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FirstCapturedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastCapturedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClosedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WindowEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WindowEvents_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceSnapshots_DuplicateOfSnapshotId",
                table: "AttendanceSnapshots",
                column: "DuplicateOfSnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceSnapshots_LinkshellId_CapturedAtUtc",
                table: "AttendanceSnapshots",
                columns: new[] { "LinkshellId", "CapturedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceSnapshots_WindowEventId",
                table: "AttendanceSnapshots",
                column: "WindowEventId");

            migrationBuilder.CreateIndex(
                name: "IX_WindowEvents_LinkshellId_LastCapturedAtUtc",
                table: "WindowEvents",
                columns: new[] { "LinkshellId", "LastCapturedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WindowEvents_LinkshellId_Status_NormalizedName",
                table: "WindowEvents",
                columns: new[] { "LinkshellId", "Status", "NormalizedName" });

            migrationBuilder.AddForeignKey(
                name: "FK_AttendanceSnapshots_AttendanceSnapshots_DuplicateOfSnapshot~",
                table: "AttendanceSnapshots",
                column: "DuplicateOfSnapshotId",
                principalTable: "AttendanceSnapshots",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_AttendanceSnapshots_WindowEvents_WindowEventId",
                table: "AttendanceSnapshots",
                column: "WindowEventId",
                principalTable: "WindowEvents",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AttendanceSnapshots_AttendanceSnapshots_DuplicateOfSnapshot~",
                table: "AttendanceSnapshots");

            migrationBuilder.DropForeignKey(
                name: "FK_AttendanceSnapshots_WindowEvents_WindowEventId",
                table: "AttendanceSnapshots");

            migrationBuilder.DropTable(
                name: "WindowEvents");

            migrationBuilder.DropIndex(
                name: "IX_AttendanceSnapshots_DuplicateOfSnapshotId",
                table: "AttendanceSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_AttendanceSnapshots_LinkshellId_CapturedAtUtc",
                table: "AttendanceSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_AttendanceSnapshots_WindowEventId",
                table: "AttendanceSnapshots");

            migrationBuilder.DropColumn(
                name: "DuplicateOfSnapshotId",
                table: "AttendanceSnapshots");

            migrationBuilder.DropColumn(
                name: "SnapshotStatus",
                table: "AttendanceSnapshots");

            migrationBuilder.DropColumn(
                name: "WindowEventId",
                table: "AttendanceSnapshots");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceSnapshots_LinkshellId",
                table: "AttendanceSnapshots",
                column: "LinkshellId");
        }
    }
}
