using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddWindowEventAttInputRowRange : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FirstAttInputRowNumber",
                table: "WindowEvents",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AttInputRowCount",
                table: "WindowEvents",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FirstAttInputRowNumber",
                table: "WindowEvents");

            migrationBuilder.DropColumn(
                name: "AttInputRowCount",
                table: "WindowEvents");
        }
    }
}
