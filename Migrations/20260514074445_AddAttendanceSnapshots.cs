using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddAttendanceSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AttendanceSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    CapturedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CapturedByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    UtcOffset = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    EntryCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttendanceSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AttendanceSnapshots_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AttendanceSnapshotEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SnapshotId = table.Column<int>(type: "integer", nullable: false),
                    CharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    MainJob = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    MainJobLevel = table.Column<int>(type: "integer", nullable: true),
                    SubJob = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    SubJobLevel = table.Column<int>(type: "integer", nullable: true),
                    Zone = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttendanceSnapshotEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AttendanceSnapshotEntries_AttendanceSnapshots_SnapshotId",
                        column: x => x.SnapshotId,
                        principalTable: "AttendanceSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceSnapshotEntries_SnapshotId",
                table: "AttendanceSnapshotEntries",
                column: "SnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceSnapshots_LinkshellId",
                table: "AttendanceSnapshots",
                column: "LinkshellId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AttendanceSnapshotEntries");

            migrationBuilder.DropTable(
                name: "AttendanceSnapshots");
        }
    }
}
