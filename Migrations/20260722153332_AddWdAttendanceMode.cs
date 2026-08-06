using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddWdAttendanceMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HnmAttendanceMode",
                table: "Linkshells",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Standard");

            migrationBuilder.AddColumn<double>(
                name: "WdClaimBonus",
                table: "Linkshells",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "WdDkpPerWindow",
                table: "Linkshells",
                type: "double precision",
                nullable: false,
                defaultValue: 0.25);

            migrationBuilder.AddColumn<double>(
                name: "WdKillBonus",
                table: "Linkshells",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<int>(
                name: "WdProcessingGraceMinutes",
                table: "Linkshells",
                type: "integer",
                nullable: false,
                defaultValue: 15);

            migrationBuilder.AddColumn<string>(
                name: "AttendanceMode",
                table: "Events",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "WdAwaitingProcessingSince",
                table: "Events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WdClaimed",
                table: "Events",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "WdFinalizedAt",
                table: "Events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WdKilled",
                table: "Events",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "WdArrivalWindow",
                table: "AppUserEvents",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LinkshellHnmWindowConfigs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    MonsterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    WindowCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LinkshellHnmWindowConfigs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LinkshellHnmWindowConfigs_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LinkshellHnmWindowConfigs_LinkshellId_MonsterName",
                table: "LinkshellHnmWindowConfigs",
                columns: new[] { "LinkshellId", "MonsterName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LinkshellHnmWindowConfigs");

            migrationBuilder.DropColumn(
                name: "HnmAttendanceMode",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "WdClaimBonus",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "WdDkpPerWindow",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "WdKillBonus",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "WdProcessingGraceMinutes",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "AttendanceMode",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "WdAwaitingProcessingSince",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "WdClaimed",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "WdFinalizedAt",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "WdKilled",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "WdArrivalWindow",
                table: "AppUserEvents");
        }
    }
}
