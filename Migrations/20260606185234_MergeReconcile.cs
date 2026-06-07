using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class MergeReconcile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LockedToDiscordGuildId",
                table: "Linkshells",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LockedToDiscordGuildName",
                table: "Linkshells",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CanManageInvites",
                table: "LinkshellRoles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "GilAmount",
                table: "AuctionItems",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsSuperAdmin",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "AppSettings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Value = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppSettings", x => x.Key);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppSettings");

            migrationBuilder.DropColumn(
                name: "LockedToDiscordGuildId",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "LockedToDiscordGuildName",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "CanManageInvites",
                table: "LinkshellRoles");

            migrationBuilder.DropColumn(
                name: "GilAmount",
                table: "AuctionItems");

            migrationBuilder.DropColumn(
                name: "IsSuperAdmin",
                table: "AspNetUsers");
        }
    }
}
