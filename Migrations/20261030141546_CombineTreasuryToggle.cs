using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class CombineTreasuryToggle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // One Treasury switch replaces the Items and Finances (EnableRevenue) pair. It stays on
            // wherever either half was on, so nobody loses a half of the tab they were using.
            migrationBuilder.Sql(
                "UPDATE \"Linkshells\" SET \"EnableRevenue\" = \"EnableRevenue\" OR \"EnableItems\";");

            migrationBuilder.DropColumn(
                name: "EnableItems",
                table: "Linkshells");

            migrationBuilder.RenameColumn(
                name: "EnableRevenue",
                table: "Linkshells",
                newName: "EnableTreasury");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "EnableTreasury",
                table: "Linkshells",
                newName: "EnableRevenue");

            // true, not EF's generated false: the column's own default when it existed.
            migrationBuilder.AddColumn<bool>(
                name: "EnableItems",
                table: "Linkshells",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            // Both halves follow the one switch, so a linkshell that had Treasury off keeps it off.
            migrationBuilder.Sql(
                "UPDATE \"Linkshells\" SET \"EnableItems\" = \"EnableRevenue\";");
        }
    }
}
