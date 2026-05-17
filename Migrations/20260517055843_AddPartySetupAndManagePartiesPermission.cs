using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddPartySetupAndManagePartiesPermission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CanManageParties",
                table: "LinkshellRoles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Backfill: existing linkshells already have their system roles
            // seeded, and EnsureDefaultRoles only inserts missing roles (never
            // updates). Grant the new permission to the built-in Leader/Officer
            // roles so it works immediately without an admin re-toggling it.
            migrationBuilder.Sql(
                "UPDATE \"LinkshellRoles\" SET \"CanManageParties\" = true " +
                "WHERE \"Name\" IN ('Leader','Officer') AND \"IsSystem\" = true;");

            migrationBuilder.CreateTable(
                name: "PartySetups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AssignedMonsterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    CreatedByAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    CreatedByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PartySetups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PartySetups_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PartySetupAlliances",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PartySetupId = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PartySetupAlliances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PartySetupAlliances_PartySetups_PartySetupId",
                        column: x => x.PartySetupId,
                        principalTable: "PartySetups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PartySetupParties",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PartySetupAllianceId = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PartySetupParties", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PartySetupParties_PartySetupAlliances_PartySetupAllianceId",
                        column: x => x.PartySetupAllianceId,
                        principalTable: "PartySetupAlliances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PartySetupSlots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PartySetupPartyId = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    RequirementType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    MainJob = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    SubJob = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    Label = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PartySetupSlots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PartySetupSlots_PartySetupParties_PartySetupPartyId",
                        column: x => x.PartySetupPartyId,
                        principalTable: "PartySetupParties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PartySetupAlliances_PartySetupId_SortOrder",
                table: "PartySetupAlliances",
                columns: new[] { "PartySetupId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_PartySetupParties_PartySetupAllianceId_SortOrder",
                table: "PartySetupParties",
                columns: new[] { "PartySetupAllianceId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_PartySetups_LinkshellId_AssignedMonsterName",
                table: "PartySetups",
                columns: new[] { "LinkshellId", "AssignedMonsterName" });

            migrationBuilder.CreateIndex(
                name: "IX_PartySetups_LinkshellId_Name",
                table: "PartySetups",
                columns: new[] { "LinkshellId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_PartySetupSlots_PartySetupPartyId_SortOrder",
                table: "PartySetupSlots",
                columns: new[] { "PartySetupPartyId", "SortOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PartySetupSlots");

            migrationBuilder.DropTable(
                name: "PartySetupParties");

            migrationBuilder.DropTable(
                name: "PartySetupAlliances");

            migrationBuilder.DropTable(
                name: "PartySetups");

            migrationBuilder.DropColumn(
                name: "CanManageParties",
                table: "LinkshellRoles");
        }
    }
}
