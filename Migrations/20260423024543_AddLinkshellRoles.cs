using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddLinkshellRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LinkshellRoles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IsSystem = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CanManageRoles = table.Column<bool>(type: "boolean", nullable: false),
                    CanManageMembers = table.Column<bool>(type: "boolean", nullable: false),
                    CanManageEvents = table.Column<bool>(type: "boolean", nullable: false),
                    CanModerateLiveEvent = table.Column<bool>(type: "boolean", nullable: false),
                    CanAddLoot = table.Column<bool>(type: "boolean", nullable: false),
                    CanManageInventory = table.Column<bool>(type: "boolean", nullable: false),
                    CanManageTreasury = table.Column<bool>(type: "boolean", nullable: false),
                    CanManageRules = table.Column<bool>(type: "boolean", nullable: false),
                    CanManageAnnouncements = table.Column<bool>(type: "boolean", nullable: false),
                    CanManageTods = table.Column<bool>(type: "boolean", nullable: false),
                    CanAuditDkp = table.Column<bool>(type: "boolean", nullable: false),
                    CanManageAuctions = table.Column<bool>(type: "boolean", nullable: false),
                    CanCustomizeLinkshell = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LinkshellRoles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LinkshellRoles_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LinkshellRoles_LinkshellId_Name",
                table: "LinkshellRoles",
                columns: new[] { "LinkshellId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LinkshellRoles");
        }
    }
}
