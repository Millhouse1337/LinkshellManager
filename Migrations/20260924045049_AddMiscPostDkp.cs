using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddMiscPostDkp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "HnmStandardMiscBonus",
                table: "Linkshells",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            // Misc posts were priced at the regular window rate until now. Start every linkshell's
            // new Misc rate there, so no misc post re-prices itself the moment this deploys.
            migrationBuilder.Sql(
                "UPDATE \"Linkshells\" SET \"HnmStandardMiscBonus\" = \"HnmStandardWindowBonus\";");

            migrationBuilder.AddColumn<double>(
                name: "DkpAmount",
                table: "AttendanceSnapshots",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HnmStandardMiscBonus",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "DkpAmount",
                table: "AttendanceSnapshots");
        }
    }
}
